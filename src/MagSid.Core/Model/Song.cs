namespace MagSid.Core.Model;

/// <summary>The root aggregate: everything one SongData\Patterns.s file describes.</summary>
public sealed class Song
{
    public const int ChannelCount = 3;

    public int Speed { get; set; } = 6;
    public List<Instrument> Instruments { get; } = [];
    public List<Pattern> Patterns { get; } = [];

    public ChannelOrder[] ChannelOrders { get; } =
    [
        new() { ChannelNumber = 1 },
        new() { ChannelNumber = 2 },
        new() { ChannelNumber = 3 },
    ];

    /// <summary>
    /// The pattern a typed number should point an order entry at.
    ///
    /// Exactly one past the last pattern creates it - which is how new patterns get made, there is
    /// no separate command for it. Anything higher clamps into the existing range instead: pattern
    /// numbers must stay contiguous 1..N, which the assembler asserts, so only one can be added at
    /// a time. Requiring the exact next number rather than clamping up to it is what stops a
    /// two-digit entry from creating a pattern on its first keystroke and another on its second.
    ///
    /// Here rather than in either view because both views take typed pattern numbers, and two
    /// copies of this rule would be two chances to break the contiguity.
    /// </summary>
    public int ResolveTypedPatternNumber(int typed)
    {
        if (typed == Patterns.Count + 1)
        {
            Patterns.Add(Pattern.CreateEmpty(typed));
            return typed;
        }

        return Math.Clamp(typed, 1, Math.Max(1, Patterns.Count));
    }
}
