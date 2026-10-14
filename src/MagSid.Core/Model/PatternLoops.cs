namespace MagSid.Core.Model;

/// <summary>
/// A pattern's rows in the order they play, LS/LE loops unrolled - one entry per tick.
///
/// The player walks a pattern the way <c>SongSequencer.FetchEvent</c> does: an LS anchors the
/// loop at the row after it, an LE with a count plays the marked section that many times in all,
/// a bare LE loops for ever, and the markers themselves take no time. Drawn this way, a row of
/// the view is a tick on every channel, so channels with loops and channels without them line up.
/// </summary>
public static class PatternLoops
{
    /// <summary>One played row: the pattern row it is, which pass of its loop this is and how many
    /// there are (-1 for a loop that never ends), and whether a loop jumped back to it.</summary>
    public readonly record struct UnrolledRow(int Row, int Pass, int Passes, bool PassStart)
    {
        /// <summary>A second or later pass - the same rows again, not rows of their own.</summary>
        public bool Linked => Pass > 1;
    }

    /// <summary>More than any pattern this format can hold, played through: a guard against a
    /// loop that would otherwise unroll without end.</summary>
    private const int MaxRows = 16384;

    /// <summary>
    /// The pattern as it plays. A bare LE loops for ever, so its section is unrolled until the
    /// rows reach <paramref name="infiniteCap"/> - at least once - and nothing after it is reached.
    /// </summary>
    public static IReadOnlyList<UnrolledRow> Unroll(Pattern pattern, int infiniteCap)
    {
        var rows = pattern.Rows;
        var played = new List<(int Row, bool PassStart, bool Endless)>();
        int index = 0, loopStart = 0, counter = 0;
        bool jumped = false, endless = false;

        while (index < rows.Count && played.Count < MaxRows)
        {
            switch (rows[index])
            {
                case LoopStartRow:
                    loopStart = index + 1;
                    counter = 0;
                    index++;
                    break;

                case LoopEndRow { RepeatCount: null }:
                {
                    // A section with no timed row in it would loop without time passing; the
                    // player would hang there, and there is nothing to draw.
                    bool timed = false;
                    for (int r = loopStart; r < index && !timed; r++)
                        timed = rows[r] is not (LoopStartRow or LoopEndRow);

                    if (!timed)
                    {
                        index++;
                        break;
                    }

                    // Every row of the section is endless, the first pass included.
                    if (!endless)
                    {
                        for (int p = played.Count - 1; p >= 0 && played[p].Row >= loopStart; p--)
                            played[p] = played[p] with { Endless = true };
                        endless = true;
                    }

                    if (played.Count >= infiniteCap)
                        return Number(played);

                    index = loopStart;
                    jumped = true;
                    break;
                }

                case LoopEndRow { RepeatCount: { } count }:
                    counter++;
                    if (counter < count)
                    {
                        index = loopStart;
                        jumped = true;
                    }
                    else
                    {
                        counter = 0;
                        index++;
                    }

                    break;

                default:
                    played.Add((index, jumped, endless));
                    jumped = false;
                    index++;
                    break;
            }
        }

        return Number(played);
    }

    /// <summary>The rows played once each, markers left out - how the view drew a pattern before
    /// loops were unrolled, and still does when they are not.</summary>
    public static IReadOnlyList<UnrolledRow> Compact(Pattern pattern)
    {
        var rows = new List<UnrolledRow>(pattern.Rows.Count);
        for (int i = 0; i < pattern.Rows.Count; i++)
        {
            if (pattern.Rows[i] is not (LoopStartRow or LoopEndRow))
                rows.Add(new UnrolledRow(i, 1, 1, false));
        }

        return rows;
    }

    private static IReadOnlyList<UnrolledRow> Number(List<(int Row, bool PassStart, bool Endless)> played)
    {
        var total = new Dictionary<int, int>();
        foreach (var (row, _, _) in played)
            total[row] = total.GetValueOrDefault(row) + 1;

        var seen = new Dictionary<int, int>();
        var result = new List<UnrolledRow>(played.Count);
        foreach (var (row, passStart, endlessRow) in played)
        {
            int pass = seen.GetValueOrDefault(row) + 1;
            seen[row] = pass;
            result.Add(new UnrolledRow(row, pass, endlessRow ? -1 : total[row], passStart && pass > 1));
        }

        return result;
    }
}
