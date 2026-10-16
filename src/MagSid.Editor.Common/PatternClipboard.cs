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
public static class PatternClipboard
{
    private static List<PatternRow> _rows = [];
    private static List<CopiedNote> _notes = [];
    private static List<CopiedSlide> _slides = [];

    public static bool HasContent => _rows.Count > 0;

    public static void Copy(Pattern pattern) => _rows = [.. pattern.Rows];

    /// <summary>Notes copied in the piano roll, each placed by its distance in beats from the first
    /// of them - so a paste puts the same shape down wherever it is aimed. Kept apart from the
    /// whole-pattern copy: one is notes, the other is a pattern's rows.</summary>
    public static IReadOnlyList<CopiedNote> Notes => _notes;

    public static void CopyNotes(IEnumerable<CopiedNote> notes)
    {
        _notes = [.. notes];
        LastCopied = ClipboardKind.Notes;
    }

    /// <summary>A stretch of the piano roll's slide lane: the rate on each row, placed by its
    /// distance in columns from the first - the same shape wherever it is pasted.</summary>
    public static IReadOnlyList<CopiedSlide> Slides => _slides;

    public static void CopySlides(IEnumerable<CopiedSlide> slides)
    {
        _slides = [.. slides];
        LastCopied = ClipboardKind.Slides;
    }

    /// <summary>Which of the roll's two copies was made last - what Ctrl+V puts down.</summary>
    public static ClipboardKind LastCopied { get; private set; } = ClipboardKind.Notes;

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

/// <summary>One note on the clipboard: how many beats after the first copied note it starts, and
/// what it is.</summary>
public sealed record CopiedNote(int BeatOffset, NoteName Note, int Instrument, int Length);

/// <summary>One row of the slide lane on the clipboard: how many columns after the first copied
/// one it sits, and the rate in force there.</summary>
public sealed record CopiedSlide(int ColumnOffset, int Rate);

public enum ClipboardKind
{
    Notes,
    Slides,
}
