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

    public static bool HasContent => _rows.Count > 0;

    public static void Copy(Pattern pattern) => _rows = [.. pattern.Rows];

    /// <summary>Writes the copied rows over <paramref name="pattern"/> from <paramref name="row"/>
    /// on, growing it with holds if the copy runs past its end. Returns false when there is nothing
    /// to paste.</summary>
    public static bool PasteInto(Pattern pattern, int row)
    {
        if (_rows.Count == 0 || row < 0)
        {
            return false;
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
