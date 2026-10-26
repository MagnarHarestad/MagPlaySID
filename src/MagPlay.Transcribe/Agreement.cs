using MagPlay.Score;

namespace MagPlay.Transcribe;

/// <summary>
/// How far a score's writes are a recording's - measured by playing the score, never by trusting
/// how it was made. A call's own writes are compared register by register: the values each
/// register takes in the call, in order, a write that changes nothing left out, as the SID
/// itself leaves it out. Where in its call a write falls is not compared - the music writes
/// on the call's cycle, a player across the hundreds it runs for. The writes between calls are
/// compared outright, cycle for cycle: a digi is its timing.
/// </summary>
public static class Agreement
{
    /// <param name="CallGroups">A register's writes in a call, wherever either side makes one.</param>
    /// <param name="CallGroupsSame">Of those, the ones where the score's are the tune's.</param>
    /// <param name="Between">Writes between calls in the recording.</param>
    /// <param name="BetweenSame">Of those, made by the score on the same cycle with the same value -
    /// all of them, and nothing else.</param>
    public readonly record struct Result(int CallGroups, int CallGroupsSame, int Between, int BetweenSame, bool BetweenExact)
    {
        public bool Exact => CallGroups == CallGroupsSame && BetweenExact;
    }

    public static Result Measure(Recording recording, MagPlay.Score.Score score)
    {
        var player = new ScorePlayer(score);
        var between = new List<TimedWrite>(player.Init);
        var tuneState = NewState();
        var scoreState = NewState();
        int groups = 0, same = 0;

        for (int call = 0; call < recording.Calls.Count || player.Cycle <= recording.End; call++)
        {
            player.Call();
            between.AddRange(player.BetweenWrites);
            if (call >= recording.Calls.Count)
                continue;

            for (int register = 0; register <= 0x18; register++)
            {
                var tune = Values(recording.CallWrites[call], register, tuneState);
                var played = Values(player.CallWrites, register, scoreState);
                if (tune.Count == 0 && played.Count == 0)
                    continue;
                groups++;
                if (tune.SequenceEqual(played))
                    same++;
            }
        }

        between.RemoveAll(w => w.Cycle > recording.End);
        var wanted = recording.Between.ToHashSet();
        int betweenSame = between.Count(wanted.Contains);
        bool exact = between.SequenceEqual(recording.Between);
        return new Result(groups, same, recording.Between.Count, betweenSame, exact);
    }

    /// <summary>The registers as the SID starts: all 0.</summary>
    internal static int[] NewState() => new int[0x19];

    /// <summary>The values <paramref name="register"/> takes in <paramref name="writes"/>, a write
    /// that leaves it as it was left out; <paramref name="state"/> carried on.</summary>
    internal static List<int> Values(IEnumerable<TimedWrite> writes, int register, int[] state)
    {
        int used = ScorePlayer.UsedBits(register);
        var values = new List<int>();
        foreach (var write in writes)
        {
            if (write.Register != register)
                continue;
            int value = write.Value & used;
            if (value != state[register])
                values.Add(value);
            state[register] = value;
        }
        return values;
    }
}
