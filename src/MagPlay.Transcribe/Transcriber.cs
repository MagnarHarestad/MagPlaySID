using MagPlay.Score;

namespace MagPlay.Transcribe;

/// <summary>
/// A recording turned into a score that plays it back: lossless by construction. The music -
/// notes, instruments, programs, whatever has been recognised - is played call by call as the
/// score is built, and in each call every register it gets wrong is written in the raw layer as
/// the tune wrote it; the writes between calls go there too. So the score makes the tune's
/// writes whatever the music says, and the raw layer's share is what is not yet understood.
/// </summary>
public static class Transcriber
{
    /// <summary>Cycles a call may sit off an even spacing and still be one of evenly spaced calls:
    /// an IRQ waits for the instruction it arrives in, so a raster's calls are a few cycles apart
    /// from each other.</summary>
    private const int CallJitter = 64;

    /// <summary>What the music makes of the tune, and what the raw layer holds.</summary>
    /// <param name="Changes">Registers in a call that end it on another value - the tune's, the
    /// music's, or both.</param>
    /// <param name="MusicChanges">Of those, the ones the music ends on the tune's value.</param>
    /// <param name="Detail">Registers in a call the music ends right but the tune also writes other
    /// values to on the way - a player's two writes of a frequency in one call - written raw.</param>
    /// <param name="Between">Writes between calls, all raw.</param>
    /// <param name="MissedByRegister">The changes the music misses, per register.</param>
    public sealed record Report(int Calls, int Changes, int MusicChanges, int Detail, int Between,
                                bool EvenCalls, int[] MissedByRegister)
    {
        /// <summary>The share of the calls' changes the music makes.</summary>
        public double Music => Changes == 0 ? 0 : MusicChanges / (double)Changes;
    }

    /// <summary>The recording as a score: <paramref name="music"/> - timed and with its raw layer
    /// filled in here - or, without one, a score that is all raw.</summary>
    public static (MagPlay.Score.Score Score, Report Report) Transcribe(Recording recording, MagPlay.Score.Score? music = null)
    {
        var score = music ?? new MagPlay.Score.Score { Machine = recording.Machine };
        score.Machine = recording.Machine;
        score.ModeVolume = null;
        score.Raw.Between.Clear();
        score.Raw.Calls.Clear();
        bool even = Time(score, recording);
        score.Raw.Between.AddRange(recording.Between);

        var player = new ScorePlayer(score);
        var tuneState = Agreement.NewState();
        var scoreState = Agreement.NewState();
        int changes = 0, musicChanges = 0, detail = 0;
        var missed = new int[0x19];
        for (int call = 0; call < recording.Calls.Count; call++)
        {
            var tune = recording.CallWrites[call];
            long at = player.Cycle;
            player.Call((number, writes) =>
            {
                int replaced = 0;
                for (int register = 0; register <= 0x18; register++)
                {
                    int tuneFrom = tuneState[register], scoreFrom = scoreState[register];
                    var wanted = Agreement.Values(tune, register, tuneState);
                    var made = Agreement.Values(writes, register, (int[])scoreState.Clone());
                    int tuneTo = wanted.Count > 0 ? wanted[^1] : tuneFrom;
                    int scoreTo = made.Count > 0 ? made[^1] : scoreFrom;
                    if (tuneTo != tuneFrom || scoreTo != scoreFrom)
                    {
                        changes++;
                        if (tuneTo == scoreTo)
                            musicChanges++;
                        else
                            missed[register]++;
                    }
                    if (made.SequenceEqual(wanted))
                        continue;

                    replaced |= 1 << register;
                    if (tuneTo == scoreTo)
                        detail++;
                }

                if (replaced == 0)
                    return null;
                var raw = new RawCall(replaced, [.. tune
                    .Where(w => (replaced >> w.Register & 1) != 0)
                    .Select(w => new RawWrite((int)(w.Cycle - at), w.Register, w.Value))]);
                score.Raw.Calls[number] = raw;
                return raw;
            });

            for (int register = 0; register <= 0x18; register++)
                Agreement.Values(player.CallWrites, register, scoreState);
        }

        return (score, new Report(recording.Calls.Count, changes, musicChanges, detail, recording.Between.Count,
                                  even, missed));
    }

    /// <summary>The score's calls on the recording's: evenly spaced when they are, within the
    /// jitter, or listed one by one. True for evenly spaced.</summary>
    private static bool Time(MagPlay.Score.Score score, Recording recording)
    {
        var calls = recording.Calls;
        score.CallCycles = null;
        if (calls.Count == 0)
        {
            score.CyclesPerCall = Machines.CyclesPerFrame(recording.Machine);
            score.FirstCall = recording.End + 1;
            return true;
        }

        score.FirstCall = calls[0];
        if (calls.Count == 1)
        {
            score.CyclesPerCall = Machines.CyclesPerFrame(recording.Machine);
            return true;
        }

        var gaps = Enumerable.Range(1, calls.Count - 1).Select(i => calls[i] - calls[i - 1]).Order().ToList();
        long period = Math.Max(1, gaps[gaps.Count / 2]);
        score.CyclesPerCall = (int)Math.Min(int.MaxValue, period);
        bool even = Enumerable.Range(0, calls.Count).All(i => Math.Abs(calls[i] - (calls[0] + i * period)) <= CallJitter);
        if (!even)
            score.CallCycles = [.. calls];
        return even;
    }
}
