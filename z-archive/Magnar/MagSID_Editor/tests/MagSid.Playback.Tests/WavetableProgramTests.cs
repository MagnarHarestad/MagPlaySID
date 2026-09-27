using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// The wavetable and note program running as a pair - the mechanism a drum or a bass transient
/// is actually built from, rather than just an arpeggio. Each test pins what the assembly's
/// Voice_Automation produces, so the two implementations cannot drift.
/// </summary>
public class WavetableProgramTests
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

    private static Instrument Drum() =>
        new() { Number = 1, Waveform = 0x41, AttackDecay = 0x00, SustainRelease = 0xb8 };

    /// <summary>A note held for `holds` further ticks. One row line is one tick, so the note
    /// row itself is the first of them.</summary>
    private static PatternRow[] LongNote(string note, int holds)
    {
        var rows = new List<PatternRow> { new NoteRow(NoteName.Parse(note), 1) };
        for (int i = 0; i < holds; i++)
            rows.Add(new HoldRow());
        return [.. rows];
    }

    [Fact]
    public void A_multi_step_wavetable_writes_one_control_byte_per_frame_then_holds()
    {
        var instrument = Drum();
        instrument.Wavetable.AddRange([0x81, 0x81, 0x41, 0x41]);
        instrument.Arpeggio.Add(NoteName.Parse("C-4"));
        instrument.ArpeggioEnding = ArpeggioEnding.Hold;

        var (writes, sequencer) = Build(instrument, LongNote("C-2", 8));

        sequencer.Frame(); // trigger: hard restart only

        foreach (int expected in new[] { 0x81, 0x81, 0x41, 0x41 })
        {
            writes.Clear();
            sequencer.Frame();
            Assert.Contains((4, expected), writes);
        }

        // Past the end the control register is left alone - that hold is how a drum settles on
        // its final waveform without needing a loop step.
        writes.Clear();
        sequencer.Frame();
        Assert.DoesNotContain(writes, w => w.Offset == 4);
    }

    [Fact]
    public void An_instrument_without_a_wavetable_still_gets_its_single_gate_on_step()
    {
        var instrument = Drum();
        instrument.Waveform = 0x21;

        var (writes, sequencer) = Build(instrument, LongNote("C-2", 3));

        sequencer.Frame();
        writes.Clear();
        sequencer.Frame();

        Assert.Contains((4, 0x21 | 0x01), writes);
    }

    [Fact]
    public void A_Root_ending_settles_the_note_program_onto_the_rows_own_note()
    {
        // The bass shape: one frame of noise at a fixed high pitch, then pulse at whatever the
        // pattern played.
        var instrument = Drum();
        instrument.Wavetable.AddRange([0x81, 0x41]);
        instrument.Arpeggio.Add(NoteName.Parse("C-6"));
        instrument.ArpeggioEnding = ArpeggioEnding.Root;

        var (writes, sequencer) = Build(instrument, LongNote("D#2", 6));

        sequencer.Frame(); // trigger

        var (attackLo, attackHi) = NotePalTable.GetFreq(NoteName.Parse("C-6"));
        writes.Clear();
        sequencer.Frame();
        Assert.Contains((0, (int)attackLo), writes);
        Assert.Contains((1, (int)attackHi), writes);

        // Every frame from here on plays the row's note, for as long as it is held.
        var (rootLo, rootHi) = NotePalTable.GetFreq(NoteName.Parse("D#2"));
        for (int frame = 0; frame < 4; frame++)
        {
            writes.Clear();
            sequencer.Frame();
            Assert.Contains((0, (int)rootLo), writes);
            Assert.Contains((1, (int)rootHi), writes);
        }
    }

    [Fact]
    public void A_Root_ending_uses_the_triggering_row_not_whatever_the_channel_holds_later()
    {
        // The trap this guards: a held note leaves the channel's "current note" as a wait
        // marker, which is not an index into the frequency table at all. A bass reaches its
        // Root step on frame two and stays there, so it would be wrong for the entire note.
        var instrument = Drum();
        instrument.Wavetable.AddRange([0x81, 0x41]);
        instrument.Arpeggio.Add(NoteName.Parse("C-6"));
        instrument.ArpeggioEnding = ArpeggioEnding.Root;

        var (writes, sequencer) = Build(instrument, LongNote("F-3", 5));

        var (rootLo, rootHi) = NotePalTable.GetFreq(NoteName.Parse("F-3"));

        for (int frame = 0; frame < 6; frame++)
            sequencer.Frame();

        writes.Clear();
        sequencer.Frame();
        Assert.Contains((0, (int)rootLo), writes);
        Assert.Contains((1, (int)rootHi), writes);
    }

    [Fact]
    public void A_Hold_ending_stays_on_the_last_step_instead_of_restarting_the_sweep()
    {
        // The kick shape: pitch falls and stays down. Looping here would retrigger the whole
        // sweep every few frames, which is audibly wrong.
        var instrument = Drum();
        instrument.Wavetable.AddRange([0x81, 0x41]);
        instrument.Arpeggio.AddRange([
            NoteName.Parse("C-4"), NoteName.Parse("C-3"), NoteName.Parse("C-2")]);
        instrument.ArpeggioEnding = ArpeggioEnding.Hold;

        var (writes, sequencer) = Build(instrument, LongNote("C-1", 8));

        sequencer.Frame(); // trigger

        foreach (string note in new[] { "C-4", "C-3", "C-2" })
        {
            writes.Clear();
            sequencer.Frame();
            var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse(note));
            Assert.Contains((0, (int)lo), writes);
            Assert.Contains((1, (int)hi), writes);
        }

        var (lastLo, lastHi) = NotePalTable.GetFreq(NoteName.Parse("C-2"));
        for (int frame = 0; frame < 3; frame++)
        {
            writes.Clear();
            sequencer.Frame();
            Assert.Contains((0, (int)lastLo), writes);
            Assert.Contains((1, (int)lastHi), writes);
        }
    }

    [Fact]
    public void A_Loop_ending_cycles_the_note_program_as_a_plain_arpeggio()
    {
        var instrument = Drum();
        instrument.Arpeggio.AddRange([
            NoteName.Parse("C-4"), NoteName.Parse("D#4"), NoteName.Parse("G-4")]);
        instrument.ArpeggioEnding = ArpeggioEnding.Loop;

        var (writes, sequencer) = Build(instrument, LongNote("C-2", 8));

        sequencer.Frame();

        foreach (string note in new[] { "C-4", "D#4", "G-4", "C-4", "D#4" })
        {
            writes.Clear();
            sequencer.Frame();
            var (lo, hi) = NotePalTable.GetFreq(NoteName.Parse(note));
            Assert.Contains((0, (int)lo), writes);
            Assert.Contains((1, (int)hi), writes);
        }
    }
}
