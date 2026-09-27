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
    private const double ClockFrequency = 985248.0;     // PAL C64
    private const double FrameRate = 50.0;              // PAL frames/second

    public const int SampleRate = 44100;

    /// <summary>The SID answers every 32 bytes from $D400 to $D7FF. Players write through the mirrors
    /// as readily as the base address, so the whole range is routed.</summary>
    private const int SidBase = 0xD400;
    private const int SidTop = 0xD7FF;

    private readonly SidChip _sid = new();
    private readonly Mos6502 _cpu;
    private readonly int _playAddress;
    private readonly double _cyclesPerFrame = ClockFrequency / FrameRate;

    private double _totalCyclesClocked;
    private double _nextFrameBoundaryCycles;
    private long _samplesProduced;

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    public long SamplesProduced => Interlocked.Read(ref _samplesProduced);

    public SidFile File { get; }

    /// <param name="chipModel">Overrides what the file's own header asks for. Null follows the
    /// header, which is what makes an export of an 8580 song sound like one.</param>
    public SidFilePlaybackEngine(SidFile file, ChipModel? chipModel = null)
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
        });

        // The PSID convention: A holds the song number, counting from zero.
        _cpu.A = Math.Max(0, file.StartSong - 1);
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

    public static SidFilePlaybackEngine FromFile(string path, ChipModel? chipModel = null)
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

        return new SidFilePlaybackEngine(SidFile.Parse(bytes), chipModel);
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
            double cyclesToNextFrame = _nextFrameBoundaryCycles - _totalCyclesClocked;
            if (cyclesToNextFrame < 1.0)
            {
                _cpu.Call(_playAddress);
                _nextFrameBoundaryCycles += _cyclesPerFrame;
                continue;
            }

            int deltaT = (int)cyclesToNextFrame;
            int deltaTRemaining = deltaT;
            int produced = _sid.ClockAndSample(
                ref deltaTRemaining, samples, samplesWritten, samplesRequested - samplesWritten);

            _totalCyclesClocked += deltaT - deltaTRemaining;
            samplesWritten += produced;
            Interlocked.Add(ref _samplesProduced, produced);
        }

        Buffer.BlockCopy(samples, 0, buffer, offset, samplesRequested * 2);
        return samplesRequested * 2;
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
