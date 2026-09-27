using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Core.Tests;

public class PatternsFileWriterTests
{
    [Fact]
    public void Renders_a_small_song_in_canonical_form()
    {
        var song = new Song { Speed = 6 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-5"), 1));
        pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new NoteOffRow());
        song.Patterns.Add(pattern);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));

        string text = PatternsFileWriter.Render(song);

        Assert.DoesNotContain('\r', text);
        Assert.Contains("var v_Song_Speed = 6\n", text);
        Assert.Contains("01, $21, $09, $aa\n", text);
        Assert.Contains("PAT01:\n\"C-5\", 01\n\"...\", 00\n\"---\", 00\nEND:\n", text);
        Assert.Contains("ORDER_CH1:\n1\nEND:\n", text);
    }

    [Fact]
    public void Renders_a_trailing_restart_pair_only_when_the_restart_position_is_not_1()
    {
        var song = new Song { Speed = 6 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-5"), 1));
        song.Patterns.Add(pattern);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[0].RestartPosition = 2;
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));

        string text = PatternsFileWriter.Render(song);

        Assert.Contains("ORDER_CH1:\n1, 1, \"restart\", 2\nEND:\n", text);
        Assert.Contains("ORDER_CH2:\n1\nEND:\n", text);
    }

    [Fact]
    public void Renders_a_finite_LE_repeat_count_and_a_bare_infinite_LE()
    {
        var song = new Song { Speed = 6 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new LoopStartRow());
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-5"), 1));
        pattern.Rows.Add(new LoopEndRow(4));
        song.Patterns.Add(pattern);

        var pattern2 = new Pattern { Number = 2 };
        pattern2.Rows.Add(new LoopStartRow());
        pattern2.Rows.Add(new NoteRow(NoteName.Parse("D-5"), 1));
        pattern2.Rows.Add(new LoopEndRow());
        song.Patterns.Add(pattern2);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));

        string text = PatternsFileWriter.Render(song);

        Assert.Contains("PAT01:\nLS\n\"C-5\", 01\nLE04\nEND:\n", text);
        Assert.Contains("PAT02:\nLS\n\"D-5\", 01\nLE\nEND:\n", text);
    }

    [Fact]
    public void Refuses_to_render_a_song_with_no_patterns()
    {
        var song = new Song();
        var ex = Assert.Throws<PatternsFormatException>(() => PatternsFileWriter.Render(song));
        Assert.Contains("No patterns defined", ex.Message);
    }
}
