using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Core.Tests;

public class PatternsFileReaderTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Parses_minimal_grammar_into_the_expected_model_shape()
    {
        var song = PatternsFileReader.Read(FixturePath("MinimalGrammar.s"));

        Assert.Equal(4, song.Speed);

        Assert.Equal(2, song.Instruments.Count);
        Assert.Equal(1, song.Instruments[0].Number);
        Assert.Equal(0x21, song.Instruments[0].Waveform);
        Assert.Equal(0x09, song.Instruments[0].AttackDecay);
        Assert.Equal(0xaa, song.Instruments[0].SustainRelease);
        Assert.Equal(2, song.Instruments[1].Number);

        Assert.Equal(2, song.Patterns.Count);

        var pat1 = song.Patterns.Single(p => p.Number == 1);
        Assert.Collection(pat1.Rows,
            row => Assert.Equal(new NoteRow(NoteName.Parse("C-5"), 1), row),
            row => Assert.IsType<HoldRow>(row),
            row => Assert.Equal(new NoteRow(NoteName.Parse("D#4"), 2), row),
            row => Assert.IsType<NoteOffRow>(row));

        var pat2 = song.Patterns.Single(p => p.Number == 2);
        Assert.Collection(pat2.Rows,
            row => Assert.Equal(new NoteRow(NoteName.Parse("G-3"), 2), row),
            row => Assert.IsType<LoopStartRow>(row),
            row => Assert.Equal(new NoteRow(NoteName.Parse("A-3"), 1), row),
            row => Assert.IsType<HoldRow>(row),
            row => Assert.IsType<LoopEndRow>(row));

        Assert.Collection(song.ChannelOrders[0].Tokens,
            t => Assert.Equal(new PatternRefToken(1), t),
            t => Assert.Equal(new PatternRefToken(2), t));
        Assert.Equal(2, song.ChannelOrders[0].RestartPosition);

        Assert.Collection(song.ChannelOrders[1].Tokens,
            t => Assert.Equal(new PatternRefToken(1), t));
        Assert.Equal(1, song.ChannelOrders[1].RestartPosition);

        Assert.Collection(song.ChannelOrders[2].Tokens,
            t => Assert.Equal(new PatternRefToken(2), t),
            t => Assert.Equal(new PatternRefToken(1), t));
        Assert.Equal(1, song.ChannelOrders[2].RestartPosition);
    }

    [Fact]
    public void Parses_the_sibling_projects_real_current_song()
    {
        var song = PatternsFileReader.Read(FixturePath("CurrentSongCopy.s"));

        Assert.Equal(6, song.Speed);
        Assert.Equal(3, song.Instruments.Count);
        Assert.Equal(3, song.Patterns.Count);

        var pat3 = song.Patterns.Single(p => p.Number == 3);
        Assert.Contains(pat3.Rows, r => r is LoopStartRow);
        Assert.Contains(pat3.Rows, r => r is LoopEndRow);

        Assert.Collection(song.ChannelOrders[0].Tokens,
            t => Assert.Equal(new PatternRefToken(1), t),
            t => Assert.Equal(new PatternRefToken(2), t));
        Assert.Equal(2, song.ChannelOrders[0].RestartPosition);
    }

    [Fact]
    public void Is_case_insensitive_and_whitespace_tolerant_on_keywords_and_rows()
    {
        string text = """
            var v_Song_Speed = 6

            instruments:
            01, $21, $09, $aa
            end:

            pat01:
            "c-5" , 01
            ls
            "...",00
            le
            end:

            order_ch1:
            1
            end:

            ORDER_CH2:
            1
            END:

            Order_Ch3:
            1
            End:
            """;

        var song = PatternsFileReader.Parse(text, "test.s");

        Assert.Single(song.Patterns);
        Assert.Collection(song.Patterns[0].Rows,
            row => Assert.Equal(new NoteRow(NoteName.Parse("C-5"), 1), row),
            row => Assert.IsType<LoopStartRow>(row),
            row => Assert.IsType<HoldRow>(row),
            row => Assert.IsType<LoopEndRow>(row));
    }

    [Fact]
    public void Rejects_flats()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            01, $21, $09, $aa
            END:

            PAT01:
            "Db4", 01
            END:

            ORDER_CH1:
            1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("not a valid note", ex.Message);
    }

    [Fact]
    public void Reports_duplicate_pattern_numbers_with_line_number()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            END:

            PAT01:
            "---", 00
            END:

            PAT01:
            "---", 00
            END:

            ORDER_CH1:
            1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("Duplicate PAT01", ex.Message);
        Assert.True(ex.LineNumber > 0);
    }

    [Fact]
    public void Reports_non_contiguous_pattern_numbers()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            END:

            PAT01:
            "---", 00
            END:

            PAT03:
            "---", 00
            END:

            ORDER_CH1:
            1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("missing PAT02", ex.Message);
    }

    [Fact]
    public void Reports_restart_not_followed_by_exactly_one_target_as_the_last_tokens()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            END:

            PAT01:
            "---", 00
            END:

            ORDER_CH1:
            1, "restart", 1, 1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("'restart' must be followed by exactly one target position, as the last two tokens", ex.Message);
    }

    [Fact]
    public void Reports_a_restart_target_out_of_range()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            END:

            PAT01:
            "---", 00
            END:

            ORDER_CH1:
            1, "restart", 5
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("'restart' target 5 is out of range", ex.Message);
    }

    [Fact]
    public void Reports_order_referencing_an_undefined_pattern()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            END:

            PAT01:
            "---", 00
            END:

            ORDER_CH1:
            99
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("references undefined pattern 99", ex.Message);
    }

    [Fact]
    public void Reports_a_note_row_referencing_an_undefined_instrument()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            END:

            PAT01:
            "C-5", 09
            END:

            ORDER_CH1:
            1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("instrument 9 is not defined", ex.Message);
    }

    [Fact]
    public void Parses_a_finite_repeat_count_on_LE()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            01, $21, $09, $aa
            END:

            PAT01:
            LS
            "C-5", 01
            LE04
            END:

            ORDER_CH1:
            1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var song = PatternsFileReader.Parse(text, "test.s");

        Assert.Collection(song.Patterns[0].Rows,
            row => Assert.IsType<LoopStartRow>(row),
            row => Assert.Equal(new NoteRow(NoteName.Parse("C-5"), 1), row),
            row => Assert.Equal(new LoopEndRow(4), row));
    }

    [Fact]
    public void Reports_an_LE_repeat_count_below_1()
    {
        string text = """
            var v_Song_Speed = 6

            INSTRUMENTS:
            01, $21, $09, $aa
            END:

            PAT01:
            LS
            "C-5", 01
            LE00
            END:

            ORDER_CH1:
            1
            END:
            ORDER_CH2:
            1
            END:
            ORDER_CH3:
            1
            END:
            """;

        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileReader.Parse(text, "test.s"));
        Assert.Contains("LE repeat count must be at least 1", ex.Message);
    }
}
