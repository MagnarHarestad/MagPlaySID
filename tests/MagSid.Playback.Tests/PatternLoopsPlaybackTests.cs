using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Playback.Tests;

/// <summary>
/// The retro view's unrolled rows against the sequencer itself: for every pattern of every song
/// in SongData, the row the channel is on at each tick, played, is the row PatternLoops.Unroll
/// puts there. If these ever differ, a stopped or resumed song no longer lines up on screen.
/// </summary>
public class PatternLoopsPlaybackTests
{
    private static string SongDataDir => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "SongData");

    public static TheoryData<string> Songs()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(SongDataDir, "*.s").OrderBy(f => f))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Songs))]
    public void Unrolled_rows_are_the_rows_the_sequencer_plays(string file)
    {
        var song = PatternsFileReader.Read(Path.Combine(SongDataDir, file));

        foreach (var pattern in song.Patterns)
        {
            var unrolled = PatternLoops.Unroll(pattern, 0);
            if (unrolled.Count == 0 || pattern.Rows.Any(r => r is LoopEndRow { RepeatCount: null }))
                continue;

            var played = PlayedRows(song, pattern, unrolled.Count);
            Assert.True(unrolled.Select(r => r.Row).SequenceEqual(played),
                $"{file} PAT{pattern.Number:00}: unrolled {string.Join(",", unrolled.Select(r => r.Row))} / played {string.Join(",", played)}");
        }
    }

    /// <summary>One pattern alone on channel 1, row by row per tick: a trigger names its row, and a
    /// tick that triggers nothing has moved on to the hold after the last one.</summary>
    private static List<int> PlayedRows(Song source, Pattern pattern, int ticks)
    {
        var song = new Song { Speed = 0 };
        song.Instruments.AddRange(source.Instruments);
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(pattern.Number));

        var rows = new List<int>();
        int current = -1;
        bool triggered = false;
        var sequencer = new SongSequencer(song, (_, _) => { });
        sequencer.RowTriggered += (ch, _, row, _, _) =>
        {
            if (ch == 0)
            {
                current = row;
                triggered = true;
            }
        };

        while (rows.Count < ticks)
        {
            triggered = false;
            if (!sequencer.Frame())
                continue;

            if (!triggered)
                current++;

            rows.Add(current);
        }

        return rows;
    }
}
