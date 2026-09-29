using MagSid.Assembler;
using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// Finding where a tune starts repeating, by running it and watching for its memory to return to a
/// state it has been in before.
///
/// The number ends up on screen beside the elapsed time, so a wrong one is a lie a listener would
/// believe. These check it against songs whose real structure is known from their own order lists.
/// </summary>
public class SidSongLengthTests
{
    private static string SongDataDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData"));

    [Theory]
    [InlineData("Airstrike.s")]
    [InlineData("BellaCiao.s")]
    [InlineData("Breakdance.s")]
    [InlineData("Lightforce.s")]
    [InlineData("Magnificent.s")]
    [InlineData("CityBomber4k(Megastyle).s")]
    [InlineData("SOS.s")]
    public void Every_song_has_a_findable_length(string songFile)
    {
        var result = SidSongLength.Measure(Load(songFile));

        Assert.NotNull(result);

        // A few seconds at the very least: anything shorter would mean the detector had latched
        // onto a state that repeats within a single pattern rather than the song's own loop.
        Assert.True(result.TotalFrames > 50 * 4,
            $"{songFile} came back as {result.TotalFrames} frames, under four seconds - that is a " +
            "repeat inside the music, not the song's loop.");

        Assert.True(result.TotalFrames < SidSongLength.DefaultMaxFrames,
            $"{songFile} ran to the search limit, so no loop was actually found.");

        Assert.InRange(result.LoopStartFrame, 0, result.TotalFrames - 1);
    }

    /// <summary>
    /// A tune's length is not the sum of its rows, and SOS is why this test exists.
    ///
    /// Counting the rows of its three order lists gives 32, 86 and 68, which would put the moment
    /// all three realign at their least common multiple - 46.8 minutes. The real answer is 14:20,
    /// because five of its patterns carry LE loops that multiply how long each one plays. Anything
    /// that reasoned about length from the file rather than from running it would be confidently
    /// wrong, which is the whole argument for measuring this by executing the player.
    /// </summary>
    [Fact]
    public void A_length_accounts_for_loops_inside_patterns()
    {
        var measured = SidSongLength.Measure(Load("SOS.s"));

        Assert.NotNull(measured);

        // Comfortably past what the bare row counts would suggest, and nowhere near their least
        // common multiple either.
        Assert.InRange(measured.Total.TotalSeconds, 60, 20 * 60);
    }

    /// <summary>The same tune must measure the same every time, or the number on screen would
    /// change between runs of the same file.</summary>
    [Fact]
    public void Measuring_twice_gives_the_same_answer()
    {
        var song = Load("BellaCiao.s");

        Assert.Equal(SidSongLength.Measure(song), SidSongLength.Measure(song));
    }

    /// <summary>
    /// A song whose three channels run lists of different lengths only repeats once all three line
    /// up again - the least common multiple of their own cycles, not any one channel's. This is the
    /// case that would catch a detector watching one channel instead of the whole machine.
    /// </summary>
    [Fact]
    public void A_length_covers_all_three_channels_not_the_shortest()
    {
        var song = PatternsFileReader.Parse(
            File.ReadAllText(Path.Combine(SongDataDirectory, "BellaCiao.s")), "BellaCiao.s");

        var measured = SidSongLength.Measure(Wrap(song));
        Assert.NotNull(measured);

        // Every channel's own list has to have run a whole number of times by then.
        int speed = song.Speed;
        foreach (var order in song.ChannelOrders)
        {
            int channelRows = order.Tokens
                .OfType<PatternRefToken>()
                .Sum(t => song.Patterns.First(p => p.Number == t.PatternNumber).Rows.Count);

            int channelFrames = channelRows * speed;
            Assert.True(channelFrames > 0);
            Assert.True(measured.TotalFrames >= channelFrames,
                $"The measured length is {measured.TotalFrames} frames but channel " +
                $"{order.ChannelNumber} alone takes {channelFrames} - the detector stopped at one " +
                "channel's cycle rather than the whole song's.");
        }
    }

    [Fact]
    public void A_search_that_finds_nothing_says_so_rather_than_guessing()
    {
        // One frame of search is not enough to see any tune repeat.
        Assert.Null(SidSongLength.Measure(Load("BellaCiao.s"), maxFrames: 1));
    }

    /// <summary>A rendered file has to contain the audio, not just a header - the failure mode of a
    /// writer that is closed before it is pumped.</summary>
    [Fact]
    public void Rendering_to_wav_writes_the_audio()
    {
        string path = Path.Combine(Path.GetTempPath(), $"magsid-export-{Guid.NewGuid():N}.wav");
        try
        {
            var duration = TimeSpan.FromSeconds(2);
            SidAudioExport.Render(Load("BellaCiao.s"), path, SidAudioExport.Format.Wav, duration);

            var written = new FileInfo(path);
            Assert.True(written.Exists);

            // 44 bytes of header plus two seconds of 16-bit mono, within a buffer's slack.
            long expected = (long)(duration.TotalSeconds * SidFilePlaybackEngine.SampleRate) * 2;
            Assert.InRange(written.Length, expected, expected + 44 + 8820);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// What a person types into the length field decides how much audio gets written, so it is
    /// worth being exact about. Zero and negatives are refused rather than quietly producing an
    /// empty file, which would look like the export had failed for some other reason.
    /// </summary>
    [Theory]
    [InlineData("02:35:01", 155.01)]
    [InlineData("3:30:00", 210)]
    [InlineData("0:00:99", 0.99)]
    [InlineData("3:30", 210)]
    [InlineData("0:45", 45)]
    [InlineData("14:20", 860)]
    [InlineData("210", 210)]
    [InlineData("  3:30  ", 210)]
    [InlineData("0:01", 1)]
    public void A_typed_length_is_read_as_minutes_seconds_and_hundredths(string typed, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SidAudioExport.ParseLength(typed));
    }

    /// <summary>What comes out reads back as the same length, or the field would drift every time
    /// the measurement filled it in.</summary>
    [Theory]
    [InlineData(155.01)]
    [InlineData(860)]
    [InlineData(0.99)]
    [InlineData(3600)]
    public void Formatting_and_parsing_are_inverses(double seconds)
    {
        var length = TimeSpan.FromSeconds(seconds);

        Assert.Equal(length, SidAudioExport.ParseLength(SidAudioExport.FormatLength(length)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("0:00")]
    [InlineData("-5")]
    [InlineData("3:60")]      // sixty seconds is a minute, and writing it that way is a mistake
    [InlineData("1:2:3:4")]
    [InlineData("3:30:100")]   // a hundredth field only goes to 99
    [InlineData("abc")]
    [InlineData("3,30")]
    public void Something_that_is_not_a_length_is_refused(string? typed)
    {
        Assert.Null(SidAudioExport.ParseLength(typed));
    }

    /// <summary>MP3 goes through Windows' own encoder, which N editions leave out. Whichever this
    /// machine is, the answer has to be a straight yes or no rather than a throw from deep inside
    /// Media Foundation.</summary>
    [Fact]
    public void Asking_whether_mp3_is_available_never_throws()
    {
        bool available = SidAudioExport.IsMp3Available();
        Assert.True(available || !available);
    }

    private static SidFile Load(string songFile)
    {
        string path = Path.Combine(SongDataDirectory, songFile);
        Assert.True(File.Exists(path), $"{songFile} is missing from {SongDataDirectory}.");

        return Wrap(PatternsFileReader.Parse(File.ReadAllText(path), songFile));
    }

    private static SidFile Wrap(Song song)
    {
        byte[] image = SongImageBuilder.Build(song.WithUnusedPatternsRemoved());

        const int headerSize = 0x7C;
        var file = new byte[headerSize + image.Length];
        "PSID"u8.CopyTo(file);
        file[0x05] = 2;
        file[0x07] = headerSize;
        file[0x08] = 0x10;
        file[0x0A] = 0x10;
        file[0x0C] = 0x10;
        file[0x0D] = 0x03;
        file[0x0F] = 1;
        file[0x11] = 1;
        image.CopyTo(file, headerSize);
        return SidFile.Parse(file);
    }
}
