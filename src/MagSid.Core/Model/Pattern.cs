namespace MagSid.Core.Model;

/// <summary>
/// A PATnn: block. Patterns are a single pool shared by all channels - the same pattern
/// number can (and is meant to) be referenced from multiple channels' order lists.
/// </summary>
public sealed class Pattern
{
    /// <summary>Rows a freshly created pattern is given: one bar of sixteen, which is what the DAW
    /// view's grid counts in. Both the new song's patterns and any pattern created while editing
    /// start with these.</summary>
    public const int DefaultNewRows = 16;

    public int Number { get; set; }
    public List<PatternRow> Rows { get; } = [];

    /// <summary>A new pattern with <see cref="DefaultNewRows"/> empty (hold) rows.</summary>
    public static Pattern CreateEmpty(int number)
    {
        var pattern = new Pattern { Number = number };
        for (int i = 0; i < DefaultNewRows; i++)
        {
            pattern.Rows.Add(new HoldRow());
        }
        return pattern;
    }
}
