using MagSid.Core.SidImport;

namespace MagSid.Playback;

/// <summary>
/// What the Import SID dialog says, and how much of a tune it looks at - here so the Windows
/// dialog and the Avalonia one say exactly the same.
/// </summary>
public static class SidImportText
{
    /// <summary>The most of a tune that is looked at. Longer than that, a song does not fit the
    /// player's order list anyway.</summary>
    public const int MaxFrames = 50 * 60 * 10;

    public static string Heading(SidFile file, string fileName)
    {
        string title = string.Join("  ·  ", new[] { file.Name, file.Author, file.Released }.Where(s => s.Length > 0));
        return title.Length > 0 ? $"{title}   ({fileName})" : fileName;
    }

    /// <summary>How long a subtune plays before it repeats, in 50 Hz frames, and the words for it.
    /// A tune that does not repeat within ten minutes is looked at for five.</summary>
    public static (int Frames, string Describe) Length(SidFile file, int song)
    {
        // Ten minutes of play calls, however often the tune is called.
        int calls = (int)Math.Ceiling(MaxFrames * Math.Max(1, SidImportTrace.CallsPerFrame(file, song)));
        var measured = SidSongLength.Measure(file, calls, song);
        if (measured is null)
            return (50 * 60 * 5, "(it does not repeat within ten minutes; the first five are looked at)");

        int frames = Math.Clamp((int)Math.Ceiling(measured.TotalFrames * 50 / measured.CallsPerSecond), 50, MaxFrames);
        string loop = measured.LoopStartFrame > 0 ? $", then repeats from {Clock(measured.LoopStart)}" : "";
        return (frames, $"(the tune plays {Clock(measured.Total)}{loop})");
    }

    public static string Report(SidImportReport report, double agreement, double callsPerFrame)
    {
        var lines = new List<string>
        {
            $"{report.Rows} rows in {report.Patterns} patterns of {report.RowsPerPattern} rows - {report.TotalBytes} bytes with the player.",
            $"It plays {agreement:P0} of the tune's notes as the tune does: the same note, on the same voice, within half a row.",
        };

        var bent = new List<string> { $"{report.NotesPlaced} notes placed" };
        if (report.NotesMoved > 0)
            bent.Add($"{report.NotesMoved} moved onto the grid");
        if (report.NotesDropped > 0)
            bent.Add($"{report.NotesDropped} dropped where two started in one row");
        if (report.NotesShifted > 0)
            bent.Add($"{report.NotesShifted} moved by octaves into C-0..B-7");
        lines.Add(string.Join("; ", bent) + ".");

        lines.Add(report.SoundsFound <= report.InstrumentsMade
            ? $"{report.SoundsFound} different sounds, an instrument each."
            : $"{report.SoundsFound} different sounds: the {report.InstrumentsMade} used most are instruments, " +
              $"and {report.NotesOnNearSound} notes play on the nearest of them.");
        if (report.UsesFilter)
            lines.Add("The filter comes over as filter programs, set up again by each note that uses it.");

        if (callsPerFrame > 1.05)
            lines.Add($"The tune plays {callsPerFrame:0.#} times a frame and MagSID once, so what it does in between is not heard.");
        else if (callsPerFrame < 0.95)
            lines.Add($"The tune plays {callsPerFrame:0.##} times a frame, slower than MagSID's player; its notes are on the grid all the same.");

        return string.Join(Environment.NewLine, lines);
    }

    public const string Hint =
        "A .sid holds a program, not notes: the import plays it and writes down what it does to the SID chip. " +
        "A note is a gate going on - or, with the gate held, a pitch that moves to another note and stays - and " +
        "its sound becomes an instrument: the waveform frame by frame, steps of pitch, pulse width, vibrato, filter " +
        "and envelope. Notes that sound alike share one; MagSID has 16, so the rest play on the nearest. Slides, " +
        "tempo changes and a filter sweeping across notes come out as the nearest thing MagSID can say. The share " +
        "of notes above is measured, not estimated: the imported song is played through MagSID's player and its " +
        "notes compared with the tune's.";

    private static string Clock(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
