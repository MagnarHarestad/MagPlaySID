using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// The per-frame instrument automation added to the player on 2026-08-20: wavetable, arpeggio,
/// pulse width and filter. Each test pins the behaviour that the assembly's Voice_Automation
/// and music.play filter block actually produce, so the two implementations cannot drift.
/// </summary>
public class InstrumentProgramTests
{
    private static (List<(int Offset, int Value)> Writes, SongSequencer Sequencer) Build(
        Instrument instrument, params PatternRow[] rows)
    {
        var song = new Song { Speed = 0 }; // tick every frame
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

    private static Instrument Plain(byte waveform = 0x21) =>
        new() { Number = 1, Waveform = waveform, AttackDecay = 0x09, SustainRelease = 0xaa };

    [Fact]
    public void The_wavetable_opens_the_gate_on_the_frame_after_the_trigger_then_holds()
    {
        var (writes, sequencer) = Build(Plain(),
            new NoteRow(NoteName.Parse("C-5"), 1), new HoldRow(), new HoldRow(), new HoldRow());

        sequencer.Frame();
        // Hard restart, gate off. Nothing has played on this voice yet, so keeping the waveform
        // leaves $00; the $08 variant sets TEST instead.
        Assert.Contains((4, HardRestart.KeepsWaveform ? 0x00 : 0x08), writes);
        Assert.DoesNotContain(writes, w => w is { Offset: 4, Value: 0x21 | 0x01 });

        writes.Clear();
        sequencer.Frame();
        Assert.Contains((4, 0x21 | 0x01), writes); // the wavetable's one and only step

        writes.Clear();
        sequencer.Frame();
        Assert.DoesNotContain(writes, w => w.Offset == 4); // held - the register is left alone
    }

    [Fact]
    public void An_arpeggio_overrides_the_rows_pitch_one_note_per_frame_and_loops()
    {
        var instrument = Plain();
        instrument.Arpeggio.AddRange([
            NoteName.Parse("C-4"), NoteName.Parse("D#4"), NoteName.Parse("G-4")]);

        // A long-held row, so every frame below is automation rather than a new trigger.
        var (writes, sequencer) = Build(instrument,
            new NoteRow(NoteName.Parse("C-5"), 1),
            new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        sequencer.Frame(); // trigger: the row's own note is written, then overridden below

        var expected = new[] { "C-4", "D#4", "G-4", "C-4", "D#4" };
        foreach (string noteText in expected)
        {
            writes.Clear();
            sequencer.Frame();

            var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse(noteText));
            Assert.Contains((0, (int)lo), writes);
            Assert.Contains((1, (int)hi), writes);
        }
    }

    [Fact]
    public void An_instrument_without_an_arpeggio_leaves_the_frequency_alone_after_the_trigger()
    {
        var (writes, sequencer) = Build(Plain(),
            new NoteRow(NoteName.Parse("C-5"), 1), new HoldRow(), new HoldRow());

        sequencer.Frame();
        writes.Clear();

        sequencer.Frame();
        sequencer.Frame();

        Assert.Empty(writes.Where(w => w.Offset is 0 or 1));
    }

    [Fact]
    public void The_pulse_width_starts_at_the_instruments_setting_and_sweeps_every_frame()
    {
        var instrument = Plain(waveform: 0x41);
        instrument.PulseWidth = 2;
        instrument.PulseSweep = 0x40;

        var (writes, sequencer) = Build(instrument,
            new NoteRow(NoteName.Parse("C-5"), 1),
            new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        sequencer.Frame(); // trigger sets the shadow to $200; nothing written yet

        // $200 + $40 per frame: $240, $280, $2c0, then the low byte wraps and carries into $300.
        var expected = new[] { (0x40, 0x02), (0x80, 0x02), (0xc0, 0x02), (0x00, 0x03) };
        foreach (var (low, high) in expected)
        {
            writes.Clear();
            sequencer.Frame();
            Assert.Contains((2, low), writes);
            Assert.Contains((3, high), writes);
        }
    }

    [Fact]
    public void A_negative_pulse_sweep_borrows_from_the_high_byte_and_wraps_at_12_bits()
    {
        var instrument = Plain(waveform: 0x41);
        instrument.PulseWidth = 0;
        instrument.PulseSweep = -0x10;

        var (writes, sequencer) = Build(instrument,
            new NoteRow(NoteName.Parse("C-5"), 1), new HoldRow(), new HoldRow());

        sequencer.Frame(); // shadow = $000

        // $000 - $10 must wrap to $ff0, not clamp at 0 and not run past 12 bits.
        writes.Clear();
        sequencer.Frame();
        Assert.Contains((2, 0xf0), writes);
        Assert.Contains((3, 0x0f), writes);

        writes.Clear();
        sequencer.Frame();
        Assert.Contains((2, 0xe0), writes);
        Assert.Contains((3, 0x0f), writes);
    }

    [Fact]
    public void A_filter_program_runs_once_per_frame_and_holds_its_last_cutoff()
    {
        var instrument = Plain();
        instrument.Filter.AddRange([
            FilterStep.Setup(0x1f, 0xf1, 0x08),
            FilterStep.Sweep(0x20),
            FilterStep.Sweep(0x40),
        ]);

        var (writes, sequencer) = Build(instrument,
            new NoteRow(NoteName.Parse("C-5"), 1),
            new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        // The filter steps before the row fetch, same as the wavetable automation - so the
        // trigger frame only claims the filter, and the program's first step (the setup) lands
        // on the frame after, matching the wavetable's own first real step.
        writes.Clear();
        sequencer.Frame();
        Assert.Empty(writes.Where(w => w.Offset is 0x16 or 0x17 or 0x18)); // trigger frame: claims only

        writes.Clear();
        sequencer.Frame();
        Assert.Contains((0x18, 0x1f), writes); // mode + volume
        Assert.Contains((0x17, 0xf1), writes); // resonance + routing
        Assert.Contains((0x16, 0x08), writes); // cutoff

        writes.Clear();
        sequencer.Frame();
        Assert.Contains((0x16, 0x20), writes);
        Assert.DoesNotContain(writes, w => w.Offset is 0x17 or 0x18); // sweep steps only move cutoff

        writes.Clear();
        sequencer.Frame();
        Assert.Contains((0x16, 0x40), writes);

        writes.Clear();
        sequencer.Frame();
        Assert.Empty(writes.Where(w => w.Offset is 0x16 or 0x17 or 0x18)); // one-shot: it holds
    }

    [Fact]
    public void An_instrument_without_a_filter_program_leaves_the_running_one_alone()
    {
        // The SID has one filter, so it is global state owned by the last instrument that
        // claimed it - a later note on a plain instrument must not silently reset it.
        var sweeping = Plain();
        sweeping.Number = 1;
        sweeping.Filter.AddRange([FilterStep.Setup(0x1f, 0xf1, 0x08), FilterStep.Sweep(0x20)]);

        var plain = Plain();
        plain.Number = 2;

        var song = new Song { Speed = 0 };
        song.Instruments.Add(sweeping);
        song.Instruments.Add(plain);

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-5"), 1));
        pattern.Rows.Add(new NoteRow(NoteName.Parse("E-5"), 2));
        pattern.Rows.Add(new HoldRow());
        pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);

        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var writes = new List<(int Offset, int Value)>();
        var sequencer = new SongSequencer(song, (o, v) => writes.Add((o, v)));

        // The filter steps before the row fetch, same as the wavetable automation - so
        // instrument 1's trigger frame only claims the filter, and the program's first step
        // lands on the frame after, alongside instrument 2's trigger (which owns no filter
        // program of its own, so it does not disturb the one instrument 1 already claimed).
        writes.Clear();
        sequencer.Frame(); // instrument 1 triggers, claiming the filter
        Assert.Empty(writes.Where(w => w.Offset is 0x16 or 0x17 or 0x18));

        writes.Clear();
        sequencer.Frame(); // step 1 runs; instrument 2 triggers in the same frame, after it
        Assert.Contains((0x16, 0x08), writes);

        writes.Clear();
        sequencer.Frame(); // step 2
        Assert.Contains((0x16, 0x20), writes);

        // Instrument 2 has no FLT section, so the two-step program is simply finished and
        // holding - it must not have been restarted, and must not have been cleared either.
        writes.Clear();
        sequencer.Frame();
        Assert.Empty(writes.Where(w => w.Offset is 0x16 or 0x17 or 0x18));
    }

    [Fact]
    public void Note_off_stops_the_wavetable_but_the_release_keeps_its_pulse_and_note_program()
    {
        var instrument = Plain(waveform: 0x41);
        instrument.Arpeggio.AddRange([NoteName.Parse("C-4"), NoteName.Parse("G-4")]);
        instrument.PulseSweep = 0x10;

        var (writes, sequencer) = Build(instrument,
            new NoteRow(NoteName.Parse("C-5"), 1),
            new NoteOffRow(),
            new HoldRow(), new HoldRow());

        // One row line is one tick, so the bare note row occupies exactly one and the note-off
        // lands on the very next frame.
        sequencer.Frame(); // trigger
        sequencer.Frame(); // note off
        writes.Clear();

        sequencer.Frame();
        sequencer.Frame();

        // The wavetable is parked on a hold, so nothing puts the gate back on - but the pulse
        // sweep and the note program run on through the release, as they do in GoatTracker.
        Assert.Empty(writes.Where(w => w.Offset == 4));
        Assert.Equal(2, writes.Count(w => w.Offset == 2));
        Assert.Equal(2, writes.Count(w => w.Offset == 1));
    }

    [Fact]
    public void Vibrato_runs_on_through_the_release()
    {
        var instrument = Plain(waveform: 0x41);
        instrument.VibratoDelaySpeed = 0x02;
        instrument.VibratoDepth = 0x20;

        var (writes, sequencer) = Build(instrument,
            new NoteRow(NoteName.Parse("C-5"), 1),
            new NoteOffRow(),
            new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        sequencer.Frame(); // trigger
        sequencer.Frame(); // note off
        writes.Clear();

        var frequencies = new List<int>();
        int lo = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            sequencer.Frame();
            lo = writes.LastOrDefault(w => w.Offset == 0).Value;
            frequencies.Add(lo);
        }

        Assert.Empty(writes.Where(w => w.Offset == 4));
        Assert.True(frequencies.Distinct().Count() > 1, "the pitch froze at the note off");
    }
}
