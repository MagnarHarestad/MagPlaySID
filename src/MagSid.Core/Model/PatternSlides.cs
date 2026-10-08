namespace MagSid.Core.Model;

/// <summary>
/// A pattern's slides seen as a value per row - the rate in force on each row - and written back
/// as the fewest slide rows that produce it.
///
/// In the rows a slide is a change: "1xx"/"2xx" sets the rate and it holds until the next slide
/// row, while a note starts unslid. A piano roll's slide lane wants the opposite view - a level on
/// every column, like a controller lane - so this translates both ways. Only a row where the rate
/// actually changes costs anything (two bytes in the export); a run drawn at one level is one row.
/// </summary>
public static class PatternSlides
{
    /// <summary>The rate in force on every row: 0 from each note, a slide row's own rate from that
    /// row on. Rows before the pattern's first note start at 0 - what carries in from the
    /// previous pattern is not this pattern's to know.</summary>
    public static int[] Rates(Pattern pattern)
    {
        var rows = pattern.Rows;
        var rates = new int[rows.Count];
        int running = 0;

        for (int row = 0; row < rows.Count; row++)
        {
            running = rows[row] switch
            {
                NoteRow or NoteOffRow => 0,
                SlideRow slide => slide.Rate,
                _ => running,
            };
            rates[row] = running;
        }

        return rates;
    }

    /// <summary>
    /// Whether a slide can be put on a row: one a note is sounding through. Not a note's own row -
    /// a slide row takes the place a note would - and not after a release, where nothing sounds
    /// for it to move. A loop marker has no time to slide in. Before the pattern's first note a
    /// slide carries on whatever the previous pattern left sounding, so those rows are open too.
    /// </summary>
    public static bool[] Slidable(Pattern pattern)
    {
        var rows = pattern.Rows;
        var open = new bool[rows.Count];
        bool released = false;

        for (int row = 0; row < rows.Count; row++)
        {
            switch (rows[row])
            {
                case NoteRow:
                    released = false;
                    break;
                case NoteOffRow:
                    released = true;
                    break;
                case HoldRow or SlideRow:
                    open[row] = !released;
                    break;
            }
        }

        return open;
    }

    /// <summary>
    /// How far a note's slides have moved its 16-bit frequency on every frame it sounds, from its
    /// trigger frame on: <paramref name="framesPerRow"/> entries per row. The player moves the
    /// pitch before it reads the row, so a slide row's rate starts on that row's second frame.
    /// </summary>
    public static int[] Glide(IReadOnlyList<int> rates, int noteRow, int length, int framesPerRow)
    {
        int lastRow = Math.Min(rates.Count - 1, noteRow + length - 1);
        var offsets = new int[Math.Max(0, lastRow - noteRow + 1) * framesPerRow];
        int offset = 0;

        for (int row = noteRow; row <= lastRow; row++)
        {
            for (int frame = 0; frame < framesPerRow; frame++)
            {
                if (row > noteRow)
                    offset += frame == 0 ? rates[row - 1] : rates[row];

                offsets[((row - noteRow) * framesPerRow) + frame] = offset;
            }
        }

        return offsets;
    }

    /// <summary>
    /// Sets the rate on some rows and leaves every other row at the rate it had, then writes the
    /// result as slide rows only where the rate changes. Rows that cannot take a slide (see
    /// <see cref="Slidable"/>) are left alone. A slide row that changes nothing is dropped - except
    /// before the pattern's first note, where it may be stopping or changing a slide carried in
    /// from the previous pattern, which this pattern cannot see.
    /// </summary>
    public static void SetRates(Pattern pattern, IReadOnlyDictionary<int, int> ratesByRow)
    {
        var rows = pattern.Rows;
        var wanted = Rates(pattern);
        var open = Slidable(pattern);

        foreach (var (row, rate) in ratesByRow)
        {
            if (row >= 0 && row < rows.Count && open[row])
                wanted[row] = Math.Clamp(rate, SlideRow.MinRate, SlideRow.MaxRate);
        }

        bool beforeFirstNote = true;
        int running = 0;

        for (int row = 0; row < rows.Count; row++)
        {
            switch (rows[row])
            {
                case NoteRow:
                    beforeFirstNote = false;
                    running = 0;
                    break;

                case NoteOffRow:
                    beforeFirstNote = false;
                    running = 0;
                    break;

                case HoldRow or SlideRow when open[row]:
                {
                    bool keepAsWritten = beforeFirstNote && rows[row] is SlideRow && !ratesByRow.ContainsKey(row);
                    if (keepAsWritten)
                    {
                        running = ((SlideRow)rows[row]).Rate;
                    }
                    else if (wanted[row] != running)
                    {
                        rows[row] = new SlideRow(wanted[row]);
                        running = wanted[row];
                    }
                    else
                    {
                        rows[row] = new HoldRow();
                    }

                    break;
                }
            }
        }
    }
}
