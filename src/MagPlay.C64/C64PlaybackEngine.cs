using MagPlay.Score;
using MagSid.Playback;
using System.Runtime.InteropServices;
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

    /// <summary>How loud a fast-forward is: quieter than the tune, as tape winding on is.</summary>
    private const float FastForwardVolume = 0.3f;

    private readonly SidChip[] _chips;
    private readonly SidTune _tune;
    private long _chipCycle;
    private bool _silent;
    private short[] _pending = new short[8192];
    private int _pendingCount;

    private readonly float[][] _voiceScopes;
    private readonly float[] _mixScope = new float[RingSamples];
    private readonly int[][] _voiceStates;
    private readonly int[][] _registers;
    private readonly short[] _extraSample = new short[1];
    private long _scopeWritten;
    private readonly object _scopeLock = new();

    /// <summary>Where the output went silent for a pause, or on again, or changed speed: the device's
    /// sample and the tune's there, and the tune's samples to each of the device's from there on -
    /// 0 while silent, 1 as it plays, more while it fast-forwards.</summary>
    private readonly List<(long Device, long Tune, int Rate)> _turns = [(0, 0, 1)];
    private long _device;
    private long _tuneSample;

    /// <param name="song">The subtune, counting from 1.</param>
    public C64PlaybackEngine(SidFile file, int song)
    {
        int chips = 1 + Math.Min(2, file.ExtraSids.Count);
        _chips = new SidChip[chips];
        _registers = new int[chips][];
        _voiceStates = new int[3 * chips][];
        _voiceScopes = new float[3 * chips][];
        for (int voice = 0; voice < _voiceStates.Length; voice++)
        {
            _voiceStates[voice] = new int[RingSamples];
            _voiceScopes[voice] = new float[RingSamples];
        }

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

    /// <summary>How many times its own speed the tune plays: 1, or more to fast-forward - the C64 runs
    /// that much faster, heard higher and quieter, and every view of it moves as fast.</summary>
    public int Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 1, 8);
    }

    private volatile int _speed = 1;

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    /// <summary>The tune's own sample the device is playing at its sample <paramref name="device"/>:
    /// the samples of silence a pause gave the device are not the tune's, so a view following this
    /// stands still while paused - and runs ahead as fast as the tune while it fast-forwards.</summary>
    public long TuneSampleAt(long device)
    {
        lock (_turns)
        {
            int at = _turns.FindLastIndex(t => t.Device <= device);
            var (from, tune, rate) = _turns[Math.Max(0, at)];
            return tune + Math.Max(0, device - from) * rate;
        }
    }

    /// <summary>The C64's cycle at sample <paramref name="sample"/>.</summary>
    public long CycleAt(double sample) => StartCycle + (long)(sample * ClockHz / SampleRate);

    /// <summary>The SID chips the tune plays on: 1, or 2 or 3 for a 2SID or 3SID tune.</summary>
    public int Chips => _chips.Length;

    /// <summary>A voice muted: 0-2 the first chip's, 3-5 the second's, 6-8 the third's.</summary>
    public void SetVoiceMuted(int voice, bool muted)
    {
        if (voice >= 0 && voice < 3 * _chips.Length)
            _chips[voice / 3].SetVoiceMuted(voice % 3, muted);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int wanted = count / 2;
        int rate = Paused || Failure is not null ? 0 : _speed;
        lock (_turns)
        {
            if (_turns[^1].Rate != rate)
                _turns.Add((_device, _tuneSample, rate));
            if (_turns.Count > 256)
                _turns.RemoveRange(0, _turns.Count - 256);
            _device += wanted;
            _tuneSample += (long)wanted * rate;
        }

        if (rate == 0)
        {
            Array.Clear(buffer, offset, wanted * 2);
            return wanted * 2;
        }

        int needed = wanted * rate;
        try
        {
            while (_pendingCount < needed)
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

        // Fast-forwarding, each sample out is the average of as many of the tune's - its pitch goes
        // up as tape's does, without the grit of samples dropped - at the fast-forward's volume.
        int ready = Math.Min(wanted, _pendingCount / rate);
        var output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, wanted * 2));
        if (rate == 1)
            _pending.AsSpan(0, ready).CopyTo(output);
        else
            for (int i = 0; i < ready; i++)
            {
                int sum = 0;
                for (int j = 0; j < rate; j++)
                    sum += _pending[i * rate + j];
                output[i] = (short)(sum * FastForwardVolume / rate);
            }
        output[ready..].Clear();

        int used = ready * rate;
        Array.Copy(_pending, used, _pending, 0, _pendingCount - used);
        _pendingCount -= used;
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
                _voiceScopes[voice][at] = chip.Voice(voice % 3).Output() / VoiceOutputScale;
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
