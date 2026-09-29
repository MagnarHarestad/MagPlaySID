using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// The translation between tracker rows and piano-roll blocks.
///
/// This is the one place the two editors genuinely disagree about what a pattern is. A tracker row
/// is a step - a note, then "..." lines that each extend it. A roll has no hold at all: a hold IS
/// the length of the note before it. Every edit crosses that boundary, and a mistake here does not
/// throw or look wrong, it silently rewrites music.
/// </summary>
public class PatternNotesTests
{
    private static Pattern PatternOf(params PatternRow[] rows)
    {
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        return pattern;
    }

    private static NoteName Note(string text) => NoteName.Parse(text);

    [Fact]
    public void A_note_with_holds_after_it_is_one_block()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(), new HoldRow(),
            new NoteRow(Note("E-4"), 2));

        var blocks = PatternNotes.Read(pattern);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(new NoteBlock(0, 3, Note("C-4"), 1), blocks[0]);
        Assert.Equal(new NoteBlock(3, 1, Note("E-4"), 2), blocks[1]);
    }

    /// <summary>Holds beyond a loop marker only play on the pass that reaches them, so a note must
    /// not swallow them - the same rule the byte emitter follows.</summary>
    [Fact]
    public void A_note_does_not_reach_across_a_loop_marker()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(),
            new LoopEndRow(2),
            new HoldRow(), new HoldRow());

        Assert.Equal(2, PatternNotes.Read(pattern)[0].Length);
        Assert.Equal(2, PatternNotes.MaxLengthAt(pattern, 0));
    }

    [Fact]
    public void A_note_off_is_its_own_block_and_not_a_note()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(),
            new NoteOffRow(), new HoldRow());

        Assert.Single(PatternNotes.Read(pattern));
        Assert.Equal(new ReleaseBlock(2, 2), Assert.Single(PatternNotes.Releases(pattern)));
    }

    [Fact]
    public void Writing_a_note_fills_its_length_with_holds()
    {
        var pattern = PatternOf(new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.SetNote(pattern, 1, Note("G-3"), 4, length: 3);

        Assert.Equal(new NoteBlock(1, 3, Note("G-3"), 4), Assert.Single(PatternNotes.Read(pattern)));
        Assert.Equal(4, pattern.Rows.Count);
    }

    /// <summary>A pattern's rows are its length. Lengthening a note consumes what follows rather
    /// than pushing anything along, because there is nowhere for it to go.</summary>
    [Fact]
    public void Writing_never_changes_the_row_count()
    {
        var pattern = PatternOf(new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.SetNote(pattern, 0, Note("C-4"), 1, length: 99);

        Assert.Equal(3, pattern.Rows.Count);
        Assert.Equal(3, PatternNotes.Read(pattern)[0].Length);
    }

    [Fact]
    public void Writing_over_a_note_shortens_the_one_before_it()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.SetNote(pattern, 2, Note("E-4"), 2);

        var blocks = PatternNotes.Read(pattern);
        Assert.Equal(2, blocks[0].Length);
        Assert.Equal(new NoteBlock(2, 2, Note("E-4"), 2), blocks[1]);
    }

    [Fact]
    public void A_note_cannot_be_written_onto_a_loop_marker()
    {
        var pattern = PatternOf(new LoopStartRow(), new HoldRow());

        PatternNotes.SetNote(pattern, 0, Note("C-4"), 1);

        Assert.Empty(PatternNotes.Read(pattern));
        Assert.IsType<LoopStartRow>(pattern.Rows[0]);
    }

    /// <summary>Deleting a note leaves a hold, because a tracker has no blank - and a hold extends
    /// whatever sounded before it, which is why removing a note can make an earlier one longer.
    /// Surprising, and correct.</summary>
    [Fact]
    public void Removing_a_note_lengthens_the_one_before_it()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(),
            new NoteRow(Note("E-4"), 1), new HoldRow());

        PatternNotes.Remove(pattern, 2);

        Assert.Equal(new NoteBlock(0, 4, Note("C-4"), 1), Assert.Single(PatternNotes.Read(pattern)));
    }

    [Fact]
    public void Growing_a_note_consumes_the_rows_after_it()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.SetLength(pattern, 0, 2);
        PatternNotes.SetLength(pattern, 0, 4);

        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);
    }

    /// <summary>
    /// There is no empty row in this format - a hold always extends what came before it - so the
    /// only way to stop a note early is a "---" where it should stop. That is audible, so the API
    /// does it plainly rather than pretending a note can just get shorter.
    /// </summary>
    [Fact]
    public void Shortening_a_note_puts_a_release_where_it_stops()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(), new HoldRow(), new HoldRow());

        Assert.True(PatternNotes.SetLength(pattern, 0, 2));

        Assert.Equal(2, PatternNotes.Read(pattern)[0].Length);
        Assert.Equal(new ReleaseBlock(2, 2), Assert.Single(PatternNotes.Releases(pattern)));
    }

    [Fact]
    public void Setting_the_length_it_already_has_changes_nothing()
    {
        var pattern = PatternOf(new NoteRow(Note("C-4"), 1), new HoldRow());

        Assert.False(PatternNotes.SetLength(pattern, 0, 2));
        Assert.Empty(PatternNotes.Releases(pattern));
    }

    [Fact]
    public void Moving_a_note_keeps_its_length_and_instrument()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 7), new HoldRow(),
            new HoldRow(), new HoldRow());

        PatternNotes.Move(pattern, 0, 2, Note("A-4"));

        Assert.Equal(new NoteBlock(2, 2, Note("A-4"), 7), Assert.Single(PatternNotes.Read(pattern)));
    }

    [Fact]
    public void Loop_markers_survive_every_edit()
    {
        var pattern = PatternOf(
            new LoopStartRow(), new HoldRow(), new HoldRow(),
            new LoopEndRow(3), new HoldRow());

        PatternNotes.SetNote(pattern, 1, Note("C-4"), 1, length: 9);
        PatternNotes.SetRelease(pattern, 4);
        PatternNotes.Remove(pattern, 1);

        Assert.IsType<LoopStartRow>(pattern.Rows[0]);
        Assert.Equal(new LoopEndRow(3), pattern.Rows[3]);
        Assert.Equal(5, pattern.Rows.Count);
    }

    [Fact]
    public void Markers_are_reported_in_row_order_with_their_counts()
    {
        var pattern = PatternOf(
            new HoldRow(), new LoopStartRow(), new HoldRow(),
            new LoopEndRow(4), new LoopEndRow());

        var markers = PatternNotes.Markers(pattern);

        Assert.Equal(3, markers.Count);
        Assert.Equal(new LoopMarker(1, true, null), markers[0]);
        Assert.Equal(new LoopMarker(3, false, 4), markers[1]);
        Assert.Equal(new LoopMarker(4, false, null), markers[2]);
    }

    /// <summary>Holds beyond a marker only play on the pass that reaches them, so dropping a marker
    /// into the middle of a note has to cut it short there - the same rule the byte emitter
    /// follows.</summary>
    [Fact]
    public void A_marker_cuts_a_note_that_reached_past_it()
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(), new HoldRow(), new HoldRow());
        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);

        PatternNotes.SetLoopEnd(pattern, 2, repeatCount: 2);

        Assert.Equal(2, PatternNotes.Read(pattern)[0].Length);
        Assert.Equal(new LoopMarker(2, false, 2), Assert.Single(PatternNotes.Markers(pattern)));
    }

    /// <summary>A repeat count of 0 is already "forever" in the assembled byte, which null spells
    /// here - so asking for 0 gets the bare LE it must have meant rather than a loop that plays
    /// zero times.</summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(-3, null)]
    [InlineData(1, 1)]
    [InlineData(255, 255)]
    [InlineData(900, 255)]
    public void A_repeat_count_outside_the_byte_becomes_something_playable(int asked, int? stored)
    {
        var pattern = PatternOf(new HoldRow(), new HoldRow());

        PatternNotes.SetLoopEnd(pattern, 1, asked);

        Assert.Equal(stored, Assert.Single(PatternNotes.Markers(pattern)).RepeatCount);
    }

    [Fact]
    public void Removing_a_marker_leaves_a_hold_and_says_whether_it_did()
    {
        var pattern = PatternOf(new NoteRow(Note("C-4"), 1), new LoopStartRow(), new HoldRow());

        Assert.True(PatternNotes.RemoveMarker(pattern, 1));
        Assert.Empty(PatternNotes.Markers(pattern));

        // The note can now reach the rows the marker was blocking.
        Assert.Equal(3, PatternNotes.Read(pattern)[0].Length);

        // And a row that never held one says so rather than quietly clearing a note.
        Assert.False(PatternNotes.RemoveMarker(pattern, 0));
        Assert.Single(PatternNotes.Read(pattern));
    }

    [Fact]
    public void A_marker_replaces_whatever_was_on_its_row()
    {
        var pattern = PatternOf(new NoteRow(Note("C-4"), 1), new HoldRow());

        PatternNotes.SetLoopStart(pattern, 0);

        Assert.Empty(PatternNotes.Read(pattern));
        Assert.Equal(2, pattern.Rows.Count);
    }

    /// <summary>Reading, writing back unchanged and reading again has to give the same blocks, or
    /// simply opening a pattern in the roll would alter it.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Reading_and_writing_back_is_a_round_trip(int length)
    {
        var pattern = PatternOf(
            new NoteRow(Note("C-4"), 1), new HoldRow(), new HoldRow(), new HoldRow(),
            new NoteRow(Note("E-4"), 2), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.SetLength(pattern, 0, length);
        var before = PatternNotes.Read(pattern);

        foreach (var block in before)
            PatternNotes.SetNote(pattern, block.Row, block.Note, block.InstrumentNumber, block.Length);

        Assert.Equal(before, PatternNotes.Read(pattern));
    }
}
