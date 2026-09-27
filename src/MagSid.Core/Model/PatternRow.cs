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

/// <summary>Bare "LS" keyword line - moves the in-pattern loop anchor to the next real event.</summary>
public sealed record LoopStartRow : PatternRow;

/// <summary>"LE" keyword line - loops back to the most recently seen LS anchor (or pattern
/// start). Bare "LE" (RepeatCount null) loops forever. "LE04" (RepeatCount = 4) instead plays
/// the marked section a finite number of times *total* (including the first pass through),
/// then falls through past this row instead of looping again.</summary>
public sealed record LoopEndRow(int? RepeatCount = null) : PatternRow;
