namespace MagSid.Core.Validation;

/// <summary>A single problem found by <see cref="SongValidator"/> - a cross-reference or
/// numbering problem, as opposed to a single malformed source line (see
/// MagSid.Core.Serialization.PatternsFormatException for those).</summary>
public sealed record ValidationIssue(string Message)
{
    public override string ToString() => Message;
}
