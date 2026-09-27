using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Playback.Tests;

public class SongSequencerTests
{
    private static Instrument MakeInstrument(int number, byte waveform = 0x21, byte ad = 0x09, byte sr = 0xaa) =>
        new() { Number = number, Waveform = waveform, AttackDecay = ad, SustainRelease = sr };

    /// <summary>A minimal, always-valid 1-pattern/1-instrument song with the given
    /// Song.Speed and channel 1 order tokens; channels 2/3 just loop pattern 1 forever so
    /// the sequencer's constructor (which primes all three channels) has something to do.</summary>
    private static (Song Song, List<(int Offset, int Value)> Writes, SongSequencer Sequencer) Build(
        int speed, IEnumerable<PatternRow> patternRows, params OrderToken[] channel1Order)
    {
        var song = new Song { Speed = speed };
        song.Instruments.Add(MakeInstrument(1));

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(patternRows);
        song.Patterns.Add(pattern);

        song.ChannelOrders[0].Tokens.AddRange(channel1Order);
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));
        writes.Clear(); // constructor priming writes nothing observable on a non-stopped channel

        return (song, writes, sequencer);
    }

    [Fact]
    public void Speed_N_ticks_every_N_plus_1_frames()
    {
        // Matches Code\musicplayer.s's "dec Song_Speed / bmi ONbeat.PlaySong": starting from
        // the reload value N, it takes N+1 decrements to go negative, so a tick happens once
        // every N+1 frames - not every N frames as the name might suggest.
        var (_, writes, sequencer) = Build(speed: 2,
            [new NoteRow(NoteName.Parse("C-5"), 1)],
            new PatternRefToken(1));

        // Frame() reports the tick directly. "Did anything get written this frame" no longer
        // answers the question: the wavetable/ADSR/pulse automation writes on every frame, not
        // only on ticks - that is the whole reason it exists.
        var tickedOnFrame = new List<int>();
        for (int frame = 1; frame <= 9; frame++)
        {
            writes.Clear();
            if (sequencer.Frame())
            {
                tickedOnFrame.Add(frame);
            }
        }

        Assert.Equal([1, 4, 7], tickedOnFrame);
    }

    [Fact]
    public void A_note_triggers_frequency_then_HR_frame_A_then_frame_B_on_the_next_tick()
    {
        var (_, writes, sequencer) = Build(speed: 0,
            [new NoteRow(NoteName.Parse("C-5"), 1)],
            new PatternRefToken(1));

        sequencer.Frame(); // speed=0 -> ticks every frame

        var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse("C-5"));
        Assert.Contains((0, (int)lo), writes);
        Assert.Contains((1, (int)hi), writes);
        Assert.Contains((5, 0x00), writes); // HR frame A: AD=0
        Assert.Contains((6, 0x07), writes); // HR frame A: SR=sustain 0, release 7 - not 0,
                                             // to avoid the envelope's ADSR-delay-bug stall
        Assert.Contains((4, 0x08), writes); // HR frame A: TEST bit, gate off

        writes.Clear();
        sequencer.Frame(); // HR frame B commits next tick

        Assert.Contains((5, 0x09), writes); // instrument's AttackDecay
        Assert.Contains((6, 0xaa), writes); // instrument's SustainRelease
        Assert.Contains((4, 0x21 | 0x01), writes); // instrument's waveform, gate bit forced on
    }

    [Fact]
    public void Hold_rows_extend_the_note_and_the_order_restarts_at_position_1_when_it_runs_out()
    {
        // One authored row line is one tick, so 1 note + 2 holds occupies exactly 3 ticks:
        // the trigger tick plus 2 more spent counting down.
        var (_, writes, sequencer) = Build(speed: 0,
            [new NoteRow(NoteName.Parse("C-5"), 1), new HoldRow(), new HoldRow()],
            new PatternRefToken(1));

        var (lo, _) = NotePalTable.GetFreq(NoteName.Parse("C-5"));

        sequencer.Frame(); // tick 1: trigger
        Assert.Contains(writes, w => w.Offset == 0); // freq written

        for (int i = 0; i < 2; i++)
        {
            writes.Clear();
            sequencer.Frame(); // ticks 2,3: holding, no new writes for channel 1
            Assert.DoesNotContain(writes, w => w.Offset is 0 or 1); // no new freq write
        }

        writes.Clear();
        sequencer.Frame(); // tick 4: order (just "1", default RestartPosition=1) runs out ->
                            // restarts at position 1 -> pattern 1 is fetched again, not stopped.
        Assert.Contains((0, (int)lo), writes);
    }

    [Fact]
    public void Song_order_restart_position_loops_back_to_the_specified_pattern_forever()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(MakeInstrument(1));

        var pat1 = new Pattern { Number = 1 };
        pat1.Rows.Add(new NoteRow(NoteName.Parse("C-5"), 1));
        var pat2 = new Pattern { Number = 2 };
        pat2.Rows.Add(new NoteRow(NoteName.Parse("D-5"), 1));
        song.Patterns.Add(pat1);
        song.Patterns.Add(pat2);

        // ORDER_CH1: 1, 2, "restart", 2  -> play pattern 1 once, then loop pattern 2 forever.
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[0].RestartPosition = 2;
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));

        var (cLo, cHi) = NotePalTable.GetFreq(NoteName.Parse("C-5"));
        var (dLo, dHi) = NotePalTable.GetFreq(NoteName.Parse("D-5"));

        writes.Clear();
        sequencer.Frame(); // tick 1: pattern 1 (C-5) triggers
        Assert.Contains((0, (int)cLo), writes);

        writes.Clear();
        sequencer.Frame(); // tick 2: a single row line is a single tick, so pattern 1 is already
                            // done and pattern 2 is fetched here.
        Assert.Contains((0, (int)dLo), writes);

        // From here on, pattern 2 must loop forever - never return to pattern 1.
        bool sawPattern1Again = false;
        for (int i = 0; i < 20; i++)
        {
            writes.Clear();
            sequencer.Frame();
            if (writes.Any(w => w.Offset == 0 && w.Value == cLo))
            {
                sawPattern1Again = true;
            }
        }

        Assert.False(sawPattern1Again);
    }

    [Fact]
    public void In_pattern_ls_le_loops_the_marked_rows_forever_without_advancing_the_order()
    {
        var (_, writes, sequencer) = Build(speed: 0,
            [
                new NoteRow(NoteName.Parse("G-3"), 1), // plays once (before LS)
                new LoopStartRow(),
                new NoteRow(NoteName.Parse("A-3"), 1),
                new NoteRow(NoteName.Parse("B-3"), 1),
                new LoopEndRow(),
            ],
            new PatternRefToken(1));

        var (gLo, _) = NotePalTable.GetFreq(NoteName.Parse("G-3"));
        var (aLo, _) = NotePalTable.GetFreq(NoteName.Parse("A-3"));
        var (bLo, _) = NotePalTable.GetFreq(NoteName.Parse("B-3"));

        sequencer.Frame();
        Assert.Contains((0, (int)gLo), writes);

        bool sawGAgain = false;
        bool sawA = false, sawB = false;
        for (int i = 0; i < 20; i++)
        {
            writes.Clear();
            sequencer.Frame();
            foreach (var (offset, value) in writes)
            {
                if (offset != 0)
                {
                    continue;
                }
                if (value == gLo)
                {
                    sawGAgain = true;
                }
                if (value == aLo)
                {
                    sawA = true;
                }
                if (value == bLo)
                {
                    sawB = true;
                }
            }
        }

        Assert.False(sawGAgain, "G-3 is before the LS anchor and must not replay");
        Assert.True(sawA);
        Assert.True(sawB);
    }

    [Fact]
    public void A_finite_LE_repeat_count_plays_the_section_that_many_times_total_then_falls_through()
    {
        // LS / C-5 / LE03 / D-5: C-5 must trigger exactly 3 times (the loop's total play
        // count, not 3 *extra* repeats) before falling through to D-5, which must then
        // trigger exactly once right after the loop is exhausted.
        var (_, _, sequencer) = Build(speed: 0,
            [
                new LoopStartRow(),
                new NoteRow(NoteName.Parse("C-5"), 1),
                new LoopEndRow(3),
                new NoteRow(NoteName.Parse("D-5"), 1),
            ],
            new PatternRefToken(1));

        var triggeredNotes = new List<NoteName>();
        sequencer.RowTriggered += (ch, _, _, _, row) =>
        {
            if (ch == 0 && row is NoteRow(var note, _))
            {
                triggeredNotes.Add(note);
            }
        };

        for (int i = 0; i < 4; i++)
        {
            sequencer.Frame();
        }

        var c5 = NoteName.Parse("C-5");
        var d5 = NoteName.Parse("D-5");
        Assert.Equal([c5, c5, c5, d5], triggeredNotes);
    }

    [Fact]
    public void A_hold_stranded_after_an_LE_costs_a_tick_instead_of_being_skipped()
    {
        // LS / C-5 / LE02 / "..." / D-5. The hold sits on the far side of the LE from C-5,
        // so C-5's duration lookahead stops short of it and nothing absorbs it. The grid
        // draws it as a row of its own, so it has to spend a tick once the loop falls
        // through - D-5 must land one frame later than it would if the hold vanished.
        var (_, _, sequencer) = Build(speed: 0,
            [
                new LoopStartRow(),
                new NoteRow(NoteName.Parse("C-5"), 1),
                new LoopEndRow(2),
                new HoldRow(),
                new NoteRow(NoteName.Parse("D-5"), 1),
            ],
            new PatternRefToken(1));

        var triggeredAtFrame = new List<(int Frame, PatternRow Row)>();
        int frame = 0;
        sequencer.RowTriggered += (ch, _, _, _, row) =>
        {
            if (ch == 0)
            {
                triggeredAtFrame.Add((frame, row));
            }
        };

        for (frame = 0; frame < 4; frame++)
        {
            sequencer.Frame();
        }

        // One row line is one tick, so rows land on consecutive frames. C-5 twice (the loop's
        // total play count), then the stranded hold, then D-5 - and D-5 arriving at frame 3
        // rather than 2 is exactly the tick the hold costs.
        Assert.Collection(triggeredAtFrame,
            t => { Assert.Equal(0, t.Frame); Assert.IsType<NoteRow>(t.Row); },
            t => { Assert.Equal(1, t.Frame); Assert.IsType<NoteRow>(t.Row); },
            t => { Assert.Equal(2, t.Frame); Assert.IsType<HoldRow>(t.Row); },
            t => { Assert.Equal(3, t.Frame); Assert.Equal(NoteName.Parse("D-5"), Assert.IsType<NoteRow>(t.Row).Note); });
    }

    [Fact]
    public void A_stranded_hold_does_not_retrigger_the_sounding_voice()
    {
        // The wait row must not touch the voice at all - no frequency, ADSR, or gate writes
        // while it is spending its tick, or it would restart the note it is meant to sustain.
        var (_, writes, sequencer) = Build(speed: 0,
            [
                new LoopStartRow(),
                new NoteRow(NoteName.Parse("C-5"), 1),
                new LoopEndRow(1),
                new HoldRow(),
            ],
            new PatternRefToken(1));

        // LE01 is a single total play, so it falls straight through: C-5 is a bare row line
        // lasting one tick, and the stranded hold is reached on the very next frame.
        sequencer.Frame(); // C-5 triggers
        writes.Clear();

        sequencer.Frame(); // the stranded hold - must not retrigger voice 1

        // What a retrigger actually looks like: a new frequency, or $08 on the control register
        // (TEST with the gate off, the hard restart). Everything else on this frame is the
        // automation doing its job - the wavetable's gate-on step lands here now that a bare
        // row line is a single tick, and ADSR and pulse are rewritten every frame regardless.
        Assert.Empty(writes.Where(w => w.Offset is 0 or 1));
        Assert.DoesNotContain((4, 0x08), writes);
    }

    [Fact]
    public void A_pattern_shared_by_two_channels_plays_identically_on_both()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(MakeInstrument(1));

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("E-4"), 1));
        song.Patterns.Add(pattern);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));
        writes.Clear();

        sequencer.Frame();

        var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse("E-4"));

        // Channel 1 -> registers 0/1, channel 2 -> registers 7/8, channel 3 -> registers 14/15.
        Assert.Contains((0, (int)lo), writes);
        Assert.Contains((1, (int)hi), writes);
        Assert.Contains((7, (int)lo), writes);
        Assert.Contains((8, (int)hi), writes);
        Assert.Contains((14, (int)lo), writes);
        Assert.Contains((15, (int)hi), writes);
    }

    [Fact]
    public void NoHardRestart_on_the_outgoing_instrument_skips_the_gate_pulse_too_not_just_ADSR()
    {
        // Changed 2026-08-30: NoHardRestart used to still pulse the gate off/on (TEST, $08),
        // just without forcing AD/SR to the fixed reset pair first - a softer retrigger, but
        // still a retrigger. Magnar asked for a true legato instead: skip the gate pulse as
        // well, so the envelope never restarts at all and just continues on the new pitch.
        var outgoing = MakeInstrument(1); // default ad=$09, sr=$aa - neither collides with the
                                           // reset pair's own $00/$07, so a stray RunAutomation
                                           // write for the outgoing instrument can't hide a bug.
        outgoing.NoHardRestart = true;
        var incoming = MakeInstrument(2);

        var song = new Song { Speed = 0 };
        song.Instruments.Add(outgoing);
        song.Instruments.Add(incoming);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        pattern.Rows.Add(new NoteRow(NoteName.Parse("D-4"), 2));
        song.Patterns.Add(pattern);

        for (int ch = 0; ch < 3; ch++)
            song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));

        sequencer.Frame(); // triggers C-4 on instrument 1 - this is the outgoing instrument next
        writes.Clear();

        sequencer.Frame(); // retriggers D-4 on instrument 2, interrupting instrument 1's NoHardRestart note

        Assert.DoesNotContain((5, 0x00), writes);
        Assert.DoesNotContain((6, 0x07), writes);
        Assert.DoesNotContain((4, 0x08), writes);

        // The one thing that *should* have changed - this is still a new note, just without the
        // reset machinery.
        var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse("D-4"));
        Assert.Contains((0, (int)lo), writes);
        Assert.Contains((1, (int)hi), writes);
    }
}
