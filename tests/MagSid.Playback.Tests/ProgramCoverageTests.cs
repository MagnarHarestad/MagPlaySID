using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// Coverage exists so an unreachable tail of a program is visible in the editor rather than
/// guessed at, and the mistake that matters is the false negative: a step shown as never reached
/// invites deleting something the song actually plays. These pin both directions - what gets
/// marked, and what deliberately does not.
/// </summary>
public class ProgramCoverageTests
{
    /// <summary>A song whose channel 1 plays one note on instrument 1 and then holds.</summary>
    private static (Song Song, SongSequencer Sequencer) Build(Instrument instrument, int holdRows = 32)
    {
        var song = new Song { Speed = 0 };   // tick every frame, so nothing waits on the divider
        song.Instruments.Add(instrument);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), instrument.Number));
        for (int i = 0; i < holdRows; i++)
            pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        return (song, new SongSequencer(song, (_, _) => { }));
    }

    private static Instrument MakeInstrument(int number = 1) =>
        new() { Number = number, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xAA };

    [Fact]
    public void Every_wavetable_step_the_note_reaches_is_marked()
    {
        var instrument = MakeInstrument();
        instrument.Wavetable.AddRange([0x81, 0x41, 0x21]);

        var (_, sequencer) = Build(instrument);
        for (int i = 0; i < 20; i++)
            sequencer.Frame();

        var coverage = sequencer.CoverageFor(1);
        Assert.True(coverage.Wavetable[0]);
        Assert.True(coverage.Wavetable[1]);
        Assert.True(coverage.Wavetable[2]);
        Assert.False(coverage.Wavetable[3]);   // the program has only three
    }

    [Fact]
    public void A_step_the_note_never_lives_long_enough_to_reach_stays_unmarked()
    {
        // The case the feature is for: a long wavetable under a note that is cut off early. The
        // song is stopped after three frames of automation, so the tail was never played.
        var instrument = MakeInstrument();
        instrument.Wavetable.AddRange([0x81, 0x41, 0x21, 0x11, 0x41, 0x21]);

        var (_, sequencer) = Build(instrument);
        for (int i = 0; i < 4; i++)
            sequencer.Frame();

        var coverage = sequencer.CoverageFor(1);
        Assert.True(coverage.Wavetable[0]);
        Assert.False(coverage.Wavetable[4]);
        Assert.False(coverage.Wavetable[5]);
    }

    [Fact]
    public void A_looping_arpeggio_marks_all_of_its_steps()
    {
        var instrument = MakeInstrument();
        instrument.Wavetable.Add(0x41);
        instrument.Arpeggio.AddRange(
            [NoteName.Parse("C-4"), NoteName.Parse("E-4"), NoteName.Parse("G-4")]);
        instrument.ArpeggioEnding = ArpeggioEnding.Loop;

        var (_, sequencer) = Build(instrument);
        for (int i = 0; i < 20; i++)
            sequencer.Frame();

        var coverage = sequencer.CoverageFor(1);
        Assert.True(coverage.Arpeggio[0]);
        Assert.True(coverage.Arpeggio[1]);
        Assert.True(coverage.Arpeggio[2]);
        Assert.False(coverage.Arpeggio[3]);
    }

    [Fact]
    public void Filter_steps_are_recorded_against_the_instrument_that_claimed_the_filter()
    {
        var instrument = MakeInstrument();
        instrument.Wavetable.Add(0x41);
        instrument.Filter.Add(FilterStep.Setup(0x1F, 0xF1, 0x40));
        instrument.Filter.Add(FilterStep.Sweep(0x30));

        var (_, sequencer) = Build(instrument);
        for (int i = 0; i < 20; i++)
            sequencer.Frame();

        var coverage = sequencer.CoverageFor(1);
        Assert.True(coverage.Filter[0]);
        Assert.True(coverage.Filter[1]);
        Assert.False(coverage.Filter[2]);

        // ...and not against an instrument that never owned it.
        Assert.False(sequencer.CoverageFor(2).Filter[0]);
    }

    [Fact]
    public void An_instrument_that_never_plays_has_no_coverage_at_all()
    {
        var instrument = MakeInstrument();
        instrument.Wavetable.AddRange([0x81, 0x41]);

        var (_, sequencer) = Build(instrument);
        for (int i = 0; i < 20; i++)
            sequencer.Frame();

        var unused = sequencer.CoverageFor(5);
        Assert.False(unused.AnyReached(0));
        Assert.False(unused.AnyReached(1));
    }

    [Fact]
    public void The_playhead_walks_down_the_wavetable_a_step_per_frame()
    {
        var instrument = MakeInstrument();
        instrument.Wavetable.AddRange([0x81, 0x41, 0x21, 0x11]);

        var (_, sequencer) = Build(instrument);
        var coverage = sequencer.CoverageFor(1);

        var seen = new List<int>();
        for (int i = 0; i < 12; i++)
        {
            sequencer.Frame();
            seen.Add(coverage.CurrentWavetableStep);
        }

        // One step per frame until the program runs out, then it rests on the last one rather
        // than blinking off - which is what makes the highlight readable between notes.
        Assert.Equal([0, 1, 2, 3, 3, 3, 3, 3], seen.Skip(seen.IndexOf(0)).Take(8).ToArray());
        Assert.True(coverage.IsCurrent(3));
        Assert.False(coverage.IsCurrent(0));
    }

    [Fact]
    public void The_playhead_never_reports_the_gap_between_a_trigger_and_its_first_step()
    {
        // A trigger clears the voice's own last-step to -1 for one frame. Following that would
        // blink the highlight off on every note, so the playhead holds its previous value.
        var instrument = MakeInstrument();
        instrument.Wavetable.AddRange([0x81, 0x41]);

        var (_, sequencer) = Build(instrument, holdRows: 1);
        var coverage = sequencer.CoverageFor(1);

        for (int i = 0; i < 40; i++)
        {
            sequencer.Frame();
            Assert.True(coverage.CurrentWavetableStep >= -1);
            if (i > 2)
                Assert.True(coverage.CurrentWavetableStep >= 0, $"blinked off on frame {i}");
        }
    }

    [Fact]
    public void AnyReached_covers_a_row_reached_by_only_one_of_the_three_programs()
    {
        // The grid puts the wavetable, note and filter steps on one row, so the row counts as
        // used when any of them got there - a two-step wavetable beside a six-step arpeggio
        // still has six live rows.
        var instrument = MakeInstrument();
        instrument.Wavetable.AddRange([0x81, 0x41]);
        instrument.Arpeggio.AddRange(
            [NoteName.Parse("C-4"), NoteName.Parse("E-4"), NoteName.Parse("G-4"),
             NoteName.Parse("C-5")]);

        var (_, sequencer) = Build(instrument);
        for (int i = 0; i < 20; i++)
            sequencer.Frame();

        var coverage = sequencer.CoverageFor(1);
        Assert.False(coverage.Wavetable[3]);
        Assert.True(coverage.Arpeggio[3]);
        Assert.True(coverage.AnyReached(3));
    }
}
