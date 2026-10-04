using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// The edits behind the piano roll's tools - split, glue, clearing a range and drawing a line.
/// Each is checked against the rows it leaves behind, since in this format a row is a note, a
/// hold, a release or a marker and nothing else: a wrong edit does not look wrong, it plays wrong.
/// </summary>
public class RollToolEditTests
{
    private static Pattern PatternOf(params PatternRow[] rows)
    {
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        return pattern;
    }

    private static NoteName N(string text) => NoteName.Parse(text);

    private static NoteRow Note(string text) => new(N(text), 1);

    /// <summary>The rows as short tokens, so a whole pattern can be compared in one line.</summary>
    private static string Shape(Pattern pattern) => string.Join(" ", pattern.Rows.Select(row => row switch
    {
        NoteRow note => note.Note.ToString(),
        HoldRow => "...",
        NoteOffRow => "---",
        LoopStartRow => "LS",
        LoopEndRow => "LE",
        _ => "?",
    }));

    /// <summary>
    /// A drag that grows a note one beat at a time goes through here once per beat. Each step ran
    /// over the release the previous step had left, and the holds behind it then carried the note
    /// on to the end of the pattern - so a drag to the right always ended with a note that never
    /// stopped. The length asked for has to be the length it gets, growing or shrinking.
    /// </summary>
    [Fact]
    public void Growing_a_note_keeps_it_exactly_as_long_as_asked()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        // A drag from the first beat to the fourth, one beat per step - so it ends on a growth.
        for (int length = 1; length <= 4; length++)
            PatternNotes.SetLength(pattern, 0, length);

        Assert.Equal("C-4 ... ... ... --- ...", Shape(pattern));
        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);
    }

    /// <summary>A release after silence releases nothing. Shortening a note that already had one
    /// at its end, or clearing the note it used to stop, would otherwise leave two in a row - a
    /// byte in the export and a second release block in the roll, saying the same thing.</summary>
    [Fact]
    public void Shortening_a_note_does_not_leave_its_old_release_behind()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new HoldRow(), new NoteOffRow(), new HoldRow());

        PatternNotes.SetLength(pattern, 0, 1);

        Assert.Equal("C-4 --- ... ... ...", Shape(pattern));
    }

    [Fact]
    public void Clearing_a_note_takes_the_release_that_stopped_it()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new NoteOffRow(), new HoldRow());

        PatternNotes.ClearRange(pattern, 0, 1);

        Assert.Equal("... ... ... ...", Shape(pattern));
    }

    /// <summary>The note before must not grow into the gap - that is the tracker's clear, and in a
    /// roll it looks like the earlier note changed by itself.</summary>
    [Fact]
    public void Erasing_a_note_leaves_silence_rather_than_a_longer_neighbour()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), Note("E-4"), new HoldRow(), new HoldRow());

        Assert.True(PatternNotes.Erase(pattern, 2));
        Assert.Equal("C-4 ... --- ... ...", Shape(pattern));
    }

    [Fact]
    public void A_moved_note_keeps_its_length_and_leaves_silence_behind()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new NoteOffRow(), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.MoveNotes(pattern, [(0, 2, N("C-4"))]);

        Assert.Equal("... ... C-4 ... --- ...", Shape(pattern));
    }

    /// <summary>Moved one at a time, the first note would measure its length after the second had
    /// been lifted beside it, and come down three rows long over the second's new place.</summary>
    [Fact]
    public void Notes_moved_together_do_not_land_on_each_other()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), Note("E-4"), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.MoveNotes(pattern, [(0, 1, N("C-4")), (2, 3, N("E-4"))]);

        Assert.Equal("... C-4 ... E-4 ... ...", Shape(pattern));
    }

    [Fact]
    public void A_moved_note_can_change_pitch_as_it_goes()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new HoldRow());

        PatternNotes.MoveNotes(pattern, [(0, 0, N("G-4"))]);

        Assert.Equal("G-4 ... ...", Shape(pattern));
    }

    [Fact]
    public void Split_makes_two_notes_of_one()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new HoldRow(), new HoldRow());

        Assert.True(PatternNotes.Split(pattern, 0, 2));
        Assert.Equal("C-4 ... C-4 ...", Shape(pattern));
    }

    [Theory]
    [InlineData(0)]   // its own first row - a note of nothing
    [InlineData(4)]   // past its end
    public void Split_refuses_outside_the_note(int at)
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new HoldRow(), new HoldRow(), Note("E-4"));

        Assert.False(PatternNotes.Split(pattern, 0, at));
        Assert.Equal("C-4 ... ... ... E-4", Shape(pattern));
    }

    [Fact]
    public void Glue_runs_the_note_on_through_the_next()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), Note("E-4"), new HoldRow());

        Assert.True(PatternNotes.Glue(pattern, 0));
        Assert.Equal("C-4 ... ... ...", Shape(pattern));
    }

    [Fact]
    public void Glue_swallows_a_release_between_them()
    {
        var pattern = PatternOf(Note("C-4"), new NoteOffRow(), new HoldRow(), Note("E-4"));

        Assert.True(PatternNotes.Glue(pattern, 0));
        Assert.Equal("C-4 ... ... ...", Shape(pattern));
    }

    [Fact]
    public void Glue_stops_at_a_loop_marker_and_at_the_end()
    {
        var marked = PatternOf(Note("C-4"), new HoldRow(), new LoopEndRow(null), Note("E-4"));
        var alone = PatternOf(Note("C-4"), new HoldRow());

        Assert.False(PatternNotes.Glue(marked, 0));
        Assert.False(PatternNotes.Glue(alone, 0));
        Assert.Equal("C-4 ... LE E-4", Shape(marked));
    }

    [Fact]
    public void Clearing_a_range_stops_what_was_sounding_into_it()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new HoldRow(), new HoldRow(), Note("E-4"), new HoldRow());

        PatternNotes.ClearRange(pattern, 1, 2);

        Assert.Equal("C-4 --- ... ... E-4 ...", Shape(pattern));
    }

    /// <summary>After a release the range is silent already, and saying so again costs a
    /// byte.</summary>
    [Fact]
    public void Clearing_a_range_that_is_already_silent_writes_no_release()
    {
        var pattern = PatternOf(new NoteOffRow(), new HoldRow(), Note("C-4"), new HoldRow());

        PatternNotes.ClearRange(pattern, 2, 3);

        Assert.Equal("--- ... ... ...", Shape(pattern));
    }

    [Fact]
    public void Clearing_a_range_keeps_its_loop_markers()
    {
        var pattern = PatternOf(Note("C-4"), new HoldRow(), new LoopStartRow(), Note("D-4"), Note("E-4"));

        PatternNotes.ClearRange(pattern, 1, 4);

        Assert.Equal("C-4 --- LS ... ...", Shape(pattern));
    }

    [Fact]
    public void A_line_steps_the_pitch_evenly_from_end_to_end()
    {
        var pattern = PatternOf(new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.DrawLine(pattern, 0, N("C-4"), 3, N("D#4"), 1);

        Assert.Equal("C-4 C#4 D-4 D#4", Shape(pattern));
    }

    [Fact]
    public void A_line_drawn_backwards_is_the_same_line()
    {
        var forwards = PatternOf(new HoldRow(), new HoldRow(), new HoldRow());
        var backwards = PatternOf(new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.DrawLine(forwards, 0, N("C-4"), 2, N("E-4"), 1);
        PatternNotes.DrawLine(backwards, 2, N("E-4"), 0, N("C-4"), 1);

        Assert.Equal(Shape(forwards), Shape(backwards));
    }

    /// <summary>Each note ends where the next begins and needs nothing; the last would ring on
    /// into the holds after it without a release.</summary>
    [Fact]
    public void A_line_ends_with_a_release_when_holds_follow_it()
    {
        var pattern = PatternOf(new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.DrawLine(pattern, 0, N("C-4"), 1, N("C-4"), 1);

        Assert.Equal("C-4 C-4 --- ...", Shape(pattern));
    }

    [Fact]
    public void A_line_steps_over_loop_markers()
    {
        var pattern = PatternOf(new HoldRow(), new LoopStartRow(), new HoldRow(), new HoldRow());

        PatternNotes.DrawLine(pattern, 0, N("C-4"), 3, N("D-4"), 1);

        Assert.Equal("C-4 LS C#4 D-4", Shape(pattern));
    }
}
