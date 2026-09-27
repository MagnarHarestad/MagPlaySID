using MagSid.Core.Model;

namespace MagSid.Core;

/// <summary>
/// Undo/redo as whole-song snapshots rather than per-action inverse operations. The editor applies
/// edits straight to the model from whichever control owns them - the tracker grid mutates
/// Pattern.Rows itself, the instrument panel writes into its own Instrument - so there is no one
/// place an inverse could be recorded, and a single missed call site would corrupt a
/// command-stack silently. A snapshot cannot drift from the thing it snapshots.
///
/// <see cref="Capture"/> is driven by the events the editor already raises *after* a change, so
/// the class holds the previous state itself: that, not the live song, is what an undo returns to.
/// </summary>
public sealed class SongHistory
{
    /// <summary>Snapshots share their rows and tokens (see <see cref="SongCloner"/>), so the cap
    /// is about bounding worst-case memory rather than anything the user would notice.</summary>
    public const int MaxDepth = 200;

    private readonly List<Song> _undo = [];
    private readonly List<Song> _redo = [];

    /// <summary>The song as it stood after the last captured edit.</summary>
    private Song _committed;

    public SongHistory(Song current) => _committed = SongCloner.Clone(current);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>Records that <paramref name="current"/> has just been edited: the state from
    /// before it goes on the undo stack, and the redo stack is dropped, since redoing onto a
    /// changed song would splice together two histories that never met.</summary>
    public void Capture(Song current)
    {
        _undo.Add(_committed);
        if (_undo.Count > MaxDepth)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
        _committed = SongCloner.Clone(current);
    }

    /// <summary>The song to restore, or null when there is nothing to undo. The returned object
    /// is handed over outright - this class keeps a separate clone - so the caller may treat it
    /// as the live song and edit it.</summary>
    public Song? Undo() => Step(_undo, _redo);

    public Song? Redo() => Step(_redo, _undo);

    public void Reset(Song current)
    {
        _undo.Clear();
        _redo.Clear();
        _committed = SongCloner.Clone(current);
    }

    private Song? Step(List<Song> from, List<Song> to)
    {
        if (from.Count == 0)
        {
            return null;
        }

        to.Add(_committed);
        var restored = from[^1];
        from.RemoveAt(from.Count - 1);

        // Cloned, not aliased: `restored` becomes the caller's live song, and a snapshot that
        // the caller can still mutate is no snapshot at all.
        _committed = SongCloner.Clone(restored);
        return restored;
    }
}
