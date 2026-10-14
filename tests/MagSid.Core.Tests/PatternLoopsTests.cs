using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// A pattern unrolled as it plays: one row per tick, a loop's section once per pass, the later
/// passes linked to the first. What the retro view draws so channels with loops line up.
/// </summary>
public class PatternLoopsTests
{
    private static NoteRow N(string note) => new(NoteName.Parse(note), 1);

    private static Pattern Make(params PatternRow[] rows)
    {
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.AddRange(rows);
        return pattern;
    }

    [Fact]
    public void A_counted_loop_plays_its_section_that_many_times_in_all()
    {
        // Outlaws.s PAT01's shape: four rows, LE04 - sixteen ticks.
        var pattern = Make(N("C-2"), new HoldRow(), N("C-1"), new HoldRow(), new LoopEndRow(4));

        var rows = PatternLoops.Unroll(pattern, 0);

        Assert.Equal(16, rows.Count);
        Assert.Equal([0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3], rows.Select(r => r.Row));
        Assert.Equal([1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4], rows.Select(r => r.Pass));
        Assert.All(rows, r => Assert.Equal(4, r.Passes));
        Assert.Equal([4, 8, 12], rows.Select((r, i) => (r, i)).Where(x => x.r.PassStart).Select(x => x.i));
        Assert.False(rows[0].Linked);
        Assert.True(rows[4].Linked);
    }

    [Fact]
    public void Rows_outside_the_loop_play_once()
    {
        var pattern = Make(N("C-3"), new LoopStartRow(), N("D-3"), new HoldRow(), new LoopEndRow(3), N("E-3"));

        var rows = PatternLoops.Unroll(pattern, 0);

        Assert.Equal([0, 2, 3, 2, 3, 2, 3, 5], rows.Select(r => r.Row));
        Assert.Equal(1, rows[0].Passes);
        Assert.Equal(3, rows[1].Passes);
        Assert.Equal(1, rows[^1].Passes);
    }

    [Fact]
    public void An_endless_loop_unrolls_to_the_cap_and_nothing_after_it_is_reached()
    {
        var pattern = Make(N("C-3"), new LoopStartRow(), N("D-3"), new HoldRow(), new LoopEndRow(), N("E-3"));

        var once = PatternLoops.Unroll(pattern, 0);
        var capped = PatternLoops.Unroll(pattern, 9);

        Assert.Equal([0, 2, 3], once.Select(r => r.Row));
        Assert.Equal([0, 2, 3, 2, 3, 2, 3, 2, 3], capped.Select(r => r.Row));
        Assert.All(capped.Skip(1), r => Assert.Equal(-1, r.Passes));
        Assert.DoesNotContain(capped, r => r.Row == 5);
    }

    [Fact]
    public void Compact_is_the_rows_once_without_markers()
    {
        var pattern = Make(N("C-3"), new LoopStartRow(), N("D-3"), new LoopEndRow(3), N("E-3"));

        Assert.Equal([0, 2, 4], PatternLoops.Compact(pattern).Select(r => r.Row));
    }
}
