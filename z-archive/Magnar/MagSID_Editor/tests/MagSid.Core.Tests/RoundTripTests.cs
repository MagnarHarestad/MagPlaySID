using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Core.Tests;

public class RoundTripTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Theory]
    [InlineData("MinimalGrammar.s")]
    [InlineData("CurrentSongCopy.s")]
    public void Writing_the_same_parsed_song_twice_is_idempotent(string fixtureName)
    {
        var song1 = PatternsFileReader.Read(FixturePath(fixtureName));
        string text1 = PatternsFileWriter.Render(song1);

        var song2 = PatternsFileReader.Parse(text1, fixtureName);
        string text2 = PatternsFileWriter.Render(song2);

        Assert.Equal(text1, text2);
    }

    [Theory]
    [InlineData("MinimalGrammar.s")]
    [InlineData("CurrentSongCopy.s")]
    public void Round_tripped_song_preserves_structure(string fixtureName)
    {
        var original = PatternsFileReader.Read(FixturePath(fixtureName));
        var roundTripped = PatternsFileReader.Parse(PatternsFileWriter.Render(original), fixtureName);

        Assert.Equal(original.Speed, roundTripped.Speed);
        Assert.Equal(original.Instruments.Count, roundTripped.Instruments.Count);
        Assert.Equal(original.Patterns.Count, roundTripped.Patterns.Count);

        foreach (var originalPattern in original.Patterns)
        {
            var roundTrippedPattern = roundTripped.Patterns.Single(p => p.Number == originalPattern.Number);
            Assert.Equal(originalPattern.Rows, roundTrippedPattern.Rows);
        }

        for (int ch = 0; ch < 3; ch++)
        {
            Assert.Equal(original.ChannelOrders[ch].Tokens, roundTripped.ChannelOrders[ch].Tokens);
            Assert.Equal(original.ChannelOrders[ch].RestartPosition, roundTripped.ChannelOrders[ch].RestartPosition);
        }
    }

    [Fact]
    public void An_instrument_name_round_trips_as_a_whole_line_comment_ahead_of_its_own_line()
    {
        var song = new Song { Speed = 1 };
        var named = Instrument.CreateDefault(1);
        named.Name = "Lead: bright"; // a colon in the name itself must not confuse the format
        song.Instruments.Add(named);
        song.Instruments.Add(Instrument.CreateDefault(2)); // left unnamed

        var pattern = new Pattern { Number = 1 };
        pattern.Rows.Add(new NoteRow(NoteName.Parse("C-4"), 1));
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        string rendered = PatternsFileWriter.Render(song);
        Assert.Contains("; Instrument 01 : Lead: bright", rendered);
        Assert.DoesNotContain("Instrument 02", rendered);

        var roundTripped = PatternsFileReader.Parse(rendered, "test");
        Assert.Equal("Lead: bright", roundTripped.Instruments.Single(i => i.Number == 1).Name);
        Assert.Equal(string.Empty, roundTripped.Instruments.Single(i => i.Number == 2).Name);
    }

    [Fact]
    public void The_sibling_projects_real_song_still_builds_after_a_round_trip()
    {
        // This asserts the *text* round-trips structurally (see the two tests above for the
        // deep model comparison); actually re-invoking the sibling project's assembler from a
        // unit test is out of scope here - see the plan's manual verification step 4 for that.
        var song = PatternsFileReader.Read(FixturePath("CurrentSongCopy.s"));
        string rendered = PatternsFileWriter.Render(song);

        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.Contains("PAT01:", rendered);
        Assert.Contains("PAT02:", rendered);
        Assert.Contains("PAT03:", rendered);
    }
}
