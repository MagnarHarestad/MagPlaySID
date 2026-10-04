using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// Which order entry is being played. OrderPos points past it - it is where the next pattern will
/// be fetched from - so the arrange view, reading OrderPos as the entry, lit the clip after the
/// playing one, and nothing at all on a lane of one clip.
/// </summary>
public class PlayingEntryTests
{
    private static Pattern TwoRows(int number, string note)
    {
        var pattern = new Pattern { Number = number };
        pattern.Rows.Add(new NoteRow(NoteName.Parse(note), 1));
        pattern.Rows.Add(new HoldRow());
        return pattern;
    }

    private static Song Build()
    {
        var song = new Song { Speed = 1 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xAA });
        song.Patterns.Add(TwoRows(1, "C-4"));
        song.Patterns.Add(TwoRows(2, "E-4"));
        song.Patterns.Add(TwoRows(3, "G-4"));

        // One clip; two clips; three clips restarting at the second - so the wrap is covered too.
        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.AddRange([new PatternRefToken(1), new PatternRefToken(2)]);
        song.ChannelOrders[2].Tokens.AddRange([new PatternRefToken(3), new PatternRefToken(1), new PatternRefToken(2)]);
        song.ChannelOrders[2].RestartPosition = 2;

        return song;
    }

    [Fact]
    public void The_playing_entry_is_the_one_whose_pattern_is_playing()
    {
        var song = Build();
        var sequencer = new SongSequencer(song, (_, _) => { });
        var seen = new[] { new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };

        for (int frame = 0; frame < 60; frame++)
        {
            sequencer.Frame();
            var captured = sequencer.Capture();

            for (int ch = 0; ch < 3; ch++)
            {
                var channel = captured.Channels[ch];
                var tokens = song.ChannelOrders[ch].Tokens;

                Assert.InRange(channel.PlayingEntry, 0, tokens.Count - 1);
                Assert.Equal(((PatternRefToken)tokens[channel.PlayingEntry]).PatternNumber, channel.CurrentPatternNumber);
                seen[ch].Add(channel.PlayingEntry);
            }
        }

        Assert.Equal([0], seen[0].Order());
        Assert.Equal([0, 1], seen[1].Order());
        Assert.Equal([0, 1, 2], seen[2].Order());
    }
}
