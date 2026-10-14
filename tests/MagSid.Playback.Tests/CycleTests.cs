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

    private static Song EveryRowSong()
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(new Instrument { Number = 1, Waveform = 0x21, AttackDecay = 0x09, SustainRelease = 0xaa });
        var pattern = new Pattern { Number = 1 };
        for (int row = 0; row < 32; row++)
            pattern.Rows.Add(new NoteRow(NoteName.AllNotes[30 + row], 1));
        song.Patterns.Add(pattern);
        foreach (var order in song.ChannelOrders)
            order.Tokens.Add(new PatternRefToken(1));
        return song;
    }

    /// <summary>The engine's output, one frame of samples at a time.</summary>
    private static List<short[]> Frames(SongPlaybackEngine engine, int frames)
    {
        var result = new List<short[]>();
        var buffer = new byte[882 * 2];
        for (int i = 0; i < frames; i++)
        {
            engine.Read(buffer, 0, buffer.Length);
            var samples = new short[882];
            Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
            result.Add(samples);
        }

        return result;
    }

    [Fact]
    public void A_count_in_holds_the_song_back_by_its_frames()
    {
        var engine = new SongPlaybackEngine(EveryRowSong());
        engine.SetCountIn(16, 4);

        Frames(engine, 24);
        Assert.True(engine.TryDequeueRowEvent(out var first));

        // The song's first row lands after the 16 count-in frames, not at the start.
        Assert.InRange(first.SampleIndex, 16 * 882 - 882, 16 * 882 + 882);
    }

    [Fact]
    public void Clicks_sound_on_the_beats_of_their_range_and_nowhere_else()
    {
        var plain = Frames(new SongPlaybackEngine(EveryRowSong()), 30);
        var clicked = new SongPlaybackEngine(EveryRowSong());
        clicked.SetClick(fromFrame: 8, toFrame: 24, framesPerBeat: 4);
        var withClicks = Frames(clicked, 30);

        var louder = Enumerable.Range(0, 30)
            .Where(f => plain[f].Zip(withClicks[f], (a, b) => Math.Abs(a - b)).Max() > 1000)
            .ToList();

        // A click is 25ms and a frame 20ms, so each one reaches into the frame after its beat.
        Assert.Equal([8, 9, 12, 13, 16, 17, 20, 21], louder);
    }
}
