using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Core.Tests;

public class SongHistoryTests
{
    private static Song SongWithSpeed(int speed)
    {
        var song = new Song { Speed = speed };
        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new HoldRow());
        song.Patterns.Add(pattern);
        return song;
    }

    [Fact]
    public void Nothing_to_undo_on_a_fresh_history()
    {
        var history = new SongHistory(SongWithSpeed(6));

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Null(history.Undo());
        Assert.Null(history.Redo());
    }

    [Fact]
    public void Undo_returns_the_state_from_before_the_edit()
    {
        var song = SongWithSpeed(6);
        var history = new SongHistory(song);

        song.Speed = 7;             // the editor mutates first...
        history.Capture(song);      // ...and reports afterwards

        Assert.True(history.CanUndo);
        var restored = history.Undo();
        Assert.NotNull(restored);
        Assert.Equal(6, restored.Speed);
    }

    [Fact]
    public void Undo_then_redo_walks_back_to_the_latest_state()
    {
        var song = SongWithSpeed(1);
        var history = new SongHistory(song);

        foreach (int speed in new[] { 2, 3, 4 })
        {
            song.Speed = speed;
            history.Capture(song);
        }

        Assert.Equal(3, history.UndoDepth);
        Assert.Equal(3, history.Undo()!.Speed);
        Assert.Equal(2, history.Undo()!.Speed);
        Assert.Equal(1, history.Undo()!.Speed);
        Assert.False(history.CanUndo);

        Assert.Equal(2, history.Redo()!.Speed);
        Assert.Equal(3, history.Redo()!.Speed);
        Assert.Equal(4, history.Redo()!.Speed);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void A_new_edit_after_an_undo_drops_the_redo_branch()
    {
        var song = SongWithSpeed(1);
        var history = new SongHistory(song);

        song.Speed = 2;
        history.Capture(song);
        var undone = history.Undo()!;
        Assert.True(history.CanRedo);

        undone.Speed = 9;
        history.Capture(undone);

        Assert.False(history.CanRedo);
        Assert.Equal(1, history.Undo()!.Speed);
    }

    [Fact]
    public void A_restored_song_can_be_edited_without_corrupting_the_history()
    {
        // Undo hands the caller a live song. If the history kept that same object as its own
        // snapshot, the next edit would reach into the thing an undo is supposed to return to.
        var song = SongWithSpeed(1);
        var history = new SongHistory(song);

        song.Speed = 2;
        history.Capture(song);
        song.Speed = 3;
        history.Capture(song);

        var restored = history.Undo()!;
        Assert.Equal(2, restored.Speed);

        restored.Speed = 99;
        restored.Patterns[0].Rows.Add(new NoteOffRow());

        Assert.Equal(1, history.Undo()!.Speed);
        var redone = history.Redo()!;
        Assert.Equal(2, redone.Speed);
        Assert.Single(redone.Patterns[0].Rows);
    }

    [Fact]
    public void Loop_markers_come_back_with_their_repeat_count()
    {
        // The case that started this: removing an LE04 and undoing has to restore LE04, not the
        // bare LE that loops forever.
        var song = SongWithSpeed(6);
        song.Patterns[0].Rows.Add(new LoopEndRow(4));
        var history = new SongHistory(song);

        song.Patterns[0].Rows.RemoveAt(1);
        history.Capture(song);

        var restored = history.Undo()!;
        Assert.Equal(new LoopEndRow(4), restored.Patterns[0].Rows[1]);
    }

    [Fact]
    public void The_stack_stops_growing_at_its_cap()
    {
        var song = SongWithSpeed(0);
        var history = new SongHistory(song);

        for (int i = 1; i <= SongHistory.MaxDepth + 25; i++)
        {
            song.Speed = i % 200;
            history.Capture(song);
        }

        Assert.Equal(SongHistory.MaxDepth, history.UndoDepth);
    }

    [Fact]
    public void Reset_clears_both_directions()
    {
        var song = SongWithSpeed(1);
        var history = new SongHistory(song);
        song.Speed = 2;
        history.Capture(song);
        history.Undo();

        history.Reset(song);

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }
}
