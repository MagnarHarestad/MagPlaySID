using MagSid.Core.SidImport;

namespace MagPlay.Score;

/// <summary>A score played into register writes: as a timeline, or grouped into the machine's
/// frames as a tune's own writes are recorded - so what reads a tune reads a score the same way.</summary>
public static class ScoreTrace
{
    /// <summary>The writes of <paramref name="calls"/> calls, after the player's first writes.</summary>
    public static List<TimedWrite> Timeline(Score score, int calls)
    {
        var player = new ScorePlayer(score);
        var timeline = new List<TimedWrite>(player.Init);
        for (int i = 0; i < calls; i++)
            timeline.AddRange(player.Call());
        return timeline;
    }

    /// <summary><paramref name="frames"/> frames of the machine's, each holding the writes that fell
    /// in it - several calls' for a multispeed tune, a digi's many.</summary>
    public static SidTrace Frames(Score score, int frames)
    {
        int frameCycles = Machines.CyclesPerFrame(score.Machine);
        long end = (long)frames * frameCycles;
        var player = new ScorePlayer(score);
        var grouped = new List<(int, int)>[frames];
        for (int f = 0; f < frames; f++)
            grouped[f] = [];

        while (player.Cycle < end)
        {
            foreach (var write in player.Call())
            {
                long frame = write.Cycle / frameCycles;
                if (frame < frames)
                    grouped[frame].Add((write.Register, write.Value));
            }
        }

        return new SidTrace([.. player.Init.Select(w => (w.Register, w.Value))], grouped);
    }
}
