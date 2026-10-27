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
/// keeps no record of the writes.
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

    private readonly SidChip _chip = new();
    private readonly SidTune _tune;
    private long _chipCycle;
    private bool _silent;
    private short[] _pending = new short[8192];
    private int _pendingCount;

    private readonly float[][] _voiceScopes = [new float[RingSamples], new float[RingSamples], new float[RingSamples]];
    private readonly float[] _mixScope = new float[RingSamples];
    private long _scopeWritten;
    private readonly object _scopeLock = new();

    /// <param name="song">The subtune, counting from 1.</param>
    public C64PlaybackEngine(SidFile file, int song)
    {
        _tune = SidTune.Load(file, Math.Clamp(song, 1, Math.Max(1, file.Songs)), this, record: false);
        File = file;
        Song = _tune.Song;
        ClockHz = Machines.ClockHz(_tune.Computer.Machine);
        _chip.SetChipModel(file.Is8580 ? ChipModel.Mos8580 : ChipModel.Mos6581);
        _chip.SetSamplingParameters(ClockHz, SamplingMethod.Interpolate, SampleRate);

        // Init, unheard, up to the first call - or two frames, for a tune that plays without - and
        // the chip let settle from the volume it set, so the music starts without a thump.
        _silent = true;
        long until = 2L * Machines.CyclesPerFrame(_tune.Computer.Machine);
        while (_tune.Computer.Calls.Count == 0 && _tune.Computer.Cycle < until)
            _tune.Run(64);
        ClockTo(_tune.Computer.Cycle);
        _chip.SettleStartupTransient(ClockHz);
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

    public void SetVoiceMuted(int voice, bool muted) => _chip.SetVoiceMuted(voice, muted);

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

    void ISidPort.Write(long cycle, int register, int value)
    {
        ClockTo(cycle);
        _chip.Write(register, value);
    }

    byte ISidPort.Read(long cycle, int register)
    {
        ClockTo(cycle);
        return (byte)_chip.Read(register);
    }

    /// <summary>The chip clocked on to <paramref name="cycle"/>, a sample at a time so each voice's
    /// own output can be read at the instant its sample is taken, as the scopes draw it.</summary>
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
            if (_chip.ClockAndSample(ref cycles, _pending, _pendingCount, 1) == 0)
                break;
            if (_silent)
                continue;
            Capture(_pending[_pendingCount]);
            _pendingCount++;
        }
    }

    private void Capture(short mix)
    {
        lock (_scopeLock)
        {
            int at = (int)(_scopeWritten % RingSamples);
            for (int voice = 0; voice < 3; voice++)
                _voiceScopes[voice][at] = _chip.Voice(voice).Output() / VoiceOutputScale;
            _mixScope[at] = mix / 32768f;
            _scopeWritten++;
        }
    }

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
