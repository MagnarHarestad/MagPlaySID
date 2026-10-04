using MagSid.Core.Model;

namespace MagSid.Core.Tests;

/// <summary>
/// Both views turn a typed number into the pattern an order entry points at. The rule that matters
/// is contiguity: pattern numbers run 1..N with no gaps, because the assembler asserts it, so a
/// typed number can create at most the one pattern that comes next.
/// </summary>
public class TypedPatternNumberTests
{
    private static Song WithPatterns(int count)
    {
        var song = new Song();
        for (int i = 1; i <= count; i++)
            song.Patterns.Add(Pattern.CreateEmpty(i));
        return song;
    }

    [Fact]
    public void An_existing_number_points_at_that_pattern()
    {
        var song = WithPatterns(3);

        Assert.Equal(2, song.ResolveTypedPatternNumber(2));
        Assert.Equal(3, song.Patterns.Count);
    }

    [Fact]
    public void The_next_number_creates_that_pattern()
    {
        var song = WithPatterns(3);

        Assert.Equal(4, song.ResolveTypedPatternNumber(4));
        Assert.Equal(4, song.Patterns.Count);
        Assert.Equal(4, song.Patterns[^1].Number);
    }

    /// <summary>A two-digit entry arrives one digit at a time. Typing "12" into a song of three
    /// patterns passes through 1 on the way, and must not leave patterns 4 to 12 behind - or even
    /// pattern 4 - just because the keystrokes went by.</summary>
    [Theory]
    [InlineData(12)]
    [InlineData(99)]
    public void A_number_past_the_next_clamps_rather_than_creating(int typed)
    {
        var song = WithPatterns(3);

        Assert.Equal(3, song.ResolveTypedPatternNumber(typed));
        Assert.Equal(3, song.Patterns.Count);
    }

    [Fact]
    public void Nought_clamps_to_the_first_pattern()
    {
        var song = WithPatterns(3);

        Assert.Equal(1, song.ResolveTypedPatternNumber(0));
    }
}
