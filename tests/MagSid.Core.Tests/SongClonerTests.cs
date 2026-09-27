using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// The undo history hands the same Song object back to the editor when it restores a snapshot, so
/// a clone that shares a mutable list with its original would let an edit made after the snapshot
/// reach back into it - the undo would then restore the very state it was meant to replace. These
/// check the separation that stops that.
/// </summary>
public class SongClonerTests
{
    private static Song MakeSong()
    {
        var song = new Song { Speed = 4 };

        var instrument = new Instrument { Number = 1, Name = "Bass", Waveform = 0x41 };
        instrument.Wavetable.AddRange([0x41, 0x21]);
        instrument.Arpeggio.Add(NoteName.Parse("C-4"));
        instrument.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x40, sweepRate: 2));
        song.Instruments.Add(instrument);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        pattern.Rows.Add(new LoopEndRow(4));
        song.Patterns.Add(pattern);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[0].RestartPosition = 1;
        return song;
    }

    [Fact]
    public void A_clone_carries_every_value()
    {
        var song = MakeSong();
        var copy = SongCloner.Clone(song);

        Assert.Equal(song.Speed, copy.Speed);
        Assert.Equal("Bass", copy.Instruments[0].Name);
        Assert.Equal(song.Instruments[0].Wavetable, copy.Instruments[0].Wavetable);
        Assert.Equal(song.Instruments[0].Arpeggio, copy.Instruments[0].Arpeggio);
        Assert.Equal(song.Instruments[0].Filter, copy.Instruments[0].Filter);
        Assert.Equal(song.Patterns[0].Rows, copy.Patterns[0].Rows);
        Assert.Equal(song.ChannelOrders[0].Tokens, copy.ChannelOrders[0].Tokens);
        Assert.Equal(new LoopEndRow(4), copy.Patterns[0].Rows[1]);
    }

    [Fact]
    public void Editing_the_original_afterwards_leaves_the_clone_alone()
    {
        var song = MakeSong();
        var copy = SongCloner.Clone(song);

        song.Speed = 9;
        song.Instruments[0].Name = "changed";
        song.Instruments[0].Wavetable.Add(0x11);
        song.Instruments[0].Arpeggio.Clear();
        song.Patterns[0].Rows.Add(new HoldRow());
        song.Patterns[0].Number = 7;
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[0].RestartPosition = 2;
        song.Instruments.Add(new Instrument { Number = 2 });
        song.Patterns.Add(new Pattern { Number = 2 });

        Assert.Equal(4, copy.Speed);
        Assert.Equal("Bass", copy.Instruments[0].Name);
        Assert.Equal([0x41, 0x21], copy.Instruments[0].Wavetable);
        Assert.Single(copy.Instruments[0].Arpeggio);
        Assert.Equal(2, copy.Patterns[0].Rows.Count);
        Assert.Equal(1, copy.Patterns[0].Number);
        Assert.Single(copy.ChannelOrders[0].Tokens);
        Assert.Equal(1, copy.ChannelOrders[0].RestartPosition);
        Assert.Single(copy.Instruments);
        Assert.Single(copy.Patterns);
    }
}
