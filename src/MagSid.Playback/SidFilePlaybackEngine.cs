using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Playback;

/// <summary>
/// Plays a <c>.sid</c> file by running its own 6502 code.
///
/// This is what replaced launching VICE's vsid.exe, and it is not merely the same thing without the
/// dependency. <see cref="SongPlaybackEngine"/> plays what the song <em>means</em>, through a C# port
/// of musicplayer.s; this plays the bytes that were actually written to the file, by executing them.
/// So it answers a different question - "is the file I just exported right?" - and the two agreeing is
/// evidence neither gives alone. SidFilePlaybackTests leans on exactly that.
///
/// The loop is <see cref="SongPlaybackEngine"/>'s, with <c>cpu.Call(play)</c> where that one calls the
/// sequencer: register writes are interleaved at frame boundaries rather than issued up front, so a
/// note lands on the right SID cycle relative to the audio around it.
/// </summary>
public sealed class SidFilePlaybackEngine : IWaveProvider
{
    private const double ClockFrequency = PlayRate.PalClock;

    public const int SampleRate = 44100;

    /// <summary>The SID answers every 32 bytes from $D400 to $D7FF. Players write through the mirrors
    /// as readily as the base address, so the whole range is routed.</summary>
    private const int SidBase = 0xD400;
    private const int SidTop = 0xD7FF;

    private readonly SidChip _sid = new();
    private readonly Mos6502 _cpu;
    private readonly int _playAddress;

    /// <summary>The tune has set CIA 1's timer latch - see PlayRate.</summary>
    private bool _timerSet;

    /// <summary>How many times play has been called - one a frame, or one a timer period for a
    /// tune on CIA 1.</summary>
    public long PlayCalls { get; private set; }

    private double _totalCyclesClocked;
    private double _nextFrameBoundaryCycles;
    private long _samplesProduced;

    /// <summary>How many samples one capture returns. 2048 at 44.1kHz is 46ms - a couple of cycles
    /// of a bass note, which is what makes a scope trace readable - and a power of two, which the
    /// spectrum's FFT needs.</summary>
    public const int ScopeSamples = 2048;

    /// <summary>
    /// How much history the rings keep: 1.5 seconds.
    ///
    /// It has to be longer than the output device's latency, and by a wide margin. Read() runs as
    /// far ahead as the buffer is deep - WaveOutEvent defaults to 300ms - so the samples a listener
    /// is hearing right now were written that long ago. A ring only as long as one capture would
    /// have overwritten them before anything could draw them, which is what made the scopes look
    /// frozen: they were showing the render frontier, refreshed in bursts about ten times a second,
    /// rather than the music.
    /// </summary>
    private const int RingSamples = 65536;

    // Written on the audio thread, read on the UI thread. Plain circular buffers behind one lock,
    // as elsewhere in this project: the critical section is a copy of a few KB and the alternative,
    // a lock-free ring, buys nothing at 60 reads a second.
    private readonly float[][] _voiceScopes =
        [new float[RingSamples], new float[RingSamples], new float[RingSamples]];

    private readonly float[] _mixScope = new float[RingSamples];

    /// <summary>The absolute index of the next sample to be written, so a caller holding a play
    /// position can ask for the window that ends there.</summary>
    private long _scopeWritten;
    private readonly object _scopeLock = new();

    /// <summary>Half a voice's peak-to-peak swing, as a divisor bringing a trace to [-1, 1].
    ///
    /// Voice.Output()'s own comment gives the range as [-2048*255, 2047*255], which holds for the
    /// 8580 but not the 6581: that model carries a waveform-DAC zero of $380 and an envelope DAC
    /// offset of $800*$FF, so its output never goes negative at all. The swing is the same either
    /// way - it is centred differently - so the scale is right for both and the offset is what a
    /// scope removes, exactly as AC coupling does on real hardware. See VoiceScopeView.</summary>
    private const float VoiceOutputScale = 2048f * 255f;

    private bool _paused;

    /// <summary>Silences output without tearing the tune down, so resuming carries on rather than
    /// restarting. The CPU stops being called, so the song stops advancing too.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    public long SamplesProduced => Interlocked.Read(ref _samplesProduced);

    public SidFile File { get; }

    /// <summary>Which subtune is playing, counting from 1.</summary>
    public int Song { get; }

    /// <param name="chipModel">Overrides what the file's own header asks for. Null follows the
    /// header, which is what makes an export of an 8580 song sound like one.</param>
    /// <param name="song">Which subtune to play, counting from 1. Null takes the file's own start
    /// song. A tune is set up entirely by its init routine, so switching subtunes means a new
    /// engine rather than a call on this one.</param>
    public SidFilePlaybackEngine(SidFile file, ChipModel? chipModel = null, int? song = null)
    {
        if (file.UnsupportedReason() is { } reason)
            throw new SidPlaybackException(reason);

        File = file;

        _sid.SetChipModel(chipModel ?? (file.Is8580 ? ChipModel.Mos8580 : ChipModel.Mos6581));
        _sid.SetSamplingParameters(ClockFrequency, SamplingMethod.Interpolate, SampleRate);

        // A flat 64 KiB with no ROMs and no I/O beyond the SID. Enough for a PSID tune, which by
        // definition is self-contained code called through init and play.
        var memory = new byte[0x10000];
        file.Data.CopyTo(memory, file.LoadAddress);

        _cpu = new Mos6502(memory, (address, value) =>
        {
            if (address >= SidBase && address <= SidTop)
                _sid.Write(address & 0x1F, value);
            else if (PlayRate.IsTimerLatch(address))
                _timerSet = true;
        });

        Song = Math.Clamp(song ?? file.StartSong, 1, file.Songs);

        // The PSID convention: A holds the song number, counting from zero.
        _cpu.A = Song - 1;
        _cpu.X = 0;
        _cpu.Y = 0;
        _cpu.Call(file.InitAddress);

        _playAddress = file.PlayAddress;

        // After init, since that is what wrote the volume register the transient comes from.
        _sid.SettleStartupTransient(ClockFrequency);

        // 0 rather than a full frame, so the first play() call happens before any sample is
        // produced - otherwise the opening 20ms goes out with every voice still untriggered, which
        // costs a short percussive first note most of its attack.
        _nextFrameBoundaryCycles = 0;
    }

    public static SidFilePlaybackEngine FromFile(string path, ChipModel? chipModel = null, int? song = null)
    {
        byte[] bytes;
        try
        {
            bytes = System.IO.File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new SidPlaybackException($"Could not read {path}: {ex.Message}");
        }

        return new SidFilePlaybackEngine(SidFile.Parse(bytes), chipModel, song);
    }

    /// <summary>Mutes one of the three voices in this engine's output only - a listening aid, exactly
    /// as in SongPlaybackEngine. The voice keeps running underneath.</summary>
    public void SetVoiceMuted(int voice, bool muted) => _sid.SetVoiceMuted(voice, muted);

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesRequested = count / 2;
        var samples = new short[samplesRequested];
        int samplesWritten = 0;

        while (samplesWritten < samplesRequested)
        {
            if (_paused)
            {
                // Silence rather than a stalled device: NAudio keeps asking, and returning nothing
                // would end playback instead of holding it.
                Array.Clear(samples, samplesWritten, samplesRequested - samplesWritten);
                samplesWritten = samplesRequested;
                break;
            }

            double cyclesToNextFrame = _nextFrameBoundaryCycles - _totalCyclesClocked;
            if (cyclesToNextFrame < 1.0)
            {
                _cpu.Call(_playAddress);
                PlayCalls++;

                // Read after the call: a tune on the CIA timer may change its tempo from play.
                _nextFrameBoundaryCycles += PlayRate.CyclesPerCall(File, Song, _cpu.Memory, _timerSet);
                continue;
            }

            int deltaT = (int)cyclesToNextFrame;
            int deltaTRemaining = deltaT;

            // One sample at a time, so each voice's own output can be read at the instant that
            // sample was taken - which is what the scopes draw. ClockAndSample only consumes the
            // cycles a sample actually needs and checks its count before clocking, so asking for
            // one repeatedly advances the chip exactly as asking for many at once does.
            int produced = 0;
            int room = samplesRequested - samplesWritten;
            while (produced < room)
            {
                int wrote = _sid.ClockAndSample(ref deltaTRemaining, samples, samplesWritten + produced, 1);
                if (wrote == 0)
                    break;

                CaptureScopePoint(samples[samplesWritten + produced]);
                produced += wrote;
            }

            _totalCyclesClocked += deltaT - deltaTRemaining;
            samplesWritten += produced;
            Interlocked.Add(ref _samplesProduced, produced);
        }

        Buffer.BlockCopy(samples, 0, buffer, offset, samplesRequested * 2);
        return samplesRequested * 2;
    }

    private void CaptureScopePoint(short mixSample)
    {
        lock (_scopeLock)
        {
            int at = (int)(_scopeWritten % RingSamples);
            for (int voice = 0; voice < 3; voice++)
                _voiceScopes[voice][at] = _sid.Voice(voice).Output() / VoiceOutputScale;

            _mixScope[at] = mixSample / 32768f;
            _scopeWritten++;
        }
    }

    /// <summary>How many samples have been captured. The newest window ends here.</summary>
    public long ScopeSamplesWritten
    {
        get
        {
            lock (_scopeLock)
                return _scopeWritten;
        }
    }

    /// <summary>The most recent <see cref="ScopeSamples"/> of one voice's own output, oldest first,
    /// scaled so a full-amplitude voice swings by 2. This is the waveform before the filter, which
    /// is what a per-channel scope shows and what makes each voice legible on its own.
    ///
    /// Not centred on zero: a 6581 voice carries a large DC offset (see VoiceOutputScale), so a
    /// caller drawing this has to remove the mean the way a scope's AC coupling would.</summary>
    public void CaptureVoiceScope(int voice, float[] destination) =>
        CaptureRing(_voiceScopes[voice], destination, long.MaxValue);

    /// <param name="endingAt">The absolute sample index the window should end at - pass the output
    /// device's own play position (AuditionClock.SamplesPlayed), not this engine's produced count.
    /// Produced samples are ones already handed to the sound card, up to a whole buffer before
    /// anyone hears them, so drawing those makes the scope lead the music and jump a buffer at a
    /// time. Clamped to what the ring still holds.</param>
    public void CaptureVoiceScope(int voice, float[] destination, long endingAt) =>
        CaptureRing(_voiceScopes[voice], destination, endingAt);

    /// <summary>The most recent <see cref="ScopeSamples"/> of the mixed output, for the spectrum.</summary>
    public void CaptureMix(float[] destination) => CaptureRing(_mixScope, destination, long.MaxValue);

    /// <inheritdoc cref="CaptureVoiceScope(int, float[], long)"/>
    public void CaptureMix(float[] destination, long endingAt) =>
        CaptureRing(_mixScope, destination, endingAt);

    private void CaptureRing(float[] source, float[] destination, long endingAt)
    {
        if (destination.Length != ScopeSamples)
            throw new ArgumentException($"A scope capture is {ScopeSamples} samples.", nameof(destination));

        lock (_scopeLock)
        {
            // Never past what has been written, and never further back than the ring still holds.
            long end = Math.Min(endingAt, _scopeWritten);
            end = Math.Max(end, Math.Min(_scopeWritten, ScopeSamples));
            end = Math.Max(end, _scopeWritten - RingSamples + ScopeSamples);

            long start = end - ScopeSamples;
            if (start < 0)
            {
                // Before the first full window exists, hand back silence rather than whatever the
                // ring was allocated with - a scope drawing uninitialised memory would be worse
                // than one drawing a flat line.
                Array.Clear(destination);
                return;
            }

            int from = (int)(start % RingSamples);
            int firstRun = Math.Min(ScopeSamples, RingSamples - from);
            Array.Copy(source, from, destination, 0, firstRun);
            if (firstRun < ScopeSamples)
                Array.Copy(source, 0, destination, firstRun, ScopeSamples - firstRun);
        }
    }

    /// <summary>
    /// Runs the tune for a number of frames without producing audio, reporting the SID register
    /// writes each one made. Entry 0 is init's; the rest is one entry per play() call.
    ///
    /// This is how the exported file gets checked against SongSequencer: two streams of
    /// (register, value) from two entirely different routes to the same music. Grouped by frame
    /// because the two disagree about the order within one and are meant to - the assembly player
    /// walks its channels downwards to save a compare, so it writes voice 3 first. Nothing is
    /// clocked between the writes of a frame, so only their per-register sequence can be heard.
    /// </summary>
    public static List<List<(int Register, int Value)>> TraceRegisterWrites(SidFile file, int frames)
    {
        if (file.UnsupportedReason() is { } reason)
            throw new SidPlaybackException(reason);

        var current = new List<(int, int)>();
        var memory = new byte[0x10000];
        file.Data.CopyTo(memory, file.LoadAddress);

        var cpu = new Mos6502(memory, (address, value) =>
        {
            if (address >= SidBase && address <= SidTop)
                current.Add((address & 0x1F, value));
        });

        var byFrame = new List<List<(int Register, int Value)>>();

        cpu.A = Math.Max(0, file.StartSong - 1);
        cpu.Call(file.InitAddress);
        byFrame.Add(current);

        for (int i = 0; i < frames; i++)
        {
            current = [];
            cpu.Call(file.PlayAddress);
            byFrame.Add(current);
        }

        return byFrame;
    }
}
