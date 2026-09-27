using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// The two sweeps whose shape is easy to get subtly wrong: the filter's triangle and the pulse
/// width's two widths. Both are pinned against what the assembly actually produces.
/// </summary>
public class SweepTests
{
    private static (List<(int Offset, int Value)> Writes, SongSequencer Sequencer) Build(
        Instrument instrument, params PatternRow[] rows)
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(instrument);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));
        writes.Clear();
        return (writes, sequencer);
    }

    private static Instrument Pulse() =>
        new() { Number = 1, Waveform = 0x41, AttackDecay = 0x00, SustainRelease = 0xb8 };

    private static PatternRow[] Held(int holds)
    {
        var rows = new List<PatternRow> { new NoteRow(NoteName.Parse("C-3"), 1) };
        for (int i = 0; i < holds; i++)
            rows.Add(new HoldRow());
        return [.. rows];
    }

    private static List<int> CutoffWrites(List<(int Offset, int Value)> writes) =>
        [.. writes.Where(w => w.Offset == 0x16).Select(w => w.Value)];

    [Fact]
    public void The_filter_sweep_rises_folds_at_the_top_and_comes_back_down()
    {
        var instrument = Pulse();
        instrument.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x10, sweepRate: 32));

        var (writes, sequencer) = Build(instrument, Held(40));

        // The filter steps before the row fetch, same as the wavetable automation - so frame 0
        // (the trigger) only claims the filter, and its first real step waits for frame 1. That
        // is where the setup's own cutoff (0x10) and the sweep's first fold (0x40, immediately
        // overwriting it) both land instead.
        var cutoffs = new List<int>();
        for (int frame = 0; frame < 11; frame++)
        {
            writes.Clear();
            sequencer.Frame();
            cutoffs.AddRange(CutoffWrites(writes));
        }

        // Phase steps 32 at a time and the cutoff is (phase folded) * 2, so it climbs to the
        // top, turns, and comes back - without ever being compared against a bound. TakeLast
        // drops the setup step's own leading 0x10, which the sweep overwrites on that same frame.
        Assert.Equal([0x40, 0x80, 0xc0, 0xfe, 0xbe, 0x7e, 0x3e, 0x00, 0x40, 0x80], cutoffs.TakeLast(10));
    }

    [Fact]
    public void A_setup_without_a_rate_stops_a_sweep_that_was_running()
    {
        var sweeping = Pulse();
        sweeping.Number = 1;
        sweeping.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x10, sweepRate: 32));

        var plain = Pulse();
        plain.Number = 2;
        plain.Filter.Add(FilterStep.Setup(0x1f, 0xf1, 0x30));

        var song = new Song { Speed = 0 };
        song.Instruments.Add(sweeping);
        song.Instruments.Add(plain);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-3"), 1));
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-3"), 2));
        pattern.Rows.AddRange(Enumerable.Range(0, 6).Select(_ => (PatternRow)new HoldRow()));
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));

        for (int frame = 0; frame < 5; frame++)
            sequencer.Frame();      // instrument 1 sweeps, then instrument 2 takes over

        writes.Clear();
        sequencer.Frame();
        sequencer.Frame();

        // Only the second instrument's own setup cutoff, and no moving sweep behind it.
        Assert.DoesNotContain(CutoffWrites(writes), c => c != 0x30);
    }

    [Fact]
    public void A_wide_pulse_sweep_carries_into_the_width_nibble()
    {
        var instrument = Pulse();
        instrument.PulseWidth = 2;
        instrument.PulseSweep = 0x40;

        var (writes, sequencer) = Build(instrument, Held(8));
        sequencer.Frame();

        var expected = new[] { (0x40, 0x02), (0x80, 0x02), (0xc0, 0x02), (0x00, 0x03) };
        foreach (var (low, high) in expected)
        {
            writes.Clear();
            sequencer.Frame();
            Assert.Contains((2, low), writes);
            Assert.Contains((3, high), writes);
        }
    }

    /// <summary>Two note-on rows for the same instrument, a few automation frames apart, so a
    /// retrigger's effect on the running pulse sweep can be observed directly.</summary>
    private static PatternRow[] Retrigger(int instrumentNumber, int holdsBeforeRetrigger, int holdsAfter)
    {
        var rows = new List<PatternRow> { new NoteRow(NoteName.Parse("C-3"), instrumentNumber) };
        rows.AddRange(Enumerable.Range(0, holdsBeforeRetrigger).Select(_ => (PatternRow)new HoldRow()));
        rows.Add(new NoteRow(NoteName.Parse("C-3"), instrumentNumber));
        rows.AddRange(Enumerable.Range(0, holdsAfter).Select(_ => (PatternRow)new HoldRow()));
        return [.. rows];
    }

    private static int LowByteAfter(List<(int Offset, int Value)> writes) =>
        writes.Last(w => w.Offset == 2).Value;

    [Fact]
    public void A_default_pulse_sweep_restarts_from_the_instrument_s_width_on_every_retrigger()
    {
        // 0x40 has a period of 4 in the low byte (256/64), which would land the "before" and
        // "after" samples below back on the same value by pure coincidence regardless of
        // whether a reset happened. 0x11 does not divide 256, so no such aliasing is possible.
        var instrument = Pulse();
        instrument.PulseWidth = 2;
        instrument.PulseSweep = 0x11;

        var (writes, sequencer) = Build(instrument, Retrigger(1, holdsBeforeRetrigger: 3, holdsAfter: 4));

        sequencer.Frame();                          // frame 0: first trigger (row 0)
        writes.Clear();
        sequencer.Frame();                          // frame 1: first note's own first automation step
        int firstNoteFirstStep = LowByteAfter(writes);   // 0x11

        for (int i = 0; i < 3; i++)
            sequencer.Frame();                       // frames 2-4: rest of the hold, then the retrigger (row 4)

        writes.Clear();
        sequencer.Frame();                          // frame 5: retriggered note's own first automation step
        int afterRetrigger = LowByteAfter(writes);

        Assert.Equal(firstNoteFirstStep, afterRetrigger);
    }

    [Fact]
    public void A_continuous_pulse_sweep_keeps_running_across_a_retrigger_instead_of_restarting()
    {
        var instrument = Pulse();
        instrument.PulseWidth = 2;
        instrument.PulseSweep = 0x11;
        instrument.PulseContinuous = true;

        var (writes, sequencer) = Build(instrument, Retrigger(1, holdsBeforeRetrigger: 3, holdsAfter: 4));

        sequencer.Frame();                          // frame 0: first trigger (row 0)
        writes.Clear();
        sequencer.Frame();                          // frame 1: first note's own first automation step
        int firstNoteFirstStep = LowByteAfter(writes);   // 0x11

        for (int i = 0; i < 3; i++)
            sequencer.Frame();                       // frames 2-4: rest of the hold, then the retrigger (row 4)

        writes.Clear();
        sequencer.Frame();                          // frame 5: retriggered note's own first automation step
        int afterRetrigger = LowByteAfter(writes);

        // Had the retrigger reset the sweep, this would also read 0x11 - exactly what the
        // default-behaviour test above confirms it does. Here it must not: five uninterrupted
        // steps of +0x11 land on 0x55.
        Assert.NotEqual(firstNoteFirstStep, afterRetrigger);
        Assert.Equal(0x55, afterRetrigger);
    }
}
