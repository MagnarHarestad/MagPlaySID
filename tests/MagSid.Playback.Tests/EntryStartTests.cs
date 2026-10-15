using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// Where a channel starts an order entry, which F2 from the arrange ruler, Cycle, punch-in and
/// REC all count from. A pattern that opens with a loop plays its first row once a pass, so
/// "the first row's trigger" is ambiguous; the entry starts on its first pass.
/// </summary>
public class EntryStartTests
{
    /// <summary>Magnificent's PAT03 in small: a two-row loop played 32 times opens the pattern,
    /// on channel 3 alone, after a 16-row pattern.</summary>
    private static Song LoopFirstSong()
    {
        var song = new Song { Speed = 2 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });

        var plain = new Pattern { Number = 1 };
        for (int row = 0; row < 16; row++)
            plain.Rows.Add(new NoteRow(NoteName.AllNotes[40 + row], 1));
        song.Patterns.Add(plain);

        var looped = new Pattern { Number = 2 };
        looped.Rows.Add(new LoopStartRow());
        looped.Rows.Add(new NoteRow(NoteName.Parse("G-5"), 1));
        looped.Rows.Add(new HoldRow());
        looped.Rows.Add(new LoopEndRow(32));
        looped.Rows.Add(new NoteRow(NoteName.Parse("A-5"), 1));
        looped.Rows.Add(new HoldRow());
        song.Patterns.Add(looped);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));
        return song;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(63)]
    public void A_pattern_opening_with_a_loop_starts_on_the_loops_first_pass(int ticksInto)
    {
        var song = LoopFirstSong();
        int framesPerTick = song.Speed + 1;

        var position = SongSequencer.FindPositionInEntry(song, channel: 2, orderIndex: 1, ticksInto);

        // PAT01 is 16 ticks, so the looped pattern starts on tick 16 - not 31 passes of two rows
        // later, which is where the latest trigger of its first row lies.
        Assert.Equal((16 + ticksInto) * framesPerTick, position?.Frame);
    }

    [Fact]
    public void The_first_entry_of_a_channel_starts_at_the_top()
    {
        var song = LoopFirstSong();

        Assert.Equal(0, SongSequencer.FindPositionInEntry(song, channel: 0, orderIndex: 0, ticksInto: 0)?.Frame);
    }
}
