using MagSid.Core.Model;

namespace MagSid.Core;

/// <summary>
/// How far each of one instrument's programs got during a play-through. -1 means that program
/// never played a step at all, which is not the same as "reached step 0" and must not be treated
/// as evidence of anything.
///
/// Plain data so this stays in Core: the editor builds it from MagSid.Playback's own
/// ProgramCoverage, which is what actually watches the frames go by.
/// </summary>
public sealed record InstrumentUsage(
    int HighestWavetableStep, int HighestArpeggioStep, int HighestFilterStep);

/// <summary>
/// Shrinks a song's pattern data without changing a note of what it plays: dead LS markers go, a
/// run of identical rows becomes an LS/LE loop, and a pattern the song order plays several times
/// in a row becomes one order entry over a pattern that loops itself - each only where that is
/// actually cheaper.
///
/// Every loop is scored by re-encoding the pattern through <see cref="SongSizeCalculator"/>, not
/// estimated, and one is applied only when the byte count really drops - so the result can never
/// come out bigger than what went in. That matters more than it sounds: the row encoding is
/// already dense (a run of "..." holds folds into the preceding note's duration for free), while
/// an LE costs a flat three bytes plus any state fixup, so most repeats are not worth looping and
/// a finder that trusted row counts would make songs larger.
/// </summary>
public static class SongOptimizer
{
    /// <summary>One thing that was done, or could be.</summary>
    public sealed record Finding(int PatternNumber, string Description, int BytesSaved);

    public sealed record Result(IReadOnlyList<Finding> Findings, SongSize Before, SongSize After)
    {
        public int BytesSaved => Before.TotalBytes - After.TotalBytes;
        public bool FoundAnything => Findings.Count > 0;
    }

    /// <summary>What Apply would do, without touching the song.</summary>
    public static Result Analyze(Song song, IReadOnlyDictionary<int, InstrumentUsage>? usage = null) =>
        Run(SongCloner.Clone(song), SongSizeCalculator.Measure(song), usage);

    public static Result Apply(Song song, IReadOnlyDictionary<int, InstrumentUsage>? usage = null) =>
        Run(song, SongSizeCalculator.Measure(song), usage);

    private static Result Run(Song song, SongSize before,
        IReadOnlyDictionary<int, InstrumentUsage>? usage)
    {
        var findings = new List<Finding>();

        if (usage is not null)
        {
            TrimUnplayedProgramSteps(song, usage, findings);
        }

        foreach (var pattern in song.Patterns)
        {
            int dead = RemoveDeadLoopStarts(pattern.Rows);
            if (dead > 0)
            {
                findings.Add(new Finding(pattern.Number,
                    dead == 1 ? "1 dead LS marker removed" : $"{dead} dead LS markers removed", 0));
            }

            // Only patterns with no loops of their own. An LE jumps to whichever LS it last
            // passed, so dropping a new LS into a pattern that already has one can silently
            // re-aim an existing LE - and loops cannot nest, there is a single anchor. The songs
            // that already use loops turn out to be at their optimum anyway.
            if (pattern.Rows.Any(r => r is LoopStartRow or LoopEndRow))
            {
                continue;
            }

            while (BestLoop(pattern.Rows) is { } loop)
            {
                pattern.Rows.RemoveRange(loop.Start, loop.BlockLength * loop.Repeats);
                pattern.Rows.Insert(loop.Start, new LoopStartRow());
                pattern.Rows.InsertRange(loop.Start + 1, loop.Body);
                pattern.Rows.Insert(loop.Start + 1 + loop.BlockLength, new LoopEndRow(loop.Repeats));

                findings.Add(new Finding(pattern.Number,
                    $"rows {loop.Start + 1}-{loop.Start + loop.BlockLength * loop.Repeats}: " +
                    $"{loop.Repeats} x {loop.BlockLength} identical rows looped",
                    loop.Saved));
            }
        }

        // After the in-pattern loops, not before: loops cannot nest, so a pattern that just got
        // one is no longer a candidate here, and the in-pattern loop is the one that can save more.
        LoopOrderRuns(song, findings);

        return new Result(findings, before, SongSizeCalculator.Measure(song));
    }

    /// <summary>A stretch of one channel's order playing the same pattern entry after entry.</summary>
    private sealed record OrderRun(int Channel, int Start, int Length);

    /// <summary>
    /// The song order playing a pattern several times in a row - 07, 07, 07, 07 - becomes one entry
    /// whose pattern loops itself: LS on its first row, LE with the count after its last. Each order
    /// entry is a byte and the LE three, so it pays from five in a row; fewer cost as much or more,
    /// and the measurement below turns them down.
    ///
    /// Only where every place the pattern is played is such a run - in any channel - and all of
    /// them are whole multiples of the same block, since the pattern itself changes: a lone entry
    /// elsewhere would start looping too. Two runs of two and one of four all work, as block 2. A
    /// restart position has to land on a block boundary, or the collapsed order would restart
    /// part-way through a pass that no longer exists as an entry.
    /// </summary>
    private static void LoopOrderRuns(Song song, List<Finding> findings)
    {
        foreach (int number in song.Patterns.Select(p => p.Number).ToList())
        {
            var pattern = song.Patterns.First(p => p.Number == number);
            if (pattern.Rows.Count == 0 || pattern.Rows.Any(r => r is LoopStartRow or LoopEndRow))
                continue;

            var runs = RunsOf(song, number);
            if (runs.Count == 0)
                continue;

            int block = runs.Min(r => r.Length);
            if (block < 2 || block > 255 || runs.Any(r => r.Length % block != 0))
                continue;

            if (runs.Any(r => !RestartOnBlockBoundary(song.ChannelOrders[r.Channel], r, block)))
                continue;

            var trial = SongCloner.Clone(song);
            CollapseRuns(trial, number, runs, block);
            int saved = SongSizeCalculator.Measure(song).TotalBytes - SongSizeCalculator.Measure(trial).TotalBytes;
            if (saved <= 0)
                continue;

            CollapseRuns(song, number, runs, block);

            int entries = runs.Sum(r => r.Length);
            string where = string.Join(", ", runs.Select(r => $"CH{r.Channel + 1}").Distinct());
            findings.Add(new Finding(number,
                $"song order {where}: {entries} entries in {runs.Count} run{(runs.Count == 1 ? "" : "s")} " +
                $"-> {entries / block}, the pattern loops itself (LS/LE{block:00})",
                saved));
        }
    }

    /// <summary>Every maximal stretch of entries playing this pattern, in every channel - lone
    /// entries included, as runs of one, so a pattern also played singly is never a candidate.</summary>
    private static List<OrderRun> RunsOf(Song song, int patternNumber)
    {
        var runs = new List<OrderRun>();
        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            var tokens = song.ChannelOrders[channel].Tokens;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] is not PatternRefToken(var n) || n != patternNumber)
                    continue;

                int start = i;
                while (i + 1 < tokens.Count && tokens[i + 1] is PatternRefToken(var next) && next == patternNumber)
                    i++;

                runs.Add(new OrderRun(channel, start, i - start + 1));
            }
        }

        return runs;
    }

    private static bool RestartOnBlockBoundary(ChannelOrder order, OrderRun run, int block)
    {
        int restart = order.RestartPosition - 1;
        return restart < run.Start || restart >= run.Start + run.Length || (restart - run.Start) % block == 0;
    }

    /// <summary>Loops the pattern and shortens every run to one entry per block, carrying each
    /// channel's restart position to the entry it pointed at.</summary>
    private static void CollapseRuns(Song song, int patternNumber, List<OrderRun> runs, int block)
    {
        var pattern = song.Patterns.First(p => p.Number == patternNumber);
        pattern.Rows.Insert(0, new LoopStartRow());
        pattern.Rows.Add(new LoopEndRow(block));

        foreach (var channelRuns in runs.GroupBy(r => r.Channel))
        {
            var order = song.ChannelOrders[channelRuns.Key];
            int restart = order.RestartPosition - 1;
            var kept = new List<OrderToken>();
            int newRestart = 0;

            for (int i = 0; i < order.Tokens.Count; i++)
            {
                if (i == restart)
                    newRestart = kept.Count;

                var run = channelRuns.FirstOrDefault(r => i >= r.Start && i < r.Start + r.Length);
                if (run is null || (i - run.Start) % block == 0)
                    kept.Add(order.Tokens[i]);
            }

            order.Tokens.Clear();
            order.Tokens.AddRange(kept);
            order.RestartPosition = newRestart + 1;
        }
    }

    /// <summary>
    /// Drops program steps a play-through never reached. Only the *tail* of a program, and only
    /// for an instrument that actually played: a gap in the middle means something stranger than
    /// an over-long program, and removing it would shift every step after it.
    ///
    /// This is sound only because a program that runs out holds its last step - the player leaves
    /// the control register alone once a WAVE program ends, and the filter keeps its final cutoff -
    /// so cutting a tail nothing ever reached changes nothing about what is heard. What it does
    /// depend on is the evidence being complete: a play-through that stopped early, or a song
    /// whose longest note comes later, leaves steps unreached that the song genuinely uses. The
    /// caller is responsible for only offering this after a full pass.
    ///
    /// A looping note program is left alone: it cycles, so anything unreached means the loop never
    /// came round, and shortening the list would move the loop rather than trim dead weight.
    /// </summary>
    private static void TrimUnplayedProgramSteps(Song song,
        IReadOnlyDictionary<int, InstrumentUsage> usage, List<Finding> findings)
    {
        foreach (var instrument in song.Instruments)
        {
            if (!usage.TryGetValue(instrument.Number, out var used))
            {
                continue;
            }

            Trim(instrument.Wavetable.Count, used.HighestWavetableStep, "wavetable",
                count => instrument.Wavetable.RemoveRange(count, instrument.Wavetable.Count - count));

            Trim(instrument.Filter.Count, used.HighestFilterStep, "filter program",
                count => instrument.Filter.RemoveRange(count, instrument.Filter.Count - count));

            if (instrument.ArpeggioEnding != ArpeggioEnding.Loop)
            {
                Trim(instrument.Arpeggio.Count, used.HighestArpeggioStep, "note program",
                    count => instrument.Arpeggio.RemoveRange(count, instrument.Arpeggio.Count - count));
            }

            void Trim(int have, int highestReached, string what, Action<int> removeFrom)
            {
                // -1 is "never played a step", which says nothing about whether the program is
                // too long - only that this play-through did not exercise it.
                if (highestReached < 0 || have <= highestReached + 1)
                {
                    return;
                }

                int keep = highestReached + 1;
                removeFrom(keep);
                findings.Add(new Finding(0,
                    $"instrument {instrument.Number:D2}: {what} {have} -> {keep} steps " +
                    $"({have - keep} never played)", 0));
            }
        }
    }

    /// <summary>
    /// An LS only matters if some LE reaches it before the next LS replaces it. One that never
    /// does is invisible to the player - it emits no byte at all, it only moves a compile-time
    /// anchor - but it reads like a loop that is not there, which is the whole reason to drop it.
    /// </summary>
    private static int RemoveDeadLoopStarts(List<PatternRow> rows)
    {
        var dead = new List<int>();
        int pending = -1;
        bool used = false;

        for (int i = 0; i < rows.Count; i++)
        {
            switch (rows[i])
            {
                case LoopStartRow:
                    if (pending >= 0 && !used)
                        dead.Add(pending);
                    pending = i;
                    used = false;
                    break;

                // The anchor survives a finite LE, so a later one reuses it and it stays live.
                case LoopEndRow when pending >= 0:
                    used = true;
                    break;
            }
        }

        if (pending >= 0 && !used)
            dead.Add(pending);

        for (int i = dead.Count - 1; i >= 0; i--)
            rows.RemoveAt(dead[i]);

        return dead.Count;
    }

    private sealed record LoopCandidate(int Start, int BlockLength, int Repeats, int Saved,
        List<PatternRow> Body);

    /// <summary>The single best-paying run of identical blocks, or null when no loop would save
    /// anything. Called repeatedly, so one pattern can end up with several loops - each is
    /// measured against the pattern as the previous ones left it.</summary>
    private static LoopCandidate? BestLoop(List<PatternRow> rows)
    {
        int baseline = SongSizeCalculator.EncodedPatternLength(rows);
        LoopCandidate? best = null;

        for (int start = 0; start < rows.Count; start++)
        {
            for (int blockLength = 1; start + blockLength * 2 <= rows.Count; blockLength++)
            {
                var body = rows.GetRange(start, blockLength);

                int repeats = 1;
                while (start + (repeats + 1) * blockLength <= rows.Count
                       && Matches(rows, start + repeats * blockLength, body))
                {
                    repeats++;
                }

                if (repeats < 2)
                    continue;

                var candidate = new List<PatternRow>(rows.Count);
                candidate.AddRange(rows.Take(start));
                candidate.Add(new LoopStartRow());
                candidate.AddRange(body);
                candidate.Add(new LoopEndRow(repeats));
                candidate.AddRange(rows.Skip(start + blockLength * repeats));

                int saved = baseline - SongSizeCalculator.EncodedPatternLength(candidate);
                if (saved > 0 && (best is null || saved > best.Saved))
                {
                    best = new LoopCandidate(start, blockLength, repeats, saved, body);
                }
            }
        }

        return best;
    }

    private static bool Matches(List<PatternRow> rows, int at, List<PatternRow> body)
    {
        for (int i = 0; i < body.Count; i++)
        {
            if (!rows[at + i].Equals(body[i]))
                return false;
        }
        return true;
    }
}
