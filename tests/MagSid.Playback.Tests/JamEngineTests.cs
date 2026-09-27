using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// Voice allocation and note lifecycle for live jamming - which of the SID's three voices a held
/// note lands on, retriggering, stealing when all three are busy, and release. The actual
/// per-frame automation (wavetable/arpeggio/pulse/filter) is VoiceEngine's and FilterEngine's own
/// logic, already covered via SongSequencerTests since SongSequencer drives the very same code;
/// these tests are about JamEngine's own job on top of that - which voice, and when.
/// </summary>
public class JamEngineTests
{
    private static Instrument MakeInstrument(int number, byte waveform = 0x21, byte ad = 0x09, byte sr = 0xaa) =>
        new() { Number = number, Waveform = waveform, AttackDecay = ad, SustainRelease = sr };

    private static (JamEngine Engine, List<(int Offset, int Value)> Writes) Build()
    {
        var writes = new List<(int Offset, int Value)>();
        var engine = new JamEngine(writeRegister: (o, v) => writes.Add((o, v)));
        writes.Clear(); // constructor priming ($d418 = $0f) isn't part of what a test asserts on
        return (engine, writes);
    }

    private static NoteName Note(string text) => NoteName.Parse(text);

    [Fact]
    public void First_three_notes_take_one_voice_each_in_order()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument);
        engine.NoteOn(Note("E-4"), instrument);
        engine.NoteOn(Note("G-4"), instrument);

        // TriggerNote writes the frequency lo byte unconditionally, at each voice's own base
        // register (0, 7, 14) - the gate itself only opens on the following Automate(), so the
        // frequency write is the reliable "which voice" signal right after NoteOn.
        var usedBases = writes.Where(w => w.Offset is 0 or 7 or 14).Select(w => w.Offset).Distinct().OrderBy(b => b);
        Assert.Equal([0, 7, 14], usedBases);
    }

    [Fact]
    public void A_fourth_note_steals_the_oldest_held_voice()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument); // voice 0, oldest
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        engine.NoteOn(Note("G-4"), instrument); // voice 2
        writes.Clear();

        engine.NoteOn(Note("C-5"), instrument); // no free voice - must steal voice 0

        Assert.Contains(writes, w => w.Offset == 0); // voice 0 retriggered (frequency rewritten)
        Assert.DoesNotContain(writes, w => w.Offset is 7 or 14); // voices 1 and 2 left alone
    }

    [Fact]
    public void Retriggering_an_already_held_note_reuses_its_own_voice_rather_than_stealing_another()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        writes.Clear();

        engine.NoteOn(Note("C-4"), instrument); // held again - same note, same key repeat

        Assert.Contains(writes, w => w.Offset == 0); // voice 0 again
        Assert.DoesNotContain(writes, w => w.Offset is 7 or 14); // voice 1 and the free voice 2 untouched
    }

    [Fact]
    public void NoteOff_releases_only_the_voice_playing_that_note()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);

        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        engine.Frame(); // let each voice's own implicit single-step program complete before release
        writes.Clear();

        engine.NoteOff(Note("C-4"));

        var write = Assert.Single(writes);
        Assert.Equal(4, write.Offset);                 // voice 0's control register
        Assert.Equal(0, write.Value & 0x01);            // gate cleared
    }

    [Fact]
    public void NoteOff_for_a_note_not_currently_held_does_nothing()
    {
        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), MakeInstrument(1));
        writes.Clear();

        engine.NoteOff(Note("G-6")); // never pressed

        Assert.Empty(writes);
    }

    [Fact]
    public void Frame_runs_the_full_wavetable_program_not_just_its_first_step()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]); // three distinct control bytes

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        writes.Clear();

        engine.Frame(); // step 1 of the WAVE program
        var first = writes.Where(w => w.Offset == 4).Select(w => w.Value).ToList();
        writes.Clear();

        engine.Frame(); // step 2 - this is exactly what NotePreviewPlayer.Trigger cannot do
        var second = writes.Where(w => w.Offset == 4).Select(w => w.Value).ToList();

        Assert.Equal([0x81], first);
        Assert.Equal([0x41], second);
    }

    [Fact]
    public void Deferred_NoteOff_holds_off_until_the_wavetable_program_completes()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]); // three steps, gate stays on throughout

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        writes.Clear();

        engine.NoteOff(Note("C-4")); // key released immediately - before even step 1 has run
        Assert.Empty(writes); // deferred: no release write yet, nothing else either

        engine.Frame(); // step 1 (0x81) - program still not complete
        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
        writes.Clear();

        engine.Frame(); // step 2 (0x41) - still not complete
        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
        writes.Clear();

        engine.Frame(); // step 3 (0x11) - the program completes on exactly this frame
        var gateWrites = writes.Where(w => w.Offset == 4).Select(w => w.Value).ToList();
        Assert.Equal([0x11, 0x10], gateWrites); // the program's own last step, then the deferred release right after
    }

    [Fact]
    public void Deferred_release_never_fires_on_its_own_while_the_key_stays_held()
    {
        var instrument = MakeInstrument(1);
        instrument.Wavetable.AddRange([0x81, 0x41, 0x11]);

        var (engine, writes) = Build();
        engine.NoteOn(Note("C-4"), instrument);
        // No NoteOff at all - the key is still held once the program completes.
        engine.Frame();
        engine.Frame();
        writes.Clear();

        engine.Frame(); // program completes on this frame, but nothing ever asked to release

        Assert.DoesNotContain(writes, w => w.Offset == 4 && (w.Value & 0x01) == 0);
    }

    [Fact]
    public void SilenceAll_gates_off_every_held_voice_and_clears_its_slot()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.NoteOn(Note("E-4"), instrument); // voice 1
        writes.Clear();

        engine.SilenceAll();

        // All three control registers written (voice 2 was never triggered, but SilenceAll
        // still touches every slot - IsActive inside VoiceEngine.Silence is what actually
        // guards it, not the caller, so this also proves an idle voice is left alone: only 2
        // gate writes, not 3, even though 3 slots exist).
        var gateWrites = writes.Where(w => w.Offset is 4 or 11 or 18).ToList();
        Assert.Equal(2, gateWrites.Count);
        Assert.All(gateWrites, w => Assert.Equal(0, w.Value & 0x01)); // gate cleared on each
    }

    [Fact]
    public void SilenceAll_frees_every_slot_so_the_same_note_does_not_read_as_still_held()
    {
        var (engine, writes) = Build();
        var instrument = MakeInstrument(1);
        engine.NoteOn(Note("C-4"), instrument); // voice 0
        engine.SilenceAll();
        writes.Clear();

        engine.NoteOn(Note("C-4"), instrument); // must retrigger, not silently no-op

        Assert.Contains(writes, w => w.Offset == 0); // voice 0's frequency written again
    }
}
