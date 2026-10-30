using System.Globalization;
using System.Text;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>A row that does something - a note, a release, an instrument or an effect - on the grid.</summary>
internal readonly record struct SheetRow(int Step, int Call, string Note, string Instrument, string Effects);

/// <summary>A note on the grid: from its step, so many steps long; its pitch C-0 as 0, middle C 48.</summary>
internal readonly record struct SheetNote(int Start, int Length, int Pitch);

/// <summary>
/// A tune's transcription as notes on a grid, for the exports: a row is a sixteenth note, four a
/// beat, sixteen a bar of 4/4 - a tracker's own reckoning. Each voice's notes are placed by the
/// call they sound on, so the voices line up, and last until the next note or release.
/// </summary>
internal sealed class NoteSheet
{
    public const int StepsPerBeat = 4;
    public const int StepsPerBar = 16;

    public required string Title { get; init; }
    public required string Author { get; init; }
    public required string Released { get; init; }
    public required int Song { get; init; }
    public required int Songs { get; init; }

    /// <summary>The rows' speed as the view gives it: 6, or 6/9 for a funk tempo.</summary>
    public required string Grid { get; init; }

    public required double CallsPerStep { get; init; }
    public required double CallsPerSecond { get; init; }

    /// <summary>Each voice's rows that do something, in the order they play.</summary>
    public required IReadOnlyList<SheetRow>[] Rows { get; init; }

    /// <summary>Each voice's notes, in order, none overlapping.</summary>
    public required IReadOnlyList<SheetNote>[] Notes { get; init; }

    /// <summary>The tune's length on the grid, to the end of its last bar.</summary>
    public required int Steps { get; init; }

    public int Voices => Notes.Length;

    public int Bars => Steps / StepsPerBar;

    /// <summary>Quarter notes a minute: four rows a beat.</summary>
    public double BeatsPerMinute => 60 * CallsPerSecond / CallsPerStep / StepsPerBeat;

    public string VoiceName(int voice) => Voices <= 3 ? $"Voice {voice + 1}" : $"SID{voice / 3 + 1} voice {voice % 3 + 1}";

    /// <summary>What a file of it is called by default: the tune's name, and its song when it has several.</summary>
    public string FileName
    {
        get
        {
            string name = string.Concat(Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (name.Length == 0)
                name = "Tune";
            return Songs > 1 ? $"{name} - song {Song}" : name;
        }
    }

    public static NoteSheet Of(SidFile file, int song, Score score, ScoreTimeline timeline)
    {
        double callsPerStep = score.FunkTempo is { } funk ? (score.Tempo + funk) / 2.0 : Math.Max(1, score.Tempo);
        int Step(int call) => (int)Math.Round(call / callsPerStep);
        int end = Math.Max(1, Step(timeline.Calls));
        int steps = (end + StepsPerBar - 1) / StepsPerBar * StepsPerBar;

        var rows = new IReadOnlyList<SheetRow>[timeline.Voices];
        var notes = new IReadOnlyList<SheetNote>[timeline.Voices];
        for (int voice = 0; voice < timeline.Voices; voice++)
        {
            var voiceRows = new List<SheetRow>();
            var voiceNotes = new List<SheetNote>();
            int? pitch = null;
            int start = 0;

            void Close(int at)
            {
                if (pitch is { } p && at > start)
                    voiceNotes.Add(new SheetNote(start, at - start, p));
                pitch = null;
            }

            foreach (var played in timeline.Rows[voice])
            {
                if (score.PatternNumber(played.Pattern)?.Rows.ElementAtOrDefault(played.Row) is not { } row)
                    continue;
                int at = Math.Min(Step(played.Call), end);
                string note = row.Note.Kind switch
                {
                    RowNoteKind.Note => NoteTables.Name(row.Note.Note + played.Transpose),
                    RowNoteKind.GateOff => "===",
                    RowNoteKind.GateOn => "+++",
                    _ => "",
                };
                string instrument = row.Instrument > 0 ? row.Instrument.ToString("X2", CultureInfo.InvariantCulture) : "";
                string effects = string.Join(" ", row.Effects.Select(e => e.Label));
                if (note.Length > 0 || instrument.Length > 0 || effects.Length > 0)
                    voiceRows.Add(new SheetRow(at, played.Call, note, instrument, effects));

                if (row.Note.Kind == RowNoteKind.Note)
                {
                    Close(at);
                    pitch = row.Note.Note + played.Transpose;
                    start = at;
                }
                else if (row.Note.Kind == RowNoteKind.GateOff)
                    Close(at);
            }
            Close(end);
            rows[voice] = voiceRows;
            notes[voice] = voiceNotes;
        }

        return new NoteSheet
        {
            Title = string.IsNullOrWhiteSpace(file.Name) ? "Untitled" : file.Name.Trim(),
            Author = file.Author?.Trim() ?? "",
            Released = file.Released?.Trim() ?? "",
            Song = song,
            Songs = Math.Max(1, file.Songs),
            Grid = score.FunkTempo is { } f ? $"{score.Tempo}/{f}" : $"{score.Tempo}",
            CallsPerStep = callsPerStep,
            CallsPerSecond = score.CallsPerSecond,
            Rows = rows,
            Notes = notes,
            Steps = steps,
        };
    }

    /// <summary>The tune as text: a header, then each voice's rows that do something - where in the
    /// bar, when, the note or release, the instrument and the effects - as a tracker lists them.</summary>
    public string ToText(string product)
    {
        var text = new StringBuilder();
        text.AppendLine(Title);
        var about = new[] { Author, Released }.Where(s => s.Length > 0).ToList();
        if (Songs > 1)
            about.Add($"song {Song} of {Songs}");
        if (about.Count > 0)
            text.AppendLine(string.Join(" · ", about));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Transcribed by {product} - rows of {Grid} calls; a row is a sixteenth note, four a beat, sixteen a bar: {BeatsPerMinute:0} beats a minute."));
        text.AppendLine("Notes are tracker notes: C-4 is middle C, === a release, +++ the gate on again; instruments and effects in hex.");

        for (int voice = 0; voice < Voices; voice++)
        {
            text.AppendLine();
            text.AppendLine(VoiceName(voice).ToUpperInvariant());
            text.AppendLine("   Bar  Beat  Time       Note  Ins  Effects");
            foreach (var row in Rows[voice])
            {
                int bar = row.Step / StepsPerBar + 1;
                int beat = row.Step % StepsPerBar / StepsPerBeat + 1;
                int sixteenth = row.Step % StepsPerBeat + 1;
                double seconds = row.Call / CallsPerSecond;
                text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{bar,6}  {beat}.{sixteenth}   {(int)seconds / 60:00}:{seconds % 60:00.00}   {row.Note,-4}  {row.Instrument,-3}  {row.Effects}").TrimEnd());
            }
            if (Rows[voice].Count == 0)
                text.AppendLine("   (silent)");
        }
        return text.ToString();
    }
}
