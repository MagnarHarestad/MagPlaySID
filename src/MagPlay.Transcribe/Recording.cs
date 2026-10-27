using MagPlay.C64;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.Transcribe;

/// <summary>
/// A tune as it played on the C64: the cycle each call started on, and every SID write, either
/// one of a call's own - made in the IRQ the player runs in - or between calls: the tune's init,
/// a digi streamed from an NMI, a tune that plays from its main loop. A 2SID or 3SID tune's other
/// chips' writes are among them, numbered as the score numbers them, from $20 and $40.
/// </summary>
public sealed class Recording
{
    public required Machine Machine { get; init; }

    /// <summary>The SID chips the tune plays on.</summary>
    public int Chips { get; init; } = 1;

    /// <summary>The cycle each call started on.</summary>
    public required IReadOnlyList<long> Calls { get; init; }

    /// <summary>Each call's own writes, in the order made.</summary>
    public required IReadOnlyList<IReadOnlyList<TimedWrite>> CallWrites { get; init; }

    /// <summary>The writes outside the calls, in the order made.</summary>
    public required IReadOnlyList<TimedWrite> Between { get; init; }

    /// <summary>The cycle the recording ends on.</summary>
    public required long End { get; init; }

    public int WriteCount => CallWrites.Sum(c => c.Count) + Between.Count;

    public static Recording Record(SidFile file, int song, double seconds)
    {
        var tune = SidTune.Load(file, song);
        tune.Run((long)(seconds * Machines.ClockHz(tune.Computer.Machine)));
        return Of(tune.Computer);
    }

    /// <summary>What <paramref name="computer"/> has played so far.</summary>
    public static Recording Of(MagPlay.C64.C64 computer)
    {
        var calls = computer.Calls;
        var callWrites = new List<TimedWrite>[calls.Count];
        for (int i = 0; i < callWrites.Length; i++)
            callWrites[i] = [];
        var between = new List<TimedWrite>();

        // Every chip's writes in the order made: the first chip's, and each other's numbered from
        // $20 a chip.
        int chips = 1 + Math.Min(2, computer.ExtraSidWrites.Count);
        var all = computer.SidWrites.Zip(computer.SidWriteContexts).ToList();
        for (int chip = 1; chip < chips; chip++)
        {
            all.AddRange(computer.ExtraSidWrites[chip - 1].Zip(computer.ExtraSidWriteContexts[chip - 1])
                .Select(w => (w.First with { Register = w.First.Register + chip * 0x20 }, w.Second)));
        }
        if (chips > 1)
            all = [.. all.OrderBy(w => w.First.Cycle)];

        int call = -1;
        foreach (var (write, context) in all)
        {
            while (call + 1 < calls.Count && calls[call + 1] <= write.Cycle)
                call++;
            if (context == WriteContext.Call && call >= 0)
                callWrites[call].Add(write);
            else
                between.Add(write);
        }

        return new Recording
        {
            Machine = computer.Machine,
            Chips = chips,
            Calls = [.. calls],
            CallWrites = callWrites,
            Between = between,
            End = computer.Cycle,
        };
    }
}
