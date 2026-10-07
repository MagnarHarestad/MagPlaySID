namespace MagSid.Core.Model;

/// <summary>
/// One line inside a PATnn: block. Row-per-line (not "note + duration") is deliberate:
/// the "..." hold lines in the file format ARE individually editable steps a tracker
/// grid adds/removes to lengthen/shorten a note, not a derived count.
/// </summary>
public abstract record PatternRow;

/// <summary>A new (or repeated) triggered note: "C-5", 01.</summary>
public sealed record NoteRow(NoteName Note, int InstrumentNumber) : PatternRow;

/// <summary>Extends the previous row's held duration by one row: "...", 00.</summary>
public sealed record HoldRow : PatternRow;

/// <summary>Silences the channel: "---", 00.</summary>
public sealed record NoteOffRow : PatternRow;

/// <summary>
/// A slide, GoatTracker's way: "1xx" slides up and "2xx" down, xx in hex, where a note would
/// be - "120", 00 is up $20 a frame, "210", 00 down $10. It takes its row like a "..." does: the
/// note already sounding carries on, from here gliding by <see cref="Rate"/> added to its 16-bit
/// frequency every frame, until the next note (which starts unslid) or the next slide row.
/// "100" stops it. Rate is -128..127.
/// </summary>
public sealed record SlideRow(int Rate) : PatternRow
{
    public const int MinRate = -128;
    public const int MaxRate = 127;

    /// <summary>How the row reads in the note column and the file: "1xx" or "2xx".</summary>
    public string Text => Rate >= 0 ? $"1{Rate:X2}" : $"2{-Rate:X2}";

    /// <summary>"1xx" up or "2xx" down, xx two hex digits within -128..127. "200", down by
    /// nothing, is a stop as much as "100" is.</summary>
    public static bool TryParse(string text, out SlideRow slide)
    {
        slide = new SlideRow(0);
        if (text.Length != 3 || text[0] is not ('1' or '2'))
            return false;

        if (!int.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out int value))
            return false;

        int rate = text[0] == '1' ? value : -value;
        if (rate is < MinRate or > MaxRate)
            return false;

        slide = new SlideRow(rate);
        return true;
    }
}

/// <summary>Bare "LS" keyword line - moves the in-pattern loop anchor to the next real event.</summary>
public sealed record LoopStartRow : PatternRow;

/// <summary>"LE" keyword line - loops back to the most recently seen LS anchor (or pattern
/// start). Bare "LE" (RepeatCount null) loops forever. "LE04" (RepeatCount = 4) instead plays
/// the marked section a finite number of times *total* (including the first pass through),
/// then falls through past this row instead of looping again.</summary>
public sealed record LoopEndRow(int? RepeatCount = null) : PatternRow;
