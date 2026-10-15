using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.Midi;

/// <summary>What a voice is built from.</summary>
public enum VoiceRole
{
    /// <summary>The highest note sounding - a tune sits on top.</summary>
    Melody,

    /// <summary>The lowest note sounding - a bass line sits underneath.</summary>
    Bass,

    /// <summary>
    /// The C64 way of spending one voice on the whole rhythm section: kick and snare from a drum
    /// part, the bass line from a bass part in the rows between. The kick is a KickBass - a thump
    /// that settles on the bass note - so a kick and a bass note share a row instead of fighting
    /// for it. This is how channel 1 is built in the demo songs.
    /// </summary>
    DrumsAndBass,
}

/// <summary>
/// What one SID voice is fed from. <see cref="Source"/> is the melodic part - for
/// <see cref="VoiceRole.DrumsAndBass"/> the bass line - and <see cref="Drums"/> the drum part,
/// read only by that role. A null <see cref="Instrument"/> gets the role's default sound.
/// </summary>
public sealed record VoiceImport(
    MidiSource? Source,
    VoiceRole Role,
    int Transpose = 0,
    Instrument? Instrument = null,
    MidiSource? Drums = null,
    bool HiHat = false)
{
    public bool Plays => Source is not null || (Role == VoiceRole.DrumsAndBass && Drums is not null);
}

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
    int TempoChanges,
    int DrumHits = 0,
    int DrumsLeftOut = 0);

public sealed record MidiImportResult(Song Song, MidiImportReport Report);

/// <summary>
/// Turns a MIDI file into a MagSID song: three voices, one note each at a time, on a row grid at
/// one speed.
///
/// A melodic voice reads one track's channel and keeps its highest or lowest sounding note, a new
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

    private static readonly NoteName FallbackDrumNote = NoteName.Parse("C-2");

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

    /// <summary>Rows in one bar of the file's time signature - or of 4/4, when a bar of it does
    /// not land on a whole row at this grid.</summary>
    public static int RowsPerBar(MidiSong midi, int rowsPerBeat)
    {
        int rows = midi.BeatsPerBar * 4 * rowsPerBeat / midi.BeatUnit;
        return rows > 0 && midi.BeatsPerBar * 4 * rowsPerBeat % midi.BeatUnit == 0 ? rows : 4 * rowsPerBeat;
    }

    /// <summary>How many whole bars the chosen parts cover.</summary>
    public static int BarsIn(MidiSong midi, IEnumerable<MidiSource?> sources, int rowsPerBeat)
    {
        double ticksPerRow = midi.TicksPerQuarter / (double)rowsPerBeat;
        long last = sources.Where(s => s is not null).SelectMany(s => s!.Notes)
            .Select(n => (long)Math.Round(n.End / ticksPerRow)).DefaultIfEmpty(0).Max();
        int rowsPerBar = RowsPerBar(midi, rowsPerBeat);
        return (int)((last + rowsPerBar - 1) / rowsPerBar);
    }

    /// <summary>
    /// A starting choice of parts. With a drum part, the demo songs' layout: drums and the lowest
    /// part as bass on voice 1, the highest part on voice 2, the next on voice 3. Without one: the
    /// highest on voice 1, the next on voice 2, the lowest as bass on voice 3. A part with under a
    /// fifth of the busiest one's notes is an intro or an effect, not a line to build on.
    /// </summary>
    public static IReadOnlyList<VoiceImport> SuggestVoices(MidiSong midi)
    {
        var sources = midi.Sources();
        var parts = sources.Where(s => !s.LooksLikeDrums).ToList();
        int busiest = parts.Select(s => s.NoteCount).DefaultIfEmpty(0).Max();
        var melodic = parts.Where(s => s.NoteCount * 5 >= busiest).OrderByDescending(s => s.AverageKey).ToList();
        var drums = sources.Where(s => s.LooksLikeDrums).OrderByDescending(s => s.NoteCount).FirstOrDefault();
        var highest = melodic.ElementAtOrDefault(0);
        var next = melodic.Count > 2 ? melodic[1] : null;
        var lowest = melodic.Count > 1 ? melodic[^1] : null;

        return drums is not null
            ? [new(lowest, VoiceRole.DrumsAndBass, Drums: drums), new(highest, VoiceRole.Melody), new(next, VoiceRole.Melody)]
            : [new(highest, VoiceRole.Melody), new(next, VoiceRole.Melody), new(lowest, VoiceRole.Bass)];
    }

    /// <summary>Every part a voice reads, for <see cref="BarsIn"/>.</summary>
    public static IEnumerable<MidiSource?> PartsOf(VoiceImport voice) =>
        voice.Role == VoiceRole.DrumsAndBass ? [voice.Source, voice.Drums] : [voice.Source];

    public static MidiImportResult Import(MidiSong midi, MidiImportOptions options)
    {
        if (options.Voices.Count != Song.ChannelCount)
            throw new ArgumentException($"Expected {Song.ChannelCount} voices.", nameof(options));
        if (!options.Voices.Any(v => v.Plays))
            throw new MidiImportException("Choose a MIDI track for at least one voice.");

        int rowsPerBar = RowsPerBar(midi, options.RowsPerBeat);
        int totalBars = BarsIn(midi, options.Voices.SelectMany(PartsOf), options.RowsPerBeat);
        int firstBar = Math.Clamp(options.FirstBar, 0, Math.Max(0, totalBars - 1));
        int bars = Math.Clamp(options.BarCount ?? totalBars - firstBar, 1, Math.Max(1, totalBars - firstBar));
        var range = new Range(midi.TicksPerQuarter / (double)options.RowsPerBeat, firstBar * rowsPerBar,
                              bars * rowsPerBar, options.RowsPerBeat);

        // Instruments are numbered as they are first used, so a sound nothing plays - a snare in
        // a part without one - never reaches the song.
        var stats = new Stats();
        var instruments = new List<Instrument>();
        var lines = new List<PatternRow[]?>();
        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            var voice = options.Voices[channel];
            var allocated = new Dictionary<string, int>();
            int Number(string kind, Func<Instrument> make)
            {
                if (!allocated.TryGetValue(kind, out int number))
                {
                    var instrument = make();
                    instrument.Number = number = instruments.Count + 1;
                    instruments.Add(instrument);
                    allocated[kind] = number;
                }
                return number;
            }

            lines.Add(!voice.Plays ? null
                : voice.Role == VoiceRole.DrumsAndBass ? DrumsAndBass(voice, channel, range, stats, Number)
                : Melodic(voice, range, stats, Number));
        }

        MidiImportResult? best = null;
        string? lastProblem = null;
        foreach (int barsPerPattern in new[] { 1, 2, 4, 8 })
        {
            if (rowsPerBar * barsPerPattern > MaxRowsPerPattern)
                break;

            var song = Build(lines, instruments, options.Speed, rowsPerBar * barsPerPattern);
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
                    midi.Bpm, SongBpm(options.Speed, options.RowsPerBeat), midi.TempoChanges,
                    stats.DrumHits, stats.DrumsLeftOut));
            }
        }

        return best ?? throw new MidiImportException(lastProblem
            ?? $"A bar is {rowsPerBar} rows, more than the {MaxRowsPerPattern} a pattern can hold - try the coarser grid.");
    }

    private sealed class Stats
    {
        public int Placed, Dropped, Moved, Shifted, DrumHits, DrumsLeftOut;
    }

    /// <summary>The imported stretch of the file, in rows.</summary>
    private sealed record Range(double TicksPerRow, int FirstRow, int RowCount, int RowsPerBeat)
    {
        /// <summary>A note's start and end rows within the range, or null when it lies outside.
        /// A note too short to reach the next row still gets one; one already sounding when the
        /// range begins starts before row 0.</summary>
        public (int Start, int End)? Rows(MidiNote note, Stats stats)
        {
            double exact = note.Start / TicksPerRow;
            int start = (int)Math.Round(exact) - FirstRow;
            int end = Math.Max(start + 1, (int)Math.Round(note.End / TicksPerRow) - FirstRow);
            if (end <= 0 || start >= RowCount)
                return null;

            if (Math.Abs(exact - Math.Round(exact)) > 0.05)
                stats.Moved++;
            return (start, Math.Min(end, RowCount));
        }
    }

    private static PatternRow[] Melodic(VoiceImport voice, Range range, Stats stats,
                                        Func<string, Func<Instrument>, int> number)
    {
        var rows = MonoLine(voice.Source!, voice.Role, voice.Transpose, range, stats);
        if (!rows.Any(r => r is NoteRow))
            return rows;

        int instrument = number("voice", () => voice.Instrument is { } template ? Clone(template) : Default(voice.Role));
        for (int i = 0; i < rows.Length; i++)
            if (rows[i] is NoteRow note)
                rows[i] = note with { InstrumentNumber = instrument };
        return rows;
    }

    /// <summary>One part's rows, one note at a time, with instrument 0 standing in for the voice's
    /// own until the caller numbers it.</summary>
    private static PatternRow[] MonoLine(MidiSource source, VoiceRole role, int transpose, Range range, Stats stats)
    {
        var notes = new List<(int Start, int End, int Key, int Id)>();
        foreach (var (note, id) in source.Notes.Select((n, i) => (n, i)))
        {
            if (range.Rows(note, stats) is { } at)
                notes.Add((at.Start, at.End, note.Key, id));
        }

        // The highest (or lowest) note sounding at each row; a note becomes the voice's the moment
        // it is that note, and a note that never is was dropped.
        notes.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sounding = new List<(int Start, int End, int Key, int Id)>();
        int next = 0;
        var heard = new HashSet<int>();
        var rows = new PatternRow[range.RowCount];
        int? current = null;
        for (int row = 0; row < range.RowCount; row++)
        {
            sounding.RemoveAll(n => n.End <= row);
            while (next < notes.Count && notes[next].Start <= row)
                sounding.Add(notes[next++]);

            (int Start, int End, int Key, int Id)? chosen = null;
            foreach (var note in sounding)
            {
                // Ties go to the later note: it is the one just played.
                if (chosen is not { } best
                    || (role == VoiceRole.Melody ? note.Key > best.Key : note.Key < best.Key)
                    || (note.Key == best.Key && note.Start > best.Start))
                    chosen = note;
            }

            if (chosen is { } pick && pick.Id != current)
            {
                rows[row] = new NoteRow(Pitch(pick.Key + transpose, stats), 0);
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

    private enum Drum { Kick, Snare, HiHat }

    /// <summary>General MIDI drum keys this voice can play; anything else is left out.</summary>
    private static Drum? DrumFor(int key) => key switch
    {
        35 or 36 => Drum.Kick,
        >= 37 and <= 40 => Drum.Snare,
        42 or 44 or 46 => Drum.HiHat,
        _ => null,
    };

    /// <summary>
    /// Kick, then snare, then a new bass note, then - if wanted - a hi-hat on a row nothing else
    /// uses. The kick is written with the bass note of the moment, so a bass note on the same
    /// pitch right after it is already sounding and is not struck again. The snare carries the
    /// bass note too: its own pitches are fixed, and reusing a note the song already has keeps the
    /// note table from growing. Two hits of one drum on neighbouring rows are a flam; the later,
    /// the main hit, is kept.
    /// </summary>
    private static PatternRow[] DrumsAndBass(VoiceImport voice, int channel, Range range, Stats stats,
                                             Func<string, Func<Instrument>, int> number)
    {
        int count = range.RowCount;
        var bass = voice.Source is null
            ? Enumerable.Repeat<PatternRow>(new HoldRow(), count).ToArray()
            : MonoLine(voice.Source, VoiceRole.Bass, voice.Transpose, range, stats);

        var hits = new Dictionary<Drum, bool[]>
        {
            [Drum.Kick] = new bool[count], [Drum.Snare] = new bool[count], [Drum.HiHat] = new bool[count],
        };
        foreach (var note in voice.Drums?.Notes ?? [])
        {
            // A hit is a moment: one before the range is not in it.
            if (range.Rows(note, stats) is not { Start: >= 0 } at)
                continue;
            if (DrumFor(note.Key) is { } drum && !hits[drum][at.Start])
                hits[drum][at.Start] = true;
            else
                stats.DrumsLeftOut++;
        }
        foreach (var drum in hits.Keys.Where(d => d != Drum.HiHat))
            for (int row = 0; row + 1 < count; row++)
                if (hits[drum][row] && hits[drum][row + 1])
                {
                    hits[drum][row] = false;
                    stats.DrumsLeftOut++;
                }

        // The bass note at a row: the one sounding, else the next within the beat, else the last.
        NoteName? last = null;
        bool sounding = false;
        NoteName NoteAt(int row)
        {
            if (bass[row] is NoteRow onset)
                return onset.Note;
            if (sounding && last is { } held)
                return held;
            for (int ahead = row + 1; ahead < Math.Min(count, row + range.RowsPerBeat); ahead++)
                if (bass[ahead] is NoteRow upcoming)
                    return upcoming.Note;
            return last ?? FirstBassNote(bass) ?? FallbackDrumNote;
        }

        int struck = hits.Values.Sum(h => h.Count(hit => hit)), placed = 0;
        var rows = new PatternRow[count];
        bool absorbNext = false;
        string? playing = null;                  // whose note the voice is sounding
        for (int row = 0; row < count; row++)
        {
            bool absorbed = absorbNext;
            absorbNext = false;
            bool bassOnset = bass[row] is NoteRow && !absorbed;

            if (hits[Drum.Kick][row])
            {
                var note = NoteAt(row);
                rows[row] = new NoteRow(note, number("kick", () => Kit("KickBass", channel)));
                absorbNext = row + 1 < count && bass[row + 1] is NoteRow following && following.Note == note;
                placed++;
                playing = "kick";
            }
            else if (hits[Drum.Snare][row])
            {
                rows[row] = new NoteRow(NoteAt(row), number("snare", () => Kit("Snare", channel)));
                if (bassOnset)
                {
                    stats.Placed--;
                    stats.Dropped++;
                }
                placed++;
                playing = "snare";
            }
            else if (bassOnset)
            {
                var onset = (NoteRow)bass[row];
                rows[row] = onset with { InstrumentNumber = number("bass", () => voice.Instrument is { } t ? Clone(t) : Kit("Bass", channel)) };
                playing = "bass";
            }
            else if (hits[Drum.HiHat][row] && voice.HiHat
                     && !(playing is "bass" or "kick" && sounding && bass[row] is not NoteOffRow))
            {
                rows[row] = new NoteRow(last ?? NoteAt(row), number("hihat", () => Kit("Hihat", channel)));
                placed++;
                playing = "hihat";
            }
            else if (bass[row] is NoteOffRow && playing is "bass" or "kick")
            {
                // The bass note ended; a drum still ringing out is left to ring.
                rows[row] = new NoteOffRow();
                playing = null;
            }
            else
            {
                rows[row] = new HoldRow();
            }

            // Follow the bass part itself, whatever was written over it.
            if (bass[row] is NoteRow heard)
            {
                last = heard.Note;
                sounding = true;
            }
            else if (bass[row] is NoteOffRow)
            {
                sounding = false;
            }
        }

        stats.DrumHits += placed;
        stats.DrumsLeftOut += struck - placed;
        return rows;
    }

    private static NoteName? FirstBassNote(PatternRow[] bass) =>
        bass.OfType<NoteRow>().Select(n => (NoteName?)n.Note).FirstOrDefault();

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

    private static Song Build(List<PatternRow[]?> lines, List<Instrument> instruments, int speed, int rowsPerPattern)
    {
        var song = new Song { Speed = speed };
        foreach (var instrument in instruments)
        {
            var copy = Clone(instrument);
            copy.Number = instrument.Number;
            song.Instruments.Add(copy);
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
    public static Instrument Default(VoiceRole role) => role switch
    {
        VoiceRole.Melody => new Instrument { Name = "Lead", Waveform = 0x41, AttackDecay = 0x09, SustainRelease = 0xA9, PulseWidth = 6 },
        VoiceRole.Bass => new Instrument { Name = "Bass", Waveform = 0x41, AttackDecay = 0x08, SustainRelease = 0x88, PulseWidth = 3 },
        _ => Kit("KickBass", 0),
    };

    /// <summary>
    /// The drum-and-bass kit, as Magnificent has it: a KickBass that thumps on noise and falls
    /// onto the row's note, a bass with the same click, a snare and a hi-hat on fixed pitches. Each
    /// opens its own filter program, routed here to the channel it plays on.
    /// </summary>
    public static Instrument Kit(string name, int channel)
    {
        var instrument = name switch
        {
            "KickBass" => Make(0x41, 0x07, 0xF7, [0x09, 0x81, 0x41], ["B-7", "B-7", "D-3", "F#2"], ArpeggioEnding.Root, 0,
                [Setup(0x6F, 0x38), Sweep(0x38), Setup(0x3F, 0x03), Sweep(0x02), Setup(0x1F, 0x18), Sweep(0x10), Sweep(0x08), Sweep(0x05), Sweep(0x04)]),
            "Bass" => Make(0x41, 0x05, 0xB7, [0x09, 0x81, 0x41], ["B-7", "B-7", "C#3"], ArpeggioEnding.Root, 0,
                [Setup(0x1F, 0x01), Sweep(0x11), Sweep(0x10), Sweep(0x0F), Sweep(0x0E), Sweep(0x0A)]),
            "Snare" => Make(0x81, 0x00, 0x97, [0x09, 0x81, 0x41, 0x41, 0x80], ["B-7", "B-7", "G-3", "E-3", "B-7"], ArpeggioEnding.Loop, 3,
                [Setup(0x3F, 0x68), Sweep(0x68), Setup(0x6F, 0x04), Sweep(0x03), Sweep(0x04)]),
            "Hihat" => Make(0x81, 0x00, 0x62, [0x09, 0x81, 0x80], ["A#7"], ArpeggioEnding.Hold, 0,
                [FilterStep.Setup(0x1F, 0xB0, 0x01), Sweep(0x40)]),
            _ => throw new ArgumentException($"No kit instrument called {name}.", nameof(name)),
        };
        instrument.Name = name;

        // The filter's voice bits name the channel; the resonance in the high nibble is kept.
        for (int i = 0; i < instrument.Filter.Count; i++)
        {
            var step = instrument.Filter[i];
            if (step.IsSetup)
                instrument.Filter[i] = FilterStep.Setup(step.ModeVolume!.Value,
                    (byte)(step.ResonanceRouting!.Value & 0xF0 | 1 << channel), step.Cutoff, step.SweepRate);
        }
        return instrument;

        static FilterStep Setup(byte modeVolume, byte cutoff) => FilterStep.Setup(modeVolume, 0xF0, cutoff);
        static FilterStep Sweep(byte cutoff) => FilterStep.Sweep(cutoff);

        static Instrument Make(byte wave, byte ad, byte sr, byte[] wavetable, string[] program,
                               ArpeggioEnding ending, int loopPoint, FilterStep[] filter)
        {
            var made = new Instrument
            {
                Waveform = wave, AttackDecay = ad, SustainRelease = sr, PulseWidth = 7,
                ArpeggioEnding = ending, ArpeggioLoopPoint = loopPoint,
            };
            made.Wavetable.AddRange(wavetable);
            made.Arpeggio.AddRange(program.Select(NoteName.Parse));
            made.Filter.AddRange(filter);
            return made;
        }
    }
}

public sealed class MidiImportException(string message) : Exception(message);
