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
/// Shrinks a song's pattern data without changing a note of what it plays. Two things only:
/// dead LS markers go, and a run of identical rows becomes an LS/LE loop where that is actually
/// cheaper.
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

        return new Result(findings, before, SongSizeCalculator.Measure(song));
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
