namespace MagSid.Core.Serialization;

/// <summary>Thrown when a Patterns.s-format source is structurally invalid. Carries a line
/// number when the problem traces to one source line; LineNumber is 0 for cross-reference
/// problems found only after the whole file has been read (see SongValidator).</summary>
public sealed class PatternsFormatException : Exception
{
    public string SourceFile { get; }
    public int LineNumber { get; }

    public PatternsFormatException(string sourceFile, int lineNumber, string message)
        : base(lineNumber > 0 ? $"{sourceFile}:{lineNumber}: {message}" : $"{sourceFile}: {message}")
    {
        SourceFile = sourceFile;
        LineNumber = lineNumber;
    }
}
