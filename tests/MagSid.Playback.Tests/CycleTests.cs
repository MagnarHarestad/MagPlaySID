using MagSid.Core.Model;

namespace MagSid.Playback.Tests;

/// <summary>
/// Cycle in the playback engine: on reaching the right locator's frame the song carries on from
/// the left one's position - a fresh sequencer there, the sound card never stopped - so the range
/// plays again and again with nothing lost or doubled at the jump.
/// </summary>
public class CycleTests
{
    [Fact]
    public void The_engine_plays_the_range_again_from_its_start_every_time_it_reaches_its_end()
    {
        // Speed 0, a note on every one of 32 rows, so each frame triggers exactly one row.
        var song = new Song { Speed = 0 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });
        var pattern = new Pattern { Number = 1 };
        for (int row = 0; row < 32; row++)
            pattern.Rows.Add(new NoteRow(NoteName.AllNotes[30 + row], 1));
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));

        var left = SongSequencer.FindPositionInEntry(song, 0, 0, 8);
        var right = SongSequencer.FindPositionInEntry(song, 0, 0, 12);
        Assert.NotNull(left);
        Assert.NotNull(right);

        var engine = new SongPlaybackEngine(song);
        engine.SetCycle(left, right!.Frame);

        var buffer = new byte[882 * 2];   // one frame of audio
        var rows = new List<int>();
        for (int i = 0; i < 30; i++)
        {
            engine.Read(buffer, 0, buffer.Length);
            while (engine.TryDequeueRowEvent(out var ev))
            {
                if (ev.Channel == 0)
                    rows.Add(ev.RowIndex);
            }
        }

        // Rows 0..11 from the top, then 8..11 over and over. The jump's first frame also names
        // the row held there, which is the one just played again.
        var played = rows.Where((r, i) => i == 0 || !(r == rows[i - 1])).ToList();
        Assert.Equal(Enumerable.Range(0, 12), played.Take(12));
        Assert.All(played.Skip(12), r => Assert.InRange(r, 8, 11));
        Assert.Equal([8, 9, 10, 11, 8, 9, 10, 11], played.Skip(12).Take(8));
    }
}
