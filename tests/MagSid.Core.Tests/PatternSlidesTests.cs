using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// The slide lane's view of a pattern - a rate on every row - against the rows the player reads,
/// where a slide row is a change and costs two bytes. And the note edits that must now leave a
/// slide row where it is.
/// </summary>
public class PatternSlidesTests
{
    private static NoteRow N(string note) => new(NoteName.Parse(note), 1);

    private static Pattern Make(params PatternRow[] rows)
    {
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        return pattern;
    }

    private static Pattern EightRowNote() =>
        Make(N("C-4"), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow(), new HoldRow());

    [Fact]
    public void A_run_drawn_at_one_rate_is_a_single_slide_row()
    {
        var pattern = EightRowNote();

        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [2] = 8, [3] = 8, [4] = 8, [5] = 8 });

        Assert.Equal(new SlideRow(8), pattern.Rows[2]);
        Assert.IsType<HoldRow>(pattern.Rows[3]);
        Assert.IsType<HoldRow>(pattern.Rows[5]);
        Assert.Equal(new SlideRow(0), pattern.Rows[6]);   // back to 0 where the drawing stopped
        Assert.Equal([0, 0, 8, 8, 8, 8, 0, 0], PatternSlides.Rates(pattern));
    }

    [Fact]
    public void Changing_one_row_leaves_the_rows_around_it_at_their_rates()
    {
        var pattern = EightRowNote();
        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [1] = 4, [2] = 4, [3] = 4, [4] = 4, [5] = 4, [6] = 4, [7] = 4 });

        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [4] = -16 });

        Assert.Equal([0, 4, 4, 4, -16, 4, 4, 4], PatternSlides.Rates(pattern));
    }

    [Fact]
    public void Drawing_back_to_zero_leaves_plain_holds_and_no_bytes()
    {
        var pattern = EightRowNote();
        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [2] = 8, [3] = 8 });

        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [2] = 0, [3] = 0 });

        Assert.All(pattern.Rows.Skip(1), row => Assert.IsType<HoldRow>(row));
    }

    [Fact]
    public void A_note_and_its_release_take_no_slide_but_the_rows_after_the_release_do()
    {
        var pattern = Make(N("C-4"), new HoldRow(), new NoteOffRow(), new HoldRow(), N("E-4"), new HoldRow());

        Assert.Equal([false, true, false, true, false, true], PatternSlides.Slidable(pattern));

        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [0] = 9, [2] = 9, [3] = 9, [5] = 9 });

        Assert.IsType<NoteRow>(pattern.Rows[0]);
        Assert.IsType<NoteOffRow>(pattern.Rows[2]);
        Assert.Equal(new SlideRow(9), pattern.Rows[3]);
        Assert.Equal(new SlideRow(9), pattern.Rows[5]);
    }

    [Fact]
    public void A_slide_runs_on_through_a_note_off()
    {
        // The player's note off leaves voice_slide alone, and the release now slides with it.
        var pattern = Make(N("C-4"), new SlideRow(8), new NoteOffRow(), new HoldRow(), new SlideRow(0));

        Assert.Equal([0, 8, 8, 8, 0], PatternSlides.Rates(pattern));
    }

    [Fact]
    public void A_slide_before_the_first_note_is_kept_when_nobody_touched_it()
    {
        // It may be stopping a slide the previous pattern left running, which this one cannot see.
        var pattern = Make(new SlideRow(0), new HoldRow(), N("C-4"), new HoldRow(), new HoldRow());

        PatternSlides.SetRates(pattern, new Dictionary<int, int> { [4] = 5 });

        Assert.Equal(new SlideRow(0), pattern.Rows[0]);
        Assert.Equal(new SlideRow(5), pattern.Rows[4]);
    }

    [Fact]
    public void A_note_sounds_on_through_its_slide_rows()
    {
        var pattern = Make(N("C-4"), new SlideRow(8), new HoldRow(), new SlideRow(0), N("E-4"));

        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);
    }

    [Fact]
    public void Lengthening_or_moving_a_note_over_slide_rows_leaves_them_in_place()
    {
        var pattern = Make(N("C-4"), new NoteOffRow(), new SlideRow(8), new HoldRow(), new HoldRow(), new HoldRow());

        PatternNotes.SetLength(pattern, 0, 4);

        Assert.Equal(new SlideRow(8), pattern.Rows[2]);
        Assert.Equal(4, PatternNotes.Read(pattern)[0].Length);

        PatternNotes.SetNote(pattern, 1, NoteName.Parse("D-4"), 1, 3);
        Assert.Equal(new SlideRow(8), pattern.Rows[2]);
    }
}
