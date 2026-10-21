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

    /// <summary>
    /// The harmony as arpeggio instruments, one per chord, the way the demo songs' ARP_ instruments
    /// work: the chord of each bar is found from the part, the same chord in any inversion is one
    /// instrument, and when there are more chords than instruments left the rare ones are picked
    /// note by note in the pattern instead.
    /// </summary>
    Chords,
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
    int? BarCount = null,
    bool UseBank = true,
    GmBank? Bank = null,
    bool KeepVibrato = false,
    bool ChordGateOffs = true)
{
    /// <summary>The bank parts' General MIDI programs are mapped to, or null for none.</summary>
    public GmBank? Sounds => UseBank ? Bank ?? GmBank.BuiltIn : null;
}

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
    int DrumsLeftOut = 0,
    int ChordsFound = 0,
    int ChordsKept = 0,
    int ChordsPicked = 0,
    int PickupRows = 0);

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

    /// <summary>Beats of three or more pitch classes a part needs before it is offered as the
    /// chords - fewer is a few passing moments, not an accompaniment.</summary>
    private const int MinChordBeats = 8;

    /// <summary>General MIDI's Acoustic Bass, program 33 - the bass a bass line gets when its own
    /// program is something else.</summary>
    private const int AcousticBass = 32;

    /// <summary>MIDI C3 - a lone part pitched lower on average is taken for a bass.</summary>
    private const int LowestLeadKey = 48;

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

    /// <summary>How many whole bars the chosen parts cover, a pickup's bar included.</summary>
    public static int BarsIn(MidiSong midi, IEnumerable<MidiSource?> sources, int rowsPerBeat)
    {
        var parts = sources.ToList();
        double ticksPerRow = midi.TicksPerQuarter / (double)rowsPerBeat;
        long last = parts.Where(s => s is not null).SelectMany(s => s!.Notes)
            .Select(n => (long)Math.Round(n.End / ticksPerRow)).DefaultIfEmpty(0).Max();
        int rowsPerBar = RowsPerBar(midi, rowsPerBeat);
        return (int)((last + PickupRows(midi, parts, rowsPerBeat) + rowsPerBar - 1) / rowsPerBar);
    }

    /// <summary>
    /// Rows of rest put in front of the song when the file starts on a pickup - an upbeat - so its
    /// bar lines fall where the music has them. Files written out from notation often start that
    /// way, with the first full bar a beat or two in, which shifts every bar after it: a waltz's
    /// bass note lands on the second beat, and bars that repeat no longer look alike.
    ///
    /// A bar line is where the lowest note of the bar starts. Each offset of whole beats is counted
    /// over the song, and one wins over no pickup only by clearly more bars.
    /// </summary>
    public static int PickupRows(MidiSong midi, IEnumerable<MidiSource?> sources, int rowsPerBeat)
    {
        var notes = sources.Where(s => s is not null).Distinct().SelectMany(s => s!.Notes).OrderBy(n => n.Start).ToList();
        if (notes.Count == 0)
            return 0;

        int rowsPerBar = RowsPerBar(midi, rowsPerBeat);
        int step = midi.BeatsPerBar > 0 && rowsPerBar % midi.BeatsPerBar == 0 ? rowsPerBar / midi.BeatsPerBar : rowsPerBeat;
        double ticksPerRow = midi.TicksPerQuarter / (double)rowsPerBeat, barTicks = rowsPerBar * ticksPerRow;
        long last = notes.Max(n => n.End);
        long longest = notes.Max(n => n.End - n.Start);

        int Bars(int offset)
        {
            int bars = 0, first = 0;
            for (double start = offset * ticksPerRow; start < last; start += barTicks)
            {
                double end = start + barTicks;
                while (first < notes.Count && notes[first].Start + longest <= start)
                    first++;
                MidiNote? lowest = null;
                for (int i = first; i < notes.Count && notes[i].Start < end; i++)
                {
                    var note = notes[i];
                    if (note.End > start && (lowest is null || note.Key < lowest.Key))
                        lowest = note;
                }
                if (lowest is not null && Math.Abs(lowest.Start - start) < ticksPerRow / 2)
                    bars++;
            }
            return bars;
        }

        int none = Bars(0), best = 0, bestBars = none;
        for (int offset = step; offset < rowsPerBar; offset += step)
        {
            int bars = Bars(offset);
            if (bars > bestBars)
                (best, bestBars) = (offset, bars);
        }
        return best > 0 && bestBars >= 2 * none && bestBars - none >= 4 ? rowsPerBar - best : 0;
    }

    /// <summary>
    /// A starting choice of parts, laid out the way the demo songs are: the lowest part as bass on
    /// voice 1 - with the drums when there are any, since a bass's filter settings suit channel 1 -
    /// the highest part, the lead, on voice 3, and voice 2 between them: the chords as arpeggios
    /// when a part carries them, else the next highest part. A part with under a fifth of the
    /// busiest one's notes is an intro or an effect, not a line to build on - unless it sits in the
    /// bass, where a line of whole bars has few notes and is still the bass. A part named for the
    /// lead or the melody is the lead, wherever it lies: the highest part of a band can be a pad.
    /// </summary>
    public static IReadOnlyList<VoiceImport> SuggestVoices(MidiSong midi)
    {
        var sources = midi.Sources();
        var parts = sources.Where(s => !s.LooksLikeDrums).ToList();
        int busiest = parts.Select(s => s.NoteCount).DefaultIfEmpty(0).Max();
        var melodic = parts.Where(s => s.NoteCount * 5 >= busiest || s.AverageKey < LowestLeadKey)
            .OrderByDescending(s => s.AverageKey).ToList();
        var drums = sources.Where(s => s.LooksLikeDrums).OrderByDescending(s => s.NoteCount).FirstOrDefault();
        var highest = melodic.FirstOrDefault(s => NamedLead(s.Name)) ?? melodic.ElementAtOrDefault(0);
        var lowest = melodic.Count > 1 ? melodic.Last(s => s != highest) : null;
        var next = melodic.FirstOrDefault(s => s != highest && s != lowest);

        // A single part that sits below C-3 is a bass line with nothing over it, not a lead.
        if (melodic.Count == 1 && melodic[0].AverageKey < LowestLeadKey)
            (lowest, highest) = (melodic[0], null);

        // The part that carries the harmony most, read as chords on voice 2.
        var harmony = parts.Select(s => (Source: s, Beats: ChordAnalyzer.ChordBeats(s, midi.TicksPerQuarter)))
            .Where(x => x.Beats >= MinChordBeats).OrderByDescending(x => x.Beats).Select(x => x.Source).FirstOrDefault();

        var bass = drums is not null
            ? new VoiceImport(lowest, VoiceRole.DrumsAndBass, Drums: drums)
            : new VoiceImport(lowest, VoiceRole.Bass);
        var middle = harmony is not null
            ? new VoiceImport(harmony, VoiceRole.Chords)
            : new VoiceImport(next, VoiceRole.Melody);
        return [bass, middle, new(highest, VoiceRole.Melody)];
    }

    private static bool NamedLead(string name) =>
        name.Contains("lead", StringComparison.OrdinalIgnoreCase)
        || name.Contains("melod", StringComparison.OrdinalIgnoreCase);

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
        int pickup = PickupRows(midi, options.Voices.SelectMany(PartsOf), options.RowsPerBeat);
        var range = new Range(midi.TicksPerQuarter / (double)options.RowsPerBeat, firstBar * rowsPerBar - pickup,
                              bars * rowsPerBar, options.RowsPerBeat, rowsPerBar);

        // Instruments are numbered as they are first used, so a sound nothing plays - a snare in
        // a part without one - never reaches the song. Chords go last: they take whatever of the 16
        // the other voices leave.
        var stats = new Stats();
        var instruments = new List<Instrument>();
        var lines = new PatternRow[]?[Song.ChannelCount];
        var order = Enumerable.Range(0, Song.ChannelCount)
            .OrderBy(c => options.Voices[c].Role == VoiceRole.Chords ? 1 : 0);
        foreach (int channel in order)
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

            lines[channel] = !voice.Plays ? null : voice.Role switch
            {
                VoiceRole.DrumsAndBass => DrumsAndBass(voice, channel, options, range, stats, Number),
                VoiceRole.Chords => Chords(voice, options, range, stats, SongValidator.MaxInstrumentNumber - instruments.Count, Number),
                _ => Melodic(voice, channel, options, range, stats, Number),
            };
        }

        MidiImportResult? best = null;
        string? lastProblem = null;
        foreach (int barsPerPattern in new[] { 1, 2, 4, 8 })
        {
            if (rowsPerBar * barsPerPattern > MaxRowsPerPattern)
                break;

            var song = Build(lines.ToList(), instruments, options.Speed, rowsPerBar * barsPerPattern);
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
                    stats.DrumHits, stats.DrumsLeftOut, stats.ChordsFound, stats.ChordsKept, stats.ChordsPicked, pickup));
            }
        }

        return best ?? throw new MidiImportException(lastProblem
            ?? $"A bar is {rowsPerBar} rows, more than the {MaxRowsPerPattern} a pattern can hold - try the coarser grid.");
    }

    private sealed class Stats
    {
        public int Placed, Dropped, Moved, Shifted, DrumHits, DrumsLeftOut, ChordsFound, ChordsKept, ChordsPicked;
    }

    /// <summary>The imported stretch of the file, in rows.</summary>
    private sealed record Range(double TicksPerRow, int FirstRow, int RowCount, int RowsPerBeat, int RowsPerBar)
    {
        /// <summary>A note's start and end rows within the range, or null when it lies outside.
        /// A note too short to reach the next row still gets one; one already sounding when the
        /// range begins starts before row 0.</summary>
        public (int Start, int End)? Rows(MidiNote note, Stats? stats)
        {
            double exact = note.Start / TicksPerRow;
            int start = (int)Math.Round(exact) - FirstRow;
            int end = Math.Max(start + 1, (int)Math.Round(note.End / TicksPerRow) - FirstRow);
            if (end <= 0 || start >= RowCount)
                return null;

            if (stats is not null && Math.Abs(exact - Math.Round(exact)) > 0.05)
                stats.Moved++;
            return (start, Math.Min(end, RowCount));
        }
    }

    private static PatternRow[] Melodic(VoiceImport voice, int channel, MidiImportOptions options, Range range,
                                        Stats stats, Func<string, Func<Instrument>, int> number)
    {
        var rows = MonoLine(voice.Source!, voice.Role, voice.Transpose, range, stats);
        if (!rows.Any(r => r is NoteRow))
            return rows;

        int instrument = number("voice", () => SoundFor(voice, channel, options).Instrument);
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
    private static PatternRow[] DrumsAndBass(VoiceImport voice, int channel, MidiImportOptions options, Range range,
                                             Stats stats, Func<string, Func<Instrument>, int> number)
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
                rows[row] = onset with { InstrumentNumber = number("bass", () => SoundFor(voice, channel, options).Instrument) };
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

    /// <summary>
    /// The chords, read a bar at a time - or half a bar, in a bar of four or more beats whose
    /// halves clearly hold two chords. A bar is read whole because an accompaniment spreads its
    /// chord over it: a waltz plays the root alone and two notes of the chord on each beat after,
    /// and no beat of it on its own is the chord. The bass under it counts in full, and the tune
    /// over it at half weight, since its notes often decide which chord it is.
    ///
    /// The most-played chords get an arpeggio instrument each, plain major and minor before the
    /// rest: a seventh needs twice the beats to be kept before one. With more chords than
    /// <paramref name="budget"/> instruments, the rest are picked out note by note in the pattern
    /// on one plain instrument, as far as they get before the next chord.
    /// </summary>
    private static PatternRow[] Chords(VoiceImport voice, MidiImportOptions options, Range range, Stats stats,
                                       int budget, Func<string, Func<Instrument>, int> number)
    {
        if (budget <= 0)
            throw new MidiImportException(
                $"The other voices use all {SongValidator.MaxInstrumentNumber} instruments - none is left for chords.");

        int count = range.RowCount, beatRows = range.RowsPerBeat;
        int beats = (count + beatRows - 1) / beatRows;
        int beatsPerBar = Math.Max(1, range.RowsPerBar / beatRows);

        var own = BeatWeights(voice.Source!, range, beats);
        var others = options.Voices
            .Where(v => v != voice && v.Role != VoiceRole.Chords && v.Source is not null && v.Source != voice.Source)
            .Select(v => (Weights: BeatWeights(v.Source!, range, beats), Factor: v.Role == VoiceRole.Melody ? 0.5 : 1.0))
            .ToList();

        // The lowest note of the chord part and the bass, per beat: at the start of a bar it is
        // usually the root, and gets a beat's weight more.
        var lowest = new int[beats];
        var struck = new bool[beats];
        Array.Fill(lowest, int.MaxValue);
        var low = options.Voices.Where(v => v.Role is VoiceRole.Bass or VoiceRole.DrumsAndBass && v.Source is not null)
            .Select(v => v.Source!).Append(voice.Source!).Distinct();
        foreach (var source in low)
        {
            foreach (var note in source.Notes)
            {
                if (range.Rows(note, null) is not { } at)
                    continue;
                if (source == voice.Source && at.Start >= 0)
                    struck[at.Start / beatRows] = true;
                for (int beat = Math.Max(0, at.Start) / beatRows; beat * beatRows < at.End; beat++)
                    lowest[beat] = Math.Min(lowest[beat], note.Key);
            }
        }

        // The tune helps name the chord, but its passing notes would make sevenths of half the
        // bars: a seventh has to be in the accompaniment - the chord part and the bass.
        double[] Window(int from, int to, bool withTune = true)
        {
            var weights = new double[12];
            for (int beat = from; beat < to; beat++)
            {
                for (int pc = 0; pc < 12; pc++)
                    weights[pc] += own[beat][pc] + others.Where(o => withTune || o.Factor == 1.0).Sum(o => o.Factor * o.Weights[beat][pc]);
            }
            if (lowest[from] != int.MaxValue)
                weights[lowest[from] % 12] += beatRows;
            return weights;
        }

        Chord? Name(int from, int to, Chord? before) =>
            ChordAnalyzer.Identify(Window(from, to), before, seventhWeights: Window(from, to, withTune: false));

        bool Sounds(int beat) => own[beat].Any(w => w > 0);

        var chords = new Chord?[beats];
        Chord? previous = null;
        for (int from = 0; from < beats; from += beatsPerBar)
        {
            int to = Math.Min(beats, from + beatsPerBar);
            if (!Enumerable.Range(from, to - from).Any(Sounds))
                continue;

            var whole = Name(from, to, previous);
            var spans = new List<(int From, int To, Chord? Chord)> { (from, to, whole) };
            if (whole is not null && beatsPerBar >= 4 && beatsPerBar % 2 == 0 && to - from == beatsPerBar)
            {
                int middle = from + beatsPerBar / 2;
                var (first, second) = (Window(from, middle), Window(middle, to));
                var one = Name(from, middle, previous);
                var two = Name(middle, to, one ?? previous);
                if (one is not null && two is not null && one != two
                    && Explained(first, one) + Explained(second, two)
                       - Explained(first, whole) - Explained(second, whole) >= SplitMargin)
                    spans = [(from, middle, one), (middle, to, two)];
            }

            foreach (var (spanFrom, spanTo, chord) in spans)
                for (int beat = spanFrom; beat < spanTo; beat++)
                    chords[beat] = Sounds(beat) ? chord : null;
            previous = spans[^1].Chord ?? previous;
        }

        var played = chords.Select((c, beat) => (c, beat)).Where(x => x.c is not null)
            .GroupBy(x => x.c!).Select(g => (Chord: g.Key, Beats: g.Count(), First: g.Min(x => x.beat)))
            .OrderByDescending(g => g.Beats * (g.Chord.Intervals.Count == 3 ? 1.0 : 0.5)).ThenBy(g => g.First)
            .ToList();
        int instruments = played.Count <= budget ? played.Count : budget - 1;
        var kept = played.Take(instruments).Select(g => g.Chord).ToHashSet();
        stats.ChordsFound += played.Count;
        stats.ChordsKept += kept.Count;
        stats.ChordsPicked += played.Count - kept.Count;

        // Where a chord is struck: when it changes, and again on a beat where the part plays new
        // notes - the accompaniment's own rhythm - and where the harmony stops.
        var strikes = new List<(int Row, Chord? Chord)>();
        Chord? sounding = null;
        for (int beat = 0; beat < beats; beat++)
        {
            var chord = chords[beat];
            if (chord != sounding || (chord is not null && struck[beat]))
                strikes.Add((beat * beatRows, chord));
            sounding = chord;
        }

        // How long each chord rings before the next thing happens, in 16th rows.
        int unit = Math.Max(1, beatRows / 4);
        var lengths = strikes.Select((s, i) => (s.Row, s.Chord, Span: (i + 1 < strikes.Count ? strikes[i + 1].Row : count) - s.Row))
            .Where(s => s.Chord is not null).ToList();
        var release = lengths.GroupBy(s => s.Chord!).ToDictionary(g => g.Key, g =>
        {
            var spans = g.Select(s => s.Span).Order().ToList();
            int median = spans[spans.Count / 2];
            return median > 16 * unit ? 0x0C : median > 8 * unit ? 0x0B : 0x0A;
        });

        // The demo songs never let an arpeggio ring on: its gate goes off three rows after it is
        // struck, four for a chord that usually has more than eight rows to itself - which also
        // gets the longer release, so it fades over the gap instead of stopping. The gate-off is in
        // the instrument's wavetable, as the demo songs' ARP_ instruments have it, not a --- in the
        // pattern: measured on the 25 midifiles, a pattern gate-off costs about three bytes each
        // (+37 to +1971 a song), the wavetable one +11 to +92, shared by every chord instrument.
        int? GateFrames(Chord chord) => options.ChordGateOffs && voice.Instrument is null
            ? (release[chord] > 0x0A ? 4 : 3) * unit * (options.Speed + 1)
            : null;

        var rows = Enumerable.Repeat<PatternRow>(new HoldRow(), count).ToArray();
        for (int i = 0; i < strikes.Count; i++)
        {
            var (row, chord) = strikes[i];
            if (chord is null)
            {
                rows[row] = new NoteOffRow();
            }
            else if (kept.Contains(chord))
            {
                rows[row] = new NoteRow(chord.Notes[0], number("chord " + chord.Name,
                    () => ChordInstrument(chord, voice.Instrument, voice.Instrument is null ? release[chord] : null, GateFrames(chord))));
            }
            else
            {
                // No instrument of its own: its notes in turn, a 16th each, up to the next strike.
                int picked = number("picked chords", () => PickedChordInstrument(voice.Instrument));
                int next = i + 1 < strikes.Count ? strikes[i + 1].Row : count;
                for (int at = row, n = 0; at < next; at += unit, n++)
                    rows[at] = new NoteRow(chord.Notes[n % chord.Notes.Count], picked);
            }
        }

        return rows;
    }

    /// <summary>How much better the halves of a bar must fit chords of their own - the shares of
    /// each half's weight they play, summed - before the bar is read as two chords.</summary>
    private const double SplitMargin = 0.25;

    /// <summary>The share of <paramref name="weights"/> that <paramref name="chord"/> plays.</summary>
    private static double Explained(double[] weights, Chord chord)
    {
        double total = weights.Sum();
        return total <= 0 ? 0 : chord.PitchClasses.Sum(pc => weights[pc]) / total;
    }

    /// <summary>Rows each pitch class sounds in every beat of <paramref name="source"/>.</summary>
    private static double[][] BeatWeights(MidiSource source, Range range, int beats)
    {
        var weights = Enumerable.Range(0, beats).Select(_ => new double[12]).ToArray();
        foreach (var note in source.Notes)
        {
            if (range.Rows(note, null) is not { } at)
                continue;
            for (int row = Math.Max(0, at.Start); row < at.End; row++)
                weights[row / range.RowsPerBeat][note.Key % 12]++;
        }
        return weights;
    }

    /// <summary>The sound a chord without an instrument of its own is picked on, a note at a time
    /// in the pattern: <paramref name="template"/>, or the demo songs' pulse arpeggio, with no
    /// note program and no gate-off - the pattern's next note is what stops it.</summary>
    public static Instrument PickedChordInstrument(Instrument? template = null)
    {
        var instrument = template is not null ? Clone(template) : new Instrument
        {
            Waveform = 0x41, AttackDecay = 0x00, SustainRelease = 0x8A, PulseWidth = 2, PulseSweep = 22,
        };
        if (template is null)
            instrument.Wavetable.AddRange([0x09, 0x41]);
        instrument.Name = "Chord notes";
        instrument.Arpeggio.Clear();
        instrument.ArpeggioEnding = ArpeggioEnding.Root;
        instrument.ArpeggioLoopPoint = 0;
        return instrument;
    }

    /// <summary>An arpeggio of <paramref name="chord"/>, one note a frame round and round, on the
    /// sound of <paramref name="template"/> - or the demo songs' pulse arpeggio, with
    /// <paramref name="release"/> as its release when given, and its gate going off after
    /// <paramref name="gateFrames"/> frames when given.</summary>
    public static Instrument ChordInstrument(Chord chord, Instrument? template = null, int? release = null,
                                             int? gateFrames = null)
    {
        var instrument = template is not null ? Clone(template) : new Instrument
        {
            Waveform = 0x41, AttackDecay = 0x00, SustainRelease = (byte)(0x80 | (release ?? 0x0A)),
            PulseWidth = 2, PulseSweep = 22,
        };
        if (template is null)
        {
            instrument.Wavetable.Add(0x09);
            for (int frame = 1; frame < (gateFrames ?? 2); frame++)
                instrument.Wavetable.Add(0x41);
            if (gateFrames is not null)
                instrument.Wavetable.Add(0x40);              // gate off, and the last step holds
        }
        instrument.Name = "Chord " + chord.Name;
        instrument.Arpeggio.Clear();
        instrument.Arpeggio.AddRange(chord.Notes);
        instrument.ArpeggioEnding = ArpeggioEnding.Loop;
        instrument.ArpeggioLoopPoint = 0;
        return instrument;
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

    /// <summary>Three channels' rows as a song: cut into patterns of <paramref name="rowsPerPattern"/>,
    /// each distinct one stored once - shared with the SID import.</summary>
    internal static Song Build(List<PatternRow[]?> lines, List<Instrument> instruments, int speed, int rowsPerPattern)
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
    internal static string? Problem(Song song)
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

    /// <summary>
    /// The sound a melodic voice - or the bass of a drums-and-bass voice - plays its notes on, and
    /// how to name it. An instrument the user loaded is used as it is. Otherwise the part's General
    /// MIDI program picks a sound from the bank, made to fit the import: no vibrato unless asked
    /// for, since the first instrument with vibrato costs the player 112 bytes, and a filter
    /// program only on channel 1 - the SID has one filter, and that is the bass's channel - routed
    /// to it. A part without a program gets the role's plain default.
    /// </summary>
    public static (Instrument Instrument, string Description) SoundFor(VoiceImport voice, int channel, MidiImportOptions options)
    {
        if (voice.Instrument is { } loaded)
            return (Clone(loaded), loaded.Name.Length > 0 ? loaded.Name : "(loaded)");

        // General MIDI plays a channel that never names a program as program 1, the grand piano;
        // a bass part keeps the bass, which suits it better than a piano would.
        int? program = voice.Source?.Program ?? (voice.Role == VoiceRole.Melody ? 0 : null);

        // A bass line keeps a bass sound whatever its part was written for: strings or a left
        // hand playing the bass are the bass here, and the bank's pad or piano would not carry it.
        if (voice.Role is VoiceRole.Bass or VoiceRole.DrumsAndBass && program is { } written
            && GmBank.KeyFor(written) is not ("Bass" or "SynthBass"))
            program = AcousticBass;
        if (voice.Role is VoiceRole.Melody or VoiceRole.Bass or VoiceRole.DrumsAndBass
            && options.Sounds?.For(program) is { } sound)
        {
            var instrument = Clone(sound.Instrument);
            if (!options.KeepVibrato)
                (instrument.VibratoDelaySpeed, instrument.VibratoDepth) = ((byte)0, (byte)0);
            if (channel == 0)
                RouteFilter(instrument, channel);
            else
                instrument.Filter.Clear();
            string named = voice.Source?.Program is null ? ", no program" : "";
            return (instrument, $"{instrument.Name} - {sound.Origin} (GM {program!.Value + 1} {GmBank.FamilyName(program.Value)}{named})");
        }

        var plain = voice.Role == VoiceRole.DrumsAndBass ? Kit("Bass", channel) : Default(voice.Role);
        return (plain, $"{plain.Name} (default)");
    }

    /// <summary>Points every setup step of the filter program at <paramref name="channel"/>; the
    /// resonance in the high nibble is kept.</summary>
    private static void RouteFilter(Instrument instrument, int channel)
    {
        for (int i = 0; i < instrument.Filter.Count; i++)
        {
            var step = instrument.Filter[i];
            if (step.IsSetup)
                instrument.Filter[i] = FilterStep.Setup(step.ModeVolume!.Value,
                    (byte)(step.ResonanceRouting!.Value & 0xF0 | 1 << channel), step.Cutoff, step.SweepRate);
        }
    }

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

        RouteFilter(instrument, channel);
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
