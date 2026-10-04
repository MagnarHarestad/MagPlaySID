using MagSid.Core.Model;

namespace MagSid.Editor.App;

/// <summary>
/// The rows of one copied pattern, shared by both views.
///
/// Static rather than owned by whichever control did the copying: the two views edit the same song
/// and a pattern copied in one is expected to paste into the other. A clipboard per view would be
/// two clipboards that look like one.
///
/// Holds the rows themselves, not a reference to the pattern they came from, so editing or deleting
/// that pattern afterwards cannot change what is about to be pasted.
/// </summary>
internal static class PatternClipboard
{
    private static List<PatternRow> _rows = [];

    // A range copy holds playable rows only and pastes over playable rows only, stepping around
    // loop markers on both ends - a marker is structure, and a range dragged across one is about
    // the notes, not about moving the loop. A whole-pattern copy is the pattern verbatim.
    private static bool _isRange;

    public static bool HasContent => _rows.Count > 0;

    public static void Copy(Pattern pattern)
    {
        _rows = [.. pattern.Rows];
        _isRange = false;
    }

    /// <summary>The playable rows from <paramref name="fromRow"/> to <paramref name="toRow"/> -
    /// what the piano roll's range tool copies.</summary>
    public static void CopyRange(Pattern pattern, int fromRow, int toRow)
    {
        _rows = [.. pattern.Rows
            .Skip(Math.Max(0, fromRow))
            .Take(Math.Max(0, toRow - fromRow + 1))
            .Where(row => row is not (LoopStartRow or LoopEndRow))];
        _isRange = true;
    }

    /// <summary>Writes the copied rows over <paramref name="pattern"/> from <paramref name="row"/>
    /// on, growing it with holds if the copy runs past its end. Returns false when there is nothing
    /// to paste.</summary>
    public static bool PasteInto(Pattern pattern, int row)
    {
        if (_rows.Count == 0 || row < 0)
        {
            return false;
        }

        if (_isRange)
        {
            int next = row;
            foreach (var copied in _rows)
            {
                while (next < pattern.Rows.Count && pattern.Rows[next] is LoopStartRow or LoopEndRow)
                    next++;

                if (next >= pattern.Rows.Count)
                    pattern.Rows.Add(new HoldRow());

                pattern.Rows[next++] = copied;
            }

            return true;
        }

        while (pattern.Rows.Count < row + _rows.Count)
        {
            pattern.Rows.Add(new HoldRow());
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            pattern.Rows[row + i] = _rows[i];
        }

        return true;
    }
}
