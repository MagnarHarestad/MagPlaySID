using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// SignalPathForm.BuildSingleNoteSong and DigiToSidOscForm.BuildPreviewSong both build a
/// throwaway song for their Audition/Trace playback: one triggered note followed by hold rows,
/// on a channel order holding that single pattern. A single-pattern order loops, and neither
/// window's audible window is bounded by anything airtight enough to beat that loop point to a
/// deadline (see project_magsid_editor_timing) - so both pad the pattern with LoopGuardFrames
/// (250) holds, pushing the loop point far past any window the audio is actually heard for.
///
/// This reproduces that exact shape against the real SongSequencer, without depending on the
/// WinForms project: a regression test for "Trace plays the instrument twice" (2026-08-29),
/// which was exactly a single-pattern order looping back within the window Signal Path listens
/// for.
/// </summary>
public class AuditionLoopGuardTests
{
    // Matches SignalPathForm.LoopGuardFrames / DigiToSidOscForm.LoopGuardFrames. Not shared code
    // (those live in a WinForms project this test project cannot reference) - if either of those
    // ever changes, change this too.
    private const int LoopGuardFrames = 250;

    private static Instrument Instr(int number) =>
        new() { Number = number, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa };

    /// <summary>The exact shape BuildSingleNoteSong/BuildPreviewSong produce: a trigger row, then
    /// enough hold rows to cover the traced span plus a release tail plus (optionally) the loop
    /// guard, on a channel order holding only that one pattern - so the order loops back to the
    /// trigger once the pattern runs out.</summary>
    private static (Song Song, SongSequencer Sequencer, List<NoteName> Triggered) SingleNoteLoopingSong(
        int frameCount, int guardFrames)
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(Instr(1));

        var pattern = new Pattern { Number = 1 };
        var note = NoteName.Parse("C-3");
        pattern.Rows.Add(new NoteRow(note, 1));
        for (int i = 0; i < frameCount + 8 + guardFrames; i++)
        {
            pattern.Rows.Add(new HoldRow());
        }
        song.Patterns.Add(pattern);

        for (int ch = 0; ch < 3; ch++)
        {
            song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(1));
        }

        var triggered = new List<NoteName>();
        var sequencer = new SongSequencer(song, (_, _) => { });
        sequencer.RowTriggered += (ch, _, _, _, row) =>
        {
            if (ch == 0 && row is NoteRow(var n, _))
            {
                triggered.Add(n);
            }
        };

        return (song, sequencer, triggered);
    }

    [Fact]
    public void Unpadded_single_pattern_order_retriggers_within_the_audition_window()
    {
        // The pre-fix shape: no guard, so the pattern (24 traced frames + 8 tail = 32 rows/
        // frames at Speed 0) loops back to its own trigger well inside the 36-frame window
        // SignalPathForm.PlayAudition actually keeps the sound playing for (frameCount + 12).
        // A negative control: if this stops retriggering, the rest of this file is not testing
        // anything.
        const int frameCount = 24;
        var (_, sequencer, triggered) = SingleNoteLoopingSong(frameCount, guardFrames: 0);

        for (int i = 0; i < frameCount + 12; i++)
        {
            sequencer.Frame();
        }

        Assert.True(triggered.Count >= 2,
            $"expected the unpadded order to retrigger at least once more within the window, got {triggered.Count} trigger(s)");
    }

    [Fact]
    public void LoopGuardFrames_padding_keeps_the_note_from_retriggering_within_the_audition_window()
    {
        const int frameCount = 24;
        var (_, sequencer, triggered) = SingleNoteLoopingSong(frameCount, guardFrames: LoopGuardFrames);

        for (int i = 0; i < frameCount + 12; i++)
        {
            sequencer.Frame();
        }

        Assert.Single(triggered);
    }

    [Fact]
    public void LoopGuardFrames_padding_survives_a_much_longer_release_tail()
    {
        // DigiToSidOscForm's Audition has no auto-stop timer at all - only the Stop button or
        // the form closing - so its window is however long the user leaves it playing, not a
        // fixed number of frames. This checks the guard holds well past any window either form
        // actually uses (SignalPathForm's stop timer maxes out around frameCount + 12).
        const int frameCount = 24;
        var (_, sequencer, triggered) = SingleNoteLoopingSong(frameCount, guardFrames: LoopGuardFrames);

        for (int i = 0; i < frameCount + 8 + LoopGuardFrames - 1; i++)
        {
            sequencer.Frame();
        }

        Assert.Single(triggered);
    }
}
