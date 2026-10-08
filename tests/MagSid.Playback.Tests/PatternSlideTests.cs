using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// Slides as pattern rows: "1xx" up and "2xx" down, xx in hex, taking a row like "..." does.
/// The sounding note glides by the rate every frame from the frame after its slide row, until
/// "100" stops it or a new note starts unslid. Speed 0 here, so a row is a frame and the
/// expected frequencies are counted straight off the rows.
/// </summary>
public class PatternSlideTests
{
    private const string SongText = """
        var v_Song_Speed = 0

        INSTRUMENTS:
        01, $41, $09, $a0, WAVE $41, ARP ROOT
        02, $41, $09, $a0
        END:

        PAT01:
        "C-4", 01
        "108", 00
        "...", 00
        "...", 00
        "...", 00
        "100", 00
        "...", 00
        "E-4", 01
        "...", 00
        "...", 00
        END:

        PAT02:
        "C-3", 02
        "205", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        "...", 00
        END:

        ORDER_CH1:
        1
        END:

        ORDER_CH2:
        2
        END:

        ORDER_CH3:
        2
        END:
        """;

    /// <summary>The frequency each voice was left at after every frame.</summary>
    private static List<int[]> RunFrames(int frames, string songText = SongText)
    {
        var song = PatternsFileReader.Parse(songText);
        var pair = new int[3, 2];
        var sequencer = new SongSequencer(song, (offset, value) =>
        {
            int voice = offset / 7, reg = offset % 7;
            if (voice < 3 && reg < 2)
                pair[voice, reg] = value;
        });

        var result = new List<int[]>();
        for (int frame = 0; frame < frames; frame++)
        {
            sequencer.Frame();
            result.Add(new[] { 0, 1, 2 }.Select(v => (pair[v, 1] << 8) | pair[v, 0]).ToArray());
        }

        return result;
    }

    [Fact]
    public void A_slide_row_glides_the_sounding_note_from_the_frame_after_it()
    {
        var frames = RunFrames(10);

        // C-4 is $1125, C-3 $0893. The slide rows sit on frame 1, so frame 2 is the first moved:
        // voice 1 (ARP ROOT rewriting the note every frame) up 8 a frame, voice 2 down 5.
        Assert.Equal(0x1125, frames[1][0]);
        Assert.Equal(0x0893, frames[1][1]);
        for (int frame = 2; frame <= 5; frame++)
        {
            Assert.Equal(0x1125 + 8 * (frame - 1), frames[frame][0]);
            Assert.Equal(0x0893 - 5 * (frame - 1), frames[frame][1]);
        }
    }

    [Fact]
    public void A_100_row_stops_the_slide_and_a_new_note_starts_unslid()
    {
        var frames = RunFrames(10);

        // "100" on frame 5: the offset reached there stays. E-4 ($159A) on frame 7 starts clean.
        Assert.Equal(0x1125 + 8 * 4, frames[5][0]);
        Assert.Equal(0x1125 + 8 * 4, frames[6][0]);
        Assert.Equal(0x159A, frames[7][0]);
        Assert.Equal(0x159A, frames[9][0]);
    }

    [Fact]
    public void The_glide_the_piano_roll_projects_is_the_one_the_sequencer_plays()
    {
        // Speed 2, three frames a row, and the rate changing mid-note: up, down, then stopped.
        string text = SongText.Replace("\r\n", "\n")
            .Replace("var v_Song_Speed = 0", "var v_Song_Speed = 2")
            .Replace("\"108\", 00\n\"...\", 00\n\"...\", 00", "\"108\", 00\n\"...\", 00\n\"20B\", 00");
        var pattern = PatternsFileReader.Parse(text).Patterns[0];
        Assert.Equal(new Core.Model.SlideRow(-11), pattern.Rows[3]);

        var offsets = Core.Model.PatternSlides.Glide(Core.Model.PatternSlides.Rates(pattern), 0, 7, 3);
        var frames = RunFrames(offsets.Length, text);

        Assert.Equal(offsets.Select(o => 0x1125 + o), frames.Select(f => f[0]));
        Assert.Contains(offsets, o => o < 0);
    }

    [Theory]
    [InlineData("\"17F\", 00", 127)]
    [InlineData("\"280\", 00", -128)]
    [InlineData("\"120\", 00", 0x20)]
    [InlineData("\"210\", 00", -0x10)]
    [InlineData("\"100\", 00", 0)]
    public void Slide_rows_read_as_their_signed_rate_and_write_back_the_same(string line, int rate)
    {
        string text = SongText.Replace("\"108\", 00", line);
        var song = PatternsFileReader.Parse(text);

        Assert.Equal(new Core.Model.SlideRow(rate), song.Patterns[0].Rows[1]);
        Assert.Contains(line.ToUpperInvariant(), PatternsFileWriter.Render(song, "test"));
    }

    [Theory]
    [InlineData("\"180\", 00")]   // up past 127
    [InlineData("\"281\", 00")]   // down past 128
    [InlineData("\"1G0\", 00")]   // not hex
    public void A_slide_out_of_range_is_refused(string line)
    {
        Assert.ThrowsAny<Exception>(() => PatternsFileReader.Parse(SongText.Replace("\"108\", 00", line)));
    }

    [Fact]
    public void An_instrument_SLIDE_is_refused_with_a_pointer_to_the_pattern_rows()
    {
        var error = Assert.ThrowsAny<Exception>(() =>
            PatternsFileReader.Parse(SongText.Replace("01, $41, $09, $a0, WAVE $41, ARP ROOT", "01, $41, $09, $a0, SLIDE +8")));

        Assert.Contains("1xx", error.Message);
    }
}
