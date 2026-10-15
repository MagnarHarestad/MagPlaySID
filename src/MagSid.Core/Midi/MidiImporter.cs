using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.Midi;

/// <summary>Which note of a chord a voice keeps.</summary>
public enum VoiceRole
{
    /// <summary>The highest note sounding - a tune sits on top.</summary>
    Melody,

    /// <summary>The lowest note sounding - a bass line sits underneath.</summary>
    Bass,
}

/// <summary>What one SID voice is fed from. A null <see cref="Source"/> leaves the voice silent;
/// a null <see cref="Instrument"/> gets the role's default sound.</summary>
public sealed record VoiceImport(MidiSource? Source, VoiceRole Role, int Transpose = 0,
                                 Instrument? Instrument = null);

public sealed record MidiImportOptions(
    IReadOnlyList<VoiceImport> Voices,
    int RowsPerBeat,
    int Speed,
    int FirstBar = 0,
    int? BarCount = null);

/// <summary>What the import had to bend to fit, so it can be shown before the song replaces
/// anything.</summary>
public sealed record MidiImportReport(
    int Bars,
    int RowsPerBar,
    int BarsPerPattern,
    int Patterns,
    int TotalBytes,
    int NotesPlaced,
    int NotesDropped,
    int NotesMoved,
    int NotesShifted,
    double SourceBpm,
    double SongBpm,
    int TempoChanges);

public sealed record MidiImportResult(Song Song, MidiImportReport Report);

/// <summary>
/// Turns a MIDI file into a MagSID song: three voices, one note each at a time, on a row grid at
/// one speed.
///
/// Each voice reads one track's channel and keeps its highest or lowest sounding note, a new
/// note taking over the moment it becomes that note. The rows are cut into patterns of whole
/// bars, and a pattern that repeats is stored once and named again in the order - which is where
/// the size goes, since tunes repeat. The pattern length is whichever of 1, 2, 4 or 8 bars makes
/// the smallest song, measured with <see cref="SongSizeCalculator"/>.
/// </summary>
public static class MidiImporter
{
    private const int PalFramesPerMinute = 50 * 60;
    private const int MaxRowsPerPattern = 64;    // a note's run of rows is one duration byte, 1..64
    private const int MaxOrderBytes = 255;
    private const int LowestKey = 12;            // MIDI C0 is the player's C-0
    private const int PitchCount = 96;

    /// <summary>The speed closest to the file's tempo, and the tempo that speed actually plays.
    /// A row lasts a whole number of 50Hz frames, so most tempos land a few percent off.</summary>
    public static (int Speed, double Bpm) SuggestSpeed(double bpm, int rowsPerBeat)
    {
        double framesPerRow = PalFramesPerMinute / bpm / rowsPerBeat;
        int speed = Math.Clamp((int)Math.Round(framesPerRow) - 1, 0, 255);
        return (speed, SongBpm(speed, rowsPerBeat));
    }

    public static double SongBpm(int speed, int rowsPerBeat) =>
        PalFramesPerMinute / (double)(rowsPerBeat * (speed + 1));

    /// <summary>Rows in one bar of the file's time signature, or null when a bar does not land on
    /// a whole row at this grid.</summary>
    public static int RowsPerBar(MidiSong midi, int rowsPerBeat)
    {
        int rows = midi.BeatsPerBar * 4 * rowsPerBeat / midi.BeatUnit;
        return rows > 0 && midi.BeatsPerBar * 4 * rowsPerBeat % midi.BeatUnit == 0 ? rows : 4 * rowsPerBeat;
    }

    /// <summary>How many whole bars the chosen voices cover.</summary>
    public static int BarsIn(MidiSong midi, IEnumerable<MidiSource?> sources, int rowsPerBeat)
    {
        double ticksPerRow = midi.TicksPerQuarter / (double)rowsPerBeat;
        long last = sources.Where(s => s is not null).SelectMany(s => s!.Notes)
            .Select(n => (long)Math.Round(n.End / ticksPerRow)).DefaultIfEmpty(0).Max();
        int rowsPerBar = RowsPerBar(midi, rowsPerBeat);
        return (int)((last + rowsPerBar - 1) / rowsPerBar);
    }

    public static MidiImportResult Import(MidiSong midi, MidiImportOptions options)
    {
        if (options.Voices.Count != Song.ChannelCount)
            throw new ArgumentException($"Expected {Song.ChannelCount} voices.", nameof(options));
        if (options.Voices.All(v => v.Source is null))
            throw new MidiImportException("Choose a MIDI track for at least one voice.");

        int rowsPerBar = RowsPerBar(midi, options.RowsPerBeat);
        int totalBars = BarsIn(midi, options.Voices.Select(v => v.Source), options.RowsPerBeat);
        int firstBar = Math.Clamp(options.FirstBar, 0, Math.Max(0, totalBars - 1));
        int bars = Math.Clamp(options.BarCount ?? totalBars - firstBar, 1, Math.Max(1, totalBars - firstBar));

        // Instruments are numbered only for the voices that play.
        var stats = new Stats();
        int playing = 0;
        var lines = options.Voices
            .Select(voice => voice.Source is null
                ? null
                : Line(midi, voice, ++playing, options.RowsPerBeat, firstBar * rowsPerBar, bars * rowsPerBar, stats))
            .ToList();

        MidiImportResult? best = null;
        string? lastProblem = null;
        foreach (int barsPerPattern in new[] { 1, 2, 4, 8 })
        {
            if (rowsPerBar * barsPerPattern > MaxRowsPerPattern)
                break;

            var song = Build(lines, options, rowsPerBar * barsPerPattern);
            if (Problem(song) is { } problem)
            {
                lastProblem = problem;
                continue;
            }

            int size = SongSizeCalculator.Measure(song).TotalBytes;
            if (best is null || size < best.Report.TotalBytes)
            {
                best = new MidiImportResult(song, new MidiImportReport(
                    bars, rowsPerBar, barsPerPattern, song.Patterns.Count, size,
                    stats.Placed, stats.Dropped, stats.Moved, stats.Shifted,
                    midi.Bpm, SongBpm(options.Speed, options.RowsPerBeat), midi.TempoChanges));
            }
        }

        return best ?? throw new MidiImportException(lastProblem
            ?? $"A bar is {rowsPerBar} rows, more than the {MaxRowsPerPattern} a pattern can hold - try the coarser grid.");
    }

    private sealed class Stats
    {
        public int Placed, Dropped, Moved, Shifted;
    }

    /// <summary>One voice's rows across the imported range.</summary>
    private static PatternRow[] Line(MidiSong midi, VoiceImport voice, int instrument, int rowsPerBeat,
                                     int firstRow, int rowCount, Stats stats)
    {
        double ticksPerRow = midi.TicksPerQuarter / (double)rowsPerBeat;

        // Snapped to the grid. A note too short to reach the next row still gets one.
        var notes = new List<(int Start, int End, int Key, int Id)>();
        foreach (var (note, id) in voice.Source!.Notes.Select((n, i) => (n, i)))
        {
            double exact = note.Start / ticksPerRow;
            int start = (int)Math.Round(exact) - firstRow;
            int end = Math.Max(start + 1, (int)Math.Round(note.End / ticksPerRow) - firstRow);
            if (end <= 0 || start >= rowCount)
                continue;

            if (Math.Abs(exact - Math.Round(exact)) > 0.05)
                stats.Moved++;
            notes.Add((start, Math.Min(end, rowCount), note.Key, id));
        }

        // The highest (or lowest) note sounding at each row; a note becomes the voice's the moment
        // it is that note, and a note that never is was dropped.
        notes.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sounding = new List<(int Start, int End, int Key, int Id)>();
        int next = 0;
        var heard = new HashSet<int>();
        var rows = new PatternRow[rowCount];
        int? current = null;
        for (int row = 0; row < rowCount; row++)
        {
            sounding.RemoveAll(n => n.End <= row);
            while (next < notes.Count && notes[next].Start <= row)
                sounding.Add(notes[next++]);

            (int Start, int End, int Key, int Id)? chosen = null;
            foreach (var note in sounding)
            {
                // Ties go to the later note: it is the one just played.
                if (chosen is not { } best
                    || (voice.Role == VoiceRole.Melody ? note.Key > best.Key : note.Key < best.Key)
                    || (note.Key == best.Key && note.Start > best.Start))
                    chosen = note;
            }

            if (chosen is { } pick && pick.Id != current)
            {
                rows[row] = new NoteRow(Pitch(pick.Key + voice.Transpose, stats), instrument);
                heard.Add(pick.Id);
                current = pick.Id;
            }
            else if (chosen is null && current is not null)
            {
                rows[row] = new NoteOffRow();
                current = null;
            }
            else
            {
                rows[row] = new HoldRow();
            }
        }

        stats.Placed += heard.Count;
        stats.Dropped += notes.Count - heard.Count;
        return rows;
    }

    /// <summary>The player's 96 notes, C-0..B-7. Outside them a note moves by octaves until it
    /// fits, and is counted.</summary>
    private static NoteName Pitch(int key, Stats stats)
    {
        int index = key - LowestKey;
        if (index is < 0 or >= PitchCount)
        {
            stats.Shifted++;
            while (index < 0) index += 12;
            while (index >= PitchCount) index -= 12;
        }
        return NoteName.AllNotes[index];
    }

    private static Song Build(List<PatternRow[]?> lines, MidiImportOptions options, int rowsPerPattern)
    {
        var song = new Song { Speed = options.Speed };

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            var voice = options.Voices[channel];
            if (voice.Source is null)
                continue;

            var instrument = voice.Instrument is { } template ? Clone(template) : Default(voice.Role);
            instrument.Number = song.Instruments.Count + 1;
            song.Instruments.Add(instrument);
        }

        // Identical patterns are stored once, across all three channels.
        var known = new Dictionary<string, int>();
        int PatternFor(IReadOnlyList<PatternRow> rows)
        {
            string key = string.Join(",", rows.Select(Describe));
            if (!known.TryGetValue(key, out int number))
            {
                number = song.Patterns.Count + 1;
                var pattern = new Pattern { Number = number };
                pattern.Rows.AddRange(rows);
                song.Patterns.Add(pattern);
                known[key] = number;
            }
            return number;
        }

        int length = lines.Where(l => l is not null).Max(l => l!.Length);
        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            var order = song.ChannelOrders[channel];
            if (lines[channel] is not { } line)
            {
                // A silent voice plays one empty pattern, round and round.
                order.Tokens.Add(new PatternRefToken(PatternFor(Enumerable.Repeat<PatternRow>(new HoldRow(), rowsPerPattern).ToList())));
                continue;
            }

            for (int start = 0; start < length; start += rowsPerPattern)
            {
                var rows = new List<PatternRow>(rowsPerPattern);
                for (int i = start; i < start + rowsPerPattern; i++)
                    rows.Add(i < line.Length ? line[i] : new HoldRow());
                order.Tokens.Add(new PatternRefToken(PatternFor(rows)));
            }
        }

        return song;
    }

    private static string Describe(PatternRow row) => row switch
    {
        NoteRow note => $"{note.Note}{note.InstrumentNumber}",
        NoteOffRow => "---",
        _ => "...",
    };

    /// <summary>Why the song cannot be exported as built, or null.</summary>
    private static string? Problem(Song song)
    {
        int orderBytes = song.ChannelOrders.Sum(o => o.Tokens.Count + 2);
        if (orderBytes > MaxOrderBytes)
            return $"The song order needs {orderBytes} bytes but the player holds {MaxOrderBytes} - " +
                   "import fewer bars.";

        return SongValidator.Validate(song) is { Count: > 0 } issues ? issues[0].Message : null;
    }

    /// <summary>A copy, so two voices given the same instrument file get one instrument each.</summary>
    private static Instrument Clone(Instrument source) =>
        Serialization.InstrumentFile.Read(Serialization.InstrumentFile.Write(source));

    /// <summary>Plain starting sounds, meant to be replaced: a pulse lead and a narrower pulse
    /// bass. No note program, so they cost the song nothing beyond their table entries.</summary>
    public static Instrument Default(VoiceRole role) => role == VoiceRole.Melody
        ? new Instrument { Name = "Lead", Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xA9, PulseWidth = 6 }
        : new Instrument { Name = "Bass", Waveform = 0x41, AttackDecay = 0x08, SustainRelease = 0x88, PulseWidth = 3 };
}

public sealed class MidiImportException(string message) : Exception(message);
