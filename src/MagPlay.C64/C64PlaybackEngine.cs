using MagPlay.Score;
using MagSid.Playback;
using MagSid.Sid;
using NAudio.Wave;

namespace MagPlay.C64;

/// <summary>
/// Plays a <c>.sid</c> on the <see cref="C64"/>: the tune runs as it would on the machine - PSID or
/// RSID, on the raster or a CIA timer, multispeed, digis from an NMI - and the SID is clocked up to
/// each write's own cycle before the write, so every write sounds where the 6510 made it. The C64
/// runs a little ahead of the sound card, a slice at a time, for as long as anyone listens; it
/// keeps no record of the writes. A 2SID or 3SID tune plays on as many chips, clocked together and
/// mixed into one.
///
/// The cycles count from the C64's start, as a <see cref="SidTune"/> recorded for a transcription
/// counts them, so a view of the transcription follows this by <see cref="CycleAt"/>.
/// </summary>
public sealed class C64PlaybackEngine : IWaveProvider, IScopeSource, ISidPort
{
    public const int SampleRate = 44100;

    /// <summary>Cycles the C64 runs at a time: two milliseconds, well inside one audio buffer.</summary>
    private const int Slice = 2000;

    /// <summary>Half a voice's peak-to-peak swing, bringing a scope trace to [-1, 1] - as
    /// <see cref="SidFilePlaybackEngine"/> scales it.</summary>
    private const float VoiceOutputScale = 2048f * 255f;

    /// <summary>Samples of history the scope rings keep: 1.5 seconds, past any device's latency.</summary>
    private const int RingSamples = 65536;

    /// <summary>What the mix of several chips is scaled by, so two or three at full volume do not
    /// clip where one would not: by chip count.</summary>
    private static readonly double[] MixScale = [0, 1, 0.7, 0.55];

    private readonly SidChip[] _chips;
    private readonly SidTune _tune;
    private long _chipCycle;
    private bool _silent;
    private short[] _pending = new short[8192];
    private int _pendingCount;

    private readonly float[][] _voiceScopes = [new float[RingSamples], new float[RingSamples], new float[RingSamples]];
    private readonly float[] _mixScope = new float[RingSamples];
    private readonly int[][] _voiceStates;
    private readonly int[][] _registers;
    private readonly short[] _extraSample = new short[1];
    private long _scopeWritten;
    private readonly object _scopeLock = new();

    /// <param name="song">The subtune, counting from 1.</param>
    public C64PlaybackEngine(SidFile file, int song)
    {
        int chips = 1 + Math.Min(2, file.ExtraSids.Count);
        _chips = new SidChip[chips];
        _registers = new int[chips][];
        _voiceStates = new int[3 * chips][];
        for (int voice = 0; voice < _voiceStates.Length; voice++)
            _voiceStates[voice] = new int[RingSamples];

        _tune = SidTune.Load(file, Math.Clamp(song, 1, Math.Max(1, file.Songs)), this, record: false);
        File = file;
        Song = _tune.Song;
        ClockHz = Machines.ClockHz(_tune.Computer.Machine);
        for (int chip = 0; chip < chips; chip++)
        {
            bool is8580 = chip == 0 ? file.Is8580 : file.ExtraSids8580[chip - 1];
            _chips[chip] = new SidChip();
            _chips[chip].SetChipModel(is8580 ? ChipModel.Mos8580 : ChipModel.Mos6581);
            _chips[chip].SetSamplingParameters(ClockHz, SamplingMethod.Interpolate, SampleRate);
            _registers[chip] = new int[0x19];
        }

        // Init, unheard, up to the first call - or two frames, for a tune that plays without - and
        // the chip let settle from the volume it set, so the music starts without a thump.
        _silent = true;
        long until = 2L * Machines.CyclesPerFrame(_tune.Computer.Machine);
        while (_tune.Computer.Calls.Count == 0 && _tune.Computer.Cycle < until)
            _tune.Run(64);
        ClockTo(_tune.Computer.Cycle);
        foreach (var chip in _chips)
            chip.SettleStartupTransient(ClockHz);
        _silent = false;
        StartCycle = _chipCycle;
    }

    public SidFile File { get; }

    public int Song { get; }

    /// <summary>The C64's clock: PAL's or NTSC's.</summary>
    public double ClockHz { get; }

    /// <summary>The C64's cycle the first sample is taken on.</summary>
    public long StartCycle { get; }

    /// <summary>Why the tune stopped, if it did - it ran into something that is not code.</summary>
    public string? Failure { get; private set; }

    public bool Paused { get; set; }

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    /// <summary>The C64's cycle at sample <paramref name="sample"/>.</summary>
    public long CycleAt(double sample) => StartCycle + (long)(sample * ClockHz / SampleRate);

    /// <summary>The SID chips the tune plays on: 1, or 2 or 3 for a 2SID or 3SID tune.</summary>
    public int Chips => _chips.Length;

    /// <summary>A voice muted - on every chip, the voice of each in its place.</summary>
    public void SetVoiceMuted(int voice, bool muted)
    {
        foreach (var chip in _chips)
            chip.SetVoiceMuted(voice, muted);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int wanted = count / 2;
        if (Paused || Failure is not null)
        {
            Array.Clear(buffer, offset, wanted * 2);
            return wanted * 2;
        }

        try
        {
            while (_pendingCount < wanted)
            {
                _tune.Run(Slice);
                ClockTo(_tune.Computer.Cycle);
            }
        }
        catch (Exception ex)
        {
            // The audio thread must not die with the tune: what it played stays, then silence.
            Failure = ex.Message;
        }

        int ready = Math.Min(wanted, _pendingCount);
        Buffer.BlockCopy(_pending, 0, buffer, offset, ready * 2);
        Array.Clear(buffer, offset + ready * 2, (wanted - ready) * 2);
        Array.Copy(_pending, ready, _pending, 0, _pendingCount - ready);
        _pendingCount -= ready;
        return wanted * 2;
    }

    void ISidPort.Write(long cycle, int chip, int register, int value)
    {
        if (chip >= _chips.Length)
            return;
        ClockTo(cycle);
        _chips[chip].Write(register, value);
        _registers[chip][register] = value & 0xFF;
    }

    byte ISidPort.Read(long cycle, int chip, int register)
    {
        if (chip >= _chips.Length)
            return 0;
        ClockTo(cycle);
        return (byte)_chips[chip].Read(register);
    }

    /// <summary>The chips clocked on to <paramref name="cycle"/>, a sample at a time so each voice's
    /// own output can be read at the instant its sample is taken, as the scopes draw it. The other
    /// chips take the cycles the first took for its sample, so theirs fall on the same instant.</summary>
    private void ClockTo(long cycle)
    {
        int cycles = (int)Math.Min(int.MaxValue, cycle - _chipCycle);
        if (cycles <= 0)
            return;
        _chipCycle = cycle;

        while (cycles > 0)
        {
            if (_pendingCount == _pending.Length)
                Array.Resize(ref _pending, _pending.Length * 2);
            int before = cycles;
            int sampled = _chips[0].ClockAndSample(ref cycles, _pending, _pendingCount, 1);
            if (_chips.Length > 1)
                MixIn(before - cycles, sampled);
            if (sampled == 0)
                break;
            if (_silent)
                continue;
            Capture(_pending[_pendingCount]);
            _pendingCount++;
        }
    }

    /// <summary>The other chips through the same <paramref name="cycles"/>, their sample added to the
    /// first's, the sum scaled for the number of chips.</summary>
    private void MixIn(int cycles, int sampled)
    {
        int sum = sampled > 0 ? _pending[_pendingCount] : 0;
        for (int chip = 1; chip < _chips.Length; chip++)
        {
            int left = cycles;
            if (_chips[chip].ClockAndSample(ref left, _extraSample, 0, 1) > 0)
                sum += _extraSample[0];
        }
        if (sampled > 0)
            _pending[_pendingCount] = (short)Math.Clamp(sum * MixScale[_chips.Length], short.MinValue, short.MaxValue);
    }

    private void Capture(short mix)
    {
        lock (_scopeLock)
        {
            int at = (int)(_scopeWritten % RingSamples);
            for (int voice = 0; voice < _voiceStates.Length; voice++)
            {
                var chip = _chips[voice / 3];
                var registers = _registers[voice / 3];
                int b = voice % 3 * 7;
                if (voice < 3)
                    _voiceScopes[voice][at] = chip.Voice(voice).Output() / VoiceOutputScale;
                _voiceStates[voice][at] = registers[b] | (registers[b + 1] << 8) | (registers[b + 4] << 16)
                                          | (chip.Voice(voice % 3).Envelope.Output() << 24);
            }
            _mixScope[at] = mix / 32768f;
            _scopeWritten++;
        }
    }

    /// <summary>What a voice is doing at sample <paramref name="at"/> - the one the listener hears:
    /// its frequency register, control byte and envelope level, 0-255. Voices 3-5 are the second
    /// chip's, 6-8 the third's.</summary>
    public (int Frequency, int Control, int Envelope) VoiceAt(int voice, long at)
    {
        lock (_scopeLock)
        {
            if (_scopeWritten == 0)
                return default;
            long sample = Math.Clamp(at, Math.Max(0, _scopeWritten - RingSamples), _scopeWritten - 1);
            int state = _voiceStates[voice][(int)(sample % RingSamples)];
            return (state & 0xFFFF, (state >> 16) & 0xFF, (state >> 24) & 0xFF);
        }
    }

    /// <summary>The note a frequency register plays on this machine, C-0 as 0 - fractional, between keys.</summary>
    public double NoteOf(int frequency) =>
        frequency <= 0 ? double.NaN : 12 * Math.Log2(frequency * ClockHz / 16777216.0 / 16.3516);

    public void CaptureVoiceScope(int voice, float[] destination, long endingAt) =>
        CaptureRing(_voiceScopes[voice], destination, endingAt);

    public void CaptureMix(float[] destination, long endingAt) => CaptureRing(_mixScope, destination, endingAt);

    /// <summary>The window of <paramref name="source"/> ending at <paramref name="endingAt"/>, never
    /// past what is written nor further back than the ring holds; silence before a window exists.</summary>
    private void CaptureRing(float[] source, float[] destination, long endingAt)
    {
        int length = destination.Length;
        lock (_scopeLock)
        {
            long end = Math.Min(endingAt, _scopeWritten);
            end = Math.Max(end, Math.Min(_scopeWritten, length));
            end = Math.Max(end, _scopeWritten - RingSamples + length);
            long start = end - length;
            if (start < 0)
            {
                Array.Clear(destination);
                return;
            }

            int from = (int)(start % RingSamples);
            int first = Math.Min(length, RingSamples - from);
            Array.Copy(source, from, destination, 0, first);
            if (first < length)
                Array.Copy(source, 0, destination, first, length - first);
        }
    }
}
