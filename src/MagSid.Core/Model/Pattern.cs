namespace MagSid.Core.Model;

/// <summary>
/// A PATnn: block. Patterns are a single pool shared by all channels - the same pattern
/// number can (and is meant to) be referenced from multiple channels' order lists.
/// </summary>
public sealed class Pattern
{
    /// <summary>Rows a freshly created pattern is given. A pattern with none draws as blank
    /// space in the tracker grid, which reads as "nothing works here" rather than "type here" -
    /// so both the new-song pattern and any pattern created while editing start with these.</summary>
    public const int DefaultNewRows = 4;

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
