namespace MagSid.Core.Model;

/// <summary>The root aggregate: everything one SongData\Patterns.s file describes.</summary>
public sealed class Song
{
    public const int ChannelCount = 3;

    public int Speed { get; set; } = 6;

    /// <summary>
    /// Funk speed: when set, the song's ticks alternate - a row of <see cref="Speed"/> + 1 frames,
    /// then one of this + 1, and so on from its first row, on every channel at once. Null, or the
    /// same as <see cref="Speed"/>, is a song at one speed. The player pays 6 bytes of code and a
    /// zero-page byte for it, and only a song that has it.
    /// </summary>
    public int? FunkSpeed { get; set; }

    /// <summary>The song swings: two speeds, not one.</summary>
    public bool HasFunk => FunkSpeed is int funk && funk != Speed;

    /// <summary>The speed of the song's tick <paramref name="tick"/>, counted from 0 at its start:
    /// every channel moves one row a tick, so this is also how long the song's row that many rows
    /// in lasts, less one.</summary>
    public int SpeedOfTick(long tick) => HasFunk && (tick & 1) == 1 ? FunkSpeed!.Value : Speed;

    /// <summary>The frame tick <paramref name="tick"/> starts on.</summary>
    public long FrameOfTick(long tick) => HasFunk
        ? tick / 2 * (Speed + FunkSpeed!.Value + 2) + (tick & 1) * (Speed + 1)
        : tick * (Speed + 1);

    /// <summary>Frames a row lasts, on average over a pair with funk speed.</summary>
    public double AverageFramesPerRow => HasFunk ? (Speed + FunkSpeed!.Value + 2) / 2.0 : Speed + 1;
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
