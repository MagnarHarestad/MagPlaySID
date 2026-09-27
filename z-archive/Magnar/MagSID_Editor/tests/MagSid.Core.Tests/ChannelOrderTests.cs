using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// Covers ChannelOrder's structural edits, whose whole job is keeping RestartPosition aimed at
/// the entry it was aimed at before. That coupling is invisible in the UI until a song loops
/// to the wrong place on playback, so it's pinned down here rather than left to inspection.
/// </summary>
public class ChannelOrderTests
{
    private static ChannelOrder Order(int[] patternNumbers, int restartPosition = 1)
    {
        var order = new ChannelOrder { ChannelNumber = 1 };
        foreach (int n in patternNumbers)
        {
            order.Tokens.Add(new PatternRefToken(n));
        }
        order.RestartPosition = restartPosition;
        return order;
    }

    private static int[] Numbers(ChannelOrder order) =>
        [.. order.Tokens.Select(t => ((PatternRefToken)t).PatternNumber)];

    [Fact]
    public void Inserting_before_the_restart_target_keeps_it_on_the_same_entry()
    {
        // Restart points at position 3 (pattern 5); inserting ahead of it must follow it to 4.
        var order = Order([1, 3, 5], restartPosition: 3);

        order.InsertEntry(1, 9);

        Assert.Equal([1, 9, 3, 5], Numbers(order));
        Assert.Equal(4, order.RestartPosition);
    }

    [Fact]
    public void Inserting_after_the_restart_target_leaves_it_alone()
    {
        var order = Order([1, 3, 5], restartPosition: 1);

        order.InsertEntry(2, 9);

        Assert.Equal([1, 3, 9, 5], Numbers(order));
        Assert.Equal(1, order.RestartPosition);
    }

    [Fact]
    public void Appending_past_the_end_is_allowed_and_does_not_move_the_restart_target()
    {
        var order = Order([1, 3], restartPosition: 2);

        order.InsertEntry(order.Tokens.Count, 7);

        Assert.Equal([1, 3, 7], Numbers(order));
        Assert.Equal(2, order.RestartPosition);
    }

    [Fact]
    public void Removing_ahead_of_the_restart_target_keeps_it_on_the_same_entry()
    {
        // Restart points at position 3 (pattern 5); dropping entry 1 must follow it back to 2.
        var order = Order([1, 3, 5], restartPosition: 3);

        order.RemoveEntryAt(0);

        Assert.Equal([3, 5], Numbers(order));
        Assert.Equal(2, order.RestartPosition);
    }

    [Fact]
    public void Removing_the_restart_target_itself_lands_on_whatever_takes_its_slot()
    {
        var order = Order([1, 3, 5], restartPosition: 2);

        order.RemoveEntryAt(1);

        Assert.Equal([1, 5], Numbers(order));
        Assert.Equal(2, order.RestartPosition); // now pattern 5, which slid into position 2
    }

    [Fact]
    public void Removing_the_last_entry_clamps_the_restart_position_into_range()
    {
        var order = Order([1, 3], restartPosition: 2);

        order.RemoveEntryAt(1);

        Assert.Equal([1], Numbers(order));
        Assert.Equal(1, order.RestartPosition);
    }

    [Fact]
    public void Removing_an_out_of_range_index_changes_nothing()
    {
        var order = Order([1, 3], restartPosition: 2);

        order.RemoveEntryAt(5);
        order.RemoveEntryAt(-1);

        Assert.Equal([1, 3], Numbers(order));
        Assert.Equal(2, order.RestartPosition);
    }

    [Fact]
    public void Moving_the_restart_target_carries_the_restart_position_with_it()
    {
        var order = Order([1, 3, 5], restartPosition: 1);

        int moved = order.MoveEntry(0, 1);

        Assert.Equal(1, moved);
        Assert.Equal([3, 1, 5], Numbers(order));
        Assert.Equal(2, order.RestartPosition); // still pattern 1
    }

    [Fact]
    public void Moving_an_entry_onto_the_restart_target_swaps_where_the_target_points()
    {
        var order = Order([1, 3, 5], restartPosition: 2);

        int moved = order.MoveEntry(2, -1);

        Assert.Equal(1, moved);
        Assert.Equal([1, 5, 3], Numbers(order));
        Assert.Equal(3, order.RestartPosition); // pattern 3 got pushed down to position 3
    }

    [Fact]
    public void Moving_off_either_end_of_the_list_is_refused()
    {
        var order = Order([1, 3], restartPosition: 1);

        Assert.Equal(-1, order.MoveEntry(0, -1));
        Assert.Equal(-1, order.MoveEntry(1, 1));
        Assert.Equal([1, 3], Numbers(order));
        Assert.Equal(1, order.RestartPosition);
    }
}
