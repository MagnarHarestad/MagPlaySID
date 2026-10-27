using MagPlay.C64;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.Transcribe;

/// <summary>
/// A tune as it played on the C64: the cycle each call started on, and every SID write, either
/// one of a call's own or between calls - the tune's init, a digi streamed from an NMI. A 2SID or
/// 3SID tune's other chips' writes are among them, numbered as the score numbers them, from $20
/// and $40.
///
/// A call is a run of the tune's player. Most players run from an IRQ, and each IRQ is a call; a
/// tune whose IRQs write nothing to the SID plays from its main loop, or from an NMI, and there a
/// call is a burst of writes - as long as the bursts come as a player's calls do, two to five
/// hundred a second, and are as short: a player that writes only when a note starts still makes
/// a few, and a digi's NMIs, a sample every few hundred cycles, make long bursts of thousands.
/// </summary>
public sealed class Recording
{
    /// <summary>Cycles without a write that end a burst - a call - of a player outside an IRQ.</summary>
    private const int BurstGap = 4000;

    /// <summary>Bursts a second a player outside an IRQ makes: its calls.</summary>
    private const double FewestCalls = 2, MostCalls = 500;

    /// <summary>Writes a player's call makes at most, on average: three voices and the filter some
    /// written twice, and some voice work besides - Hi Fi Sky's main loop makes 48.</summary>
    private const int MostWritesPerCall = 128;

    public required Machine Machine { get; init; }

    /// <summary>The SID chips the tune plays on.</summary>
    public int Chips { get; init; } = 1;

    /// <summary>What the tune's player runs from - an IRQ, its main loop or an NMI; null when
    /// nothing plays like a player.</summary>
    public WriteContext? Player { get; init; }

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

    /// <summary>
    /// A whole tune: run until it loops - its length measured on the way - or for
    /// <paramref name="maxSeconds"/>, keeping its player's writes only, and the recording cut to
    /// the loop or <paramref name="seconds"/>. What a view of the tune needs, in one run and without
    /// the digi it may stream beside its music. The player is found in its first ten seconds.
    /// </summary>
    public static (Recording Recording, SidSongLength.Result? Length) RecordWhole(SidFile file, int song,
        double seconds = 600, double maxSeconds = C64SongLength.MaxSeconds)
    {
        var player = Record(file, song, Math.Min(10, seconds)).Player;
        var tune = SidTune.Load(file, song);
        tune.Computer.Keep = context => context == player;
        var length = C64SongLength.Measure(tune, maxSeconds);

        double clock = Machines.ClockHz(tune.Computer.Machine);
        double keep = length is null ? seconds : Math.Min(seconds, length.Total.TotalSeconds + 1);
        if (tune.Computer.Cycle < keep * clock)
            tune.Run((long)(keep * clock) - tune.Computer.Cycle);
        return (Of(tune.Computer, player, end: (long)(keep * clock)), length);
    }

    /// <summary>What <paramref name="computer"/> has played so far - up to <paramref name="end"/>
    /// when given - with the player its writes show, unless <paramref name="player"/> is given.</summary>
    public static Recording Of(MagPlay.C64.C64 computer, WriteContext? player = null, long? end = null)
    {
        // Every chip's writes in the order made: the first chip's, and each other's numbered from
        // $20 a chip.
        int chips = 1 + Math.Min(2, computer.ExtraSidWrites.Count);
        long until = Math.Min(end ?? long.MaxValue, computer.Cycle);
        var all = computer.SidWrites.Zip(computer.SidWriteContexts).ToList();
        for (int chip = 1; chip < chips; chip++)
        {
            all.AddRange(computer.ExtraSidWrites[chip - 1].Zip(computer.ExtraSidWriteContexts[chip - 1])
                .Select(w => (w.First with { Register = w.First.Register + chip * 0x20 }, w.Second)));
        }
        if (chips > 1)
            all = [.. all.OrderBy(w => w.First.Cycle)];
        all = [.. all.Where(w => w.First.Cycle < until)];

        player ??= PlayerOf(all, computer.Machine);
        List<long> calls = player switch
        {
            WriteContext.Call => [.. computer.Calls.Where(c => c < until)],
            { } outside => Framed(Bursts(all.Where(w => w.Second == outside).Select(w => w.First.Cycle)),
                                  Machines.CyclesPerFrame(computer.Machine), until),
            _ => [.. computer.Calls.Where(c => c < until)],
        };

        var callWrites = new List<TimedWrite>[calls.Count];
        for (int i = 0; i < callWrites.Length; i++)
            callWrites[i] = [];
        var between = new List<TimedWrite>();

        int call = -1;
        foreach (var (write, context) in all)
        {
            while (call + 1 < calls.Count && calls[call + 1] <= write.Cycle)
                call++;
            if (context == player && call >= 0)
                callWrites[call].Add(write);
            else
                between.Add(write);
        }

        return new Recording
        {
            Machine = computer.Machine,
            Chips = chips,
            Player = player,
            Calls = calls,
            CallWrites = callWrites,
            Between = between,
            End = until,
        };
    }

    /// <summary>What the player runs from: the IRQ when its calls write to the SID at all; else
    /// the main loop or the NMI whose bursts of writes come at a player's rate and are as short -
    /// the one with the more writes, if both do.</summary>
    private static WriteContext? PlayerOf(List<(TimedWrite Write, WriteContext Context)> writes, Machine machine)
    {
        if (writes.Any(w => w.Context == WriteContext.Call))
            return WriteContext.Call;
        if (writes.Count == 0)
            return null;

        double seconds = Math.Max(1e-3, (writes[^1].Write.Cycle - writes[0].Write.Cycle) / Machines.ClockHz(machine));
        return new[] { WriteContext.Main, WriteContext.Nmi }
            .Select(context => (Context: context, Cycles: writes.Where(w => w.Context == context).Select(w => w.Write.Cycle).ToList()))
            .Where(c => Bursts(c.Cycles) is { Count: >= 2 } bursts && bursts.Count / seconds is >= FewestCalls and <= MostCalls
                        && c.Cycles.Count <= bursts.Count * MostWritesPerCall)
            .OrderByDescending(c => c.Cycles.Count)
            .Select(c => (WriteContext?)c.Context)
            .FirstOrDefault();
    }

    /// <summary>
    /// A player's calls from its bursts: when the gaps between them are whole frames - nine in ten
    /// within a tenth of one - the player is called once a frame and writes only when it has
    /// something to say, so the frames between its bursts are calls too, as evenly spread, and
    /// those after its last up to <paramref name="until"/>.
    /// </summary>
    private static List<long> Framed(List<long> bursts, int frame, long until)
    {
        var gaps = bursts.Zip(bursts.Skip(1), (a, b) => b - a).ToList();
        bool framed = gaps.Count > 0 && gaps.Count(g => Math.Abs(g - Math.Round(g / (double)frame) * frame) <= frame / 10.0
                                                          && g >= frame * 0.9) >= gaps.Count * 0.9;
        if (!framed)
            return bursts;

        var calls = new List<long>();
        for (int i = 0; i < bursts.Count; i++)
        {
            calls.Add(bursts[i]);
            if (i + 1 == bursts.Count)
            {
                for (long next = bursts[i] + frame; next < until; next += frame)
                    calls.Add(next);
                break;
            }
            long gap = bursts[i + 1] - bursts[i];
            int frames = (int)Math.Round(gap / (double)frame);
            for (int j = 1; j < frames; j++)
                calls.Add(bursts[i] + gap * j / frames);
        }
        return calls;
    }

    /// <summary>Where each burst of writes starts: a write after a gap of <see cref="BurstGap"/>.</summary>
    private static List<long> Bursts(IEnumerable<long> cycles)
    {
        var starts = new List<long>();
        long? last = null;
        foreach (long cycle in cycles)
        {
            if (last is not { } before || cycle - before > BurstGap)
                starts.Add(cycle);
            last = cycle;
        }
        return starts;
    }
}
