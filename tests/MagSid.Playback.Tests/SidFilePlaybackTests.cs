using MagSid.Assembler;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

/// <summary>
/// Plays the exported bytes and checks that they make the same music the editor does.
///
/// This closes a loop the golden images cannot. Those prove the exported bytes are the bytes c6510
/// would have produced. These prove those bytes, executed on a 6502, write the same SID registers in
/// the same order as <see cref="SongSequencer"/> - the C# port the editor plays through. Either check
/// alone leaves a gap: bytes can be right and the port wrong, or the port right and the emitter's
/// idea of the player subtly different.
///
/// It also means the built-in .sid player is verified rather than merely written. A SID player that
/// mis-executes one opcode produces a tune that sounds plausible and wrong.
/// </summary>
public class SidFilePlaybackTests
{
    private static string SongDataDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData"));

    /// <summary>Long enough to get well past the first pattern of every song, through note-offs, loop
    /// markers and filter programs, without making the suite slow.</summary>
    private const int Frames = 1500;

    [Theory]
    [InlineData("Airstrike.s")]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("CityBomber4k(Megastyle).s")]
    [InlineData("SOS.s")]
    public void Exported_bytes_write_the_same_registers_as_the_sequencer(string songFile)
    {
        var song = LoadSong(songFile);

        // Frame 0 is what setting the song up writes, matching init's on the other side.
        var fromSequencer = new List<List<(int Register, int Value)>>();
        var current = new List<(int, int)>();
        var sequencer = new SongSequencer(song, (register, value) => current.Add((register, value)));
        fromSequencer.Add(current);
        for (int i = 0; i < Frames; i++)
        {
            current = [];
            sequencer.Frame();
            fromSequencer.Add(current);
        }

        var fromCpu = SidFilePlaybackEngine.TraceRegisterWrites(
            SidFile.Parse(BuildPsid(song)), Frames);

        Assert.Equal(fromSequencer.Count, fromCpu.Count);

        for (int frame = 0; frame < fromSequencer.Count; frame++)
        {
            // Per register, in order. The two walk their channels in opposite directions - the
            // assembly player counts down to save a compare - so the frame's writes arrive
            // interleaved differently, and nothing is clocked between them, so only the sequence
            // each individual register sees can be heard.
            var mine = ByRegister(fromCpu[frame]);
            var theirs = ByRegister(fromSequencer[frame]);

            foreach (int register in mine.Keys.Union(theirs.Keys).OrderBy(r => r))
            {
                mine.TryGetValue(register, out var exported);
                theirs.TryGetValue(register, out var sequenced);

                Assert.True(
                    (exported ?? []).SequenceEqual(sequenced ?? []),
                    $"{songFile}, frame {frame}, ${0xD400 + register:X4}: the exported tune wrote " +
                    $"[{Values(exported)}], the sequencer [{Values(sequenced)}].");
            }
        }
    }

    private static Dictionary<int, List<int>> ByRegister(List<(int Register, int Value)> writes)
    {
        var grouped = new Dictionary<int, List<int>>();
        foreach (var (register, value) in writes)
        {
            if (!grouped.TryGetValue(register, out var values))
                grouped[register] = values = [];
            values.Add(value);
        }

        return grouped;
    }

    private static string Values(List<int>? values) =>
        values is null ? string.Empty : string.Join(" ", values.Select(v => $"${v:X2}"));

    /// <summary>The engine has to produce audio, not just registers - and the tune has to be audible
    /// rather than a buffer of silence, which is what a player that never triggers a voice returns.</summary>
    [Fact]
    public void Produces_audible_samples()
    {
        var engine = new SidFilePlaybackEngine(SidFile.Parse(BuildPsid(LoadSong("BellaCiao.s"))));

        // A second of audio, in buffers the size NAudio would ask for.
        var buffer = new byte[4096];
        int peak = 0;
        long produced = 0;
        while (produced < SidFilePlaybackEngine.SampleRate)
        {
            int bytesRead = engine.Read(buffer, 0, buffer.Length);
            produced += bytesRead / 2;
            for (int i = 0; i < bytesRead; i += 2)
                peak = Math.Max(peak, Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8))));
        }

        Assert.True(peak > 1000, $"A second of BellaCiao peaked at {peak}, which is effectively silence.");
    }

    /// <summary>The player window draws entirely from these buffers, and a silent one would show as
    /// a flat line rather than as an error - so what fills them is checked here instead of by looking
    /// at the window.</summary>
    [Fact]
    public void Fills_a_scope_for_every_voice_and_the_mix()
    {
        // BellaCiao uses all three voices, which is what makes it the right song for this.
        var engine = new SidFilePlaybackEngine(SidFile.Parse(BuildPsid(LoadSong("BellaCiao.s"))));
        Render(engine, SidFilePlaybackEngine.SampleRate / 2);

        var capture = new float[SidFilePlaybackEngine.ScopeSamples];

        for (int voice = 0; voice < 3; voice++)
        {
            engine.CaptureVoiceScope(voice, capture);
            Assert.True(Peak(capture) > 0.001f,
                $"Voice {voice + 1}'s scope is flat after half a second; its panel would draw a dead line.");
            // Centred the way the view centres it, which is where the [-1, 1] contract applies -
            // a 6581 voice arrives sitting on a DC offset.
            float peak = Peak(Centred(capture));
            Assert.True(peak <= 1.0f,
                $"Voice {voice + 1}'s scope swings to {peak} once centred, past the [-1, 1] the view scales to.");
        }

        engine.CaptureMix(capture);
        Assert.True(Peak(capture) > 0.001f, "The mix window is flat, so the spectrum would show nothing.");
    }

    /// <summary>
    /// A capture must follow the sample it is asked for, not the render frontier.
    ///
    /// This is the bug that made the scopes look frozen: Read() runs a whole output buffer ahead,
    /// so the newest samples are ones nobody has heard yet, and they arrive in bursts about ten
    /// times a second however often the UI ticks. Drawing the window that ends at the device's play
    /// position fixes both the lag and the stepping - but only if the ring is longer than the
    /// latency, which is the half that is easy to get wrong and impossible to see.
    /// </summary>
    [Fact]
    public void A_capture_follows_the_sample_it_is_asked_for()
    {
        var engine = new SidFilePlaybackEngine(SidFile.Parse(BuildPsid(LoadSong("BellaCiao.s"))));
        Render(engine, SidFilePlaybackEngine.SampleRate);

        long frontier = engine.ScopeSamplesWritten;
        Assert.True(frontier >= SidFilePlaybackEngine.SampleRate,
            $"Only {frontier} samples were captured; the test would prove little.");

        // A third of a second back - well past any sane output latency, and the distance at which
        // the old ring had already overwritten what was audible.
        long behind = frontier - (SidFilePlaybackEngine.SampleRate / 3);

        var newest = new float[SidFilePlaybackEngine.ScopeSamples];
        var earlier = new float[SidFilePlaybackEngine.ScopeSamples];
        engine.CaptureVoiceScope(0, newest, frontier);
        engine.CaptureVoiceScope(0, earlier, behind);

        Assert.False(newest.SequenceEqual(earlier),
            "The same window came back for two positions a third of a second apart, so the capture " +
            "is ignoring the position it was given and the scopes cannot follow the music.");

        // And asking twice for the same instant is stable, or the trace would shimmer.
        var again = new float[SidFilePlaybackEngine.ScopeSamples];
        engine.CaptureVoiceScope(0, again, behind);
        Assert.Equal(earlier, again);
    }

    /// <summary>The ring has to outlast the output latency by a wide margin, or the window a
    /// listener is hearing is gone before anything can draw it.</summary>
    [Fact]
    public void Keeps_more_than_a_second_of_history()
    {
        var engine = new SidFilePlaybackEngine(SidFile.Parse(BuildPsid(LoadSong("BellaCiao.s"))));
        Render(engine, SidFilePlaybackEngine.SampleRate);

        long frontier = engine.ScopeSamplesWritten;
        var oldest = new float[SidFilePlaybackEngine.ScopeSamples];
        var newest = new float[SidFilePlaybackEngine.ScopeSamples];

        // A full second back: further than WaveOutEvent's own 300ms default, with room to spare.
        engine.CaptureVoiceScope(0, oldest, frontier - SidFilePlaybackEngine.SampleRate + SidFilePlaybackEngine.ScopeSamples);
        engine.CaptureVoiceScope(0, newest, frontier);

        Assert.False(oldest.SequenceEqual(newest),
            "A window a second old came back identical to the newest one, so the ring is no longer " +
            "than the latency it has to outlast.");
    }

    /// <summary>Pause has to hold the tune rather than end it: NAudio keeps asking for samples, and
    /// returning none would stop playback outright.</summary>
    [Fact]
    public void Pausing_yields_silence_without_ending_playback()
    {
        var engine = new SidFilePlaybackEngine(SidFile.Parse(BuildPsid(LoadSong("BellaCiao.s"))));
        Render(engine, SidFilePlaybackEngine.SampleRate / 4);

        long beforePause = engine.SamplesProduced;
        engine.Paused = true;

        var buffer = new byte[4096];
        int bytesRead = engine.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, bytesRead);
        Assert.Equal(0, PeakOf(buffer, bytesRead));

        engine.Paused = false;
        Render(engine, SidFilePlaybackEngine.SampleRate / 4);
        Assert.True(engine.SamplesProduced > beforePause, "Playback did not resume after unpausing.");
    }

    private static void Render(SidFilePlaybackEngine engine, long samples)
    {
        var buffer = new byte[4096];
        long produced = 0;
        while (produced < samples)
            produced += engine.Read(buffer, 0, buffer.Length) / 2;
    }

    private static float[] Centred(float[] samples)
    {
        float mean = samples.Average();
        return samples.Select(sample => sample - mean).ToArray();
    }

    private static float Peak(float[] samples)
    {
        float peak = 0f;
        foreach (float sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    private static int PeakOf(byte[] buffer, int count)
    {
        int peak = 0;
        for (int i = 0; i < count; i += 2)
            peak = Math.Max(peak, Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8))));
        return peak;
    }

    [Fact]
    public void Round_trips_the_header_fields_the_editor_writes()
    {
        var sid = SidFile.Parse(BuildPsid(LoadSong("SOS.s"), name: "SOS", author: "Magnar", released: "2026"));

        Assert.Equal("SOS", sid.Name);
        Assert.Equal("Magnar", sid.Author);
        Assert.Equal("2026", sid.Released);
        Assert.Equal(0x1000, sid.LoadAddress);
        Assert.Equal(0x1000, sid.InitAddress);
        Assert.Equal(0x1003, sid.PlayAddress);
        Assert.Null(sid.UnsupportedReason());
    }

    /// <summary>A relocated export has to play too - the addresses move, the music does not.</summary>
    [Theory]
    [InlineData(0x2000)]
    [InlineData(0x4400)]
    public void Plays_the_same_music_from_another_load_address(int origin)
    {
        var song = LoadSong("BellaCiao.s");

        var atDefault = SidFilePlaybackEngine.TraceRegisterWrites(
            SidFile.Parse(BuildPsid(song)), Frames);
        var relocated = SidFilePlaybackEngine.TraceRegisterWrites(
            SidFile.Parse(BuildPsid(song, origin)), Frames);

        Assert.Equal(atDefault.Count, relocated.Count);
        for (int frame = 0; frame < atDefault.Count; frame++)
            Assert.Equal(atDefault[frame], relocated[frame]);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_sid()
    {
        var exception = Assert.Throws<SidPlaybackException>(
            () => SidFile.Parse(new byte[200]));
        Assert.Contains("PSID", exception.Message);
    }

    /// <summary>An RSID needs a C64 this player does not emulate, and saying so is better than
    /// playing noise.</summary>
    [Fact]
    public void Explains_why_an_RSID_cannot_play()
    {
        byte[] bytes = BuildPsid(LoadSong("SOS.s"));
        "RSID"u8.CopyTo(bytes);

        Assert.Contains("RSID", SidFile.Parse(bytes).UnsupportedReason());
    }

    /// <summary>Wraps the image the way SidExporter does. Duplicated here rather than called, because
    /// SidExporter lives in the WinForms project and a test project should not have to drag that
    /// in - the header is 12 fields and the shape is pinned by the round-trip test above.</summary>
    private static byte[] BuildPsid(
        Song song, int loadAddress = 0x1000,
        string name = "Test", string author = "Test", string released = "2026")
    {
        byte[] image = SongImageBuilder.Build(song.WithUnusedPatternsRemoved(), loadAddress);

        const int headerSize = 0x7C;
        var file = new byte[headerSize + image.Length];
        "PSID"u8.CopyTo(file);
        Be16(file, 0x04, 2);
        Be16(file, 0x06, headerSize);
        Be16(file, 0x08, loadAddress);
        Be16(file, 0x0A, loadAddress);
        Be16(file, 0x0C, loadAddress + 3);
        Be16(file, 0x0E, 1);
        Be16(file, 0x10, 1);
        Ascii(file, 0x16, name);
        Ascii(file, 0x36, author);
        Ascii(file, 0x56, released);
        Be16(file, 0x76, (0b01 << 2) | (0b01 << 4));    // PAL, MOS6581
        image.CopyTo(file, headerSize);
        return file;
    }

    private static void Be16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void Ascii(byte[] buffer, int offset, string value) =>
        System.Text.Encoding.ASCII.GetBytes(value).CopyTo(buffer, offset);

    private static Song LoadSong(string songFile)
    {
        string path = Path.Combine(SongDataDirectory, songFile);
        Assert.True(File.Exists(path), $"{songFile} is missing from {SongDataDirectory}.");
        return PatternsFileReader.Parse(File.ReadAllText(path), songFile);
    }
}
