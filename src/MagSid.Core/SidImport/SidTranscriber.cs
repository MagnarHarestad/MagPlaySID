using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Core.SidImport;

/// <summary>A tune's SID register writes, one list per 50 Hz frame, in the order they were made.
/// <see cref="Init"/> is what init wrote before the first frame.</summary>
public sealed record SidTrace(IReadOnlyList<(int Register, int Value)> Init, IReadOnlyList<IReadOnlyList<(int Register, int Value)>> Frames);

/// <param name="FramesPerRow">The row grid: the song's speed is one less.</param>
/// <param name="FirstFrame">Where the import starts, in frames from the tune's start.</param>
/// <param name="FrameCount">How many frames it takes.</param>
/// <param name="Filter">Carry the filter over as filter programs.</param>
/// <param name="Vibrato">Carry vibrato over.</param>
/// <param name="FunkFramesPerRow">With funk speed, every second row's length; null for one speed.</param>
public sealed record SidImportOptions(int FramesPerRow, int FirstFrame, int FrameCount, bool Filter = true, bool Vibrato = true,
                                      int? FunkFramesPerRow = null);

public sealed record SidImportReport(
    int Frames,
    int FramesPerRow,
    int Rows,
    int RowsPerPattern,
    int Patterns,
    int TotalBytes,
    int NotesPlaced,
    int NotesMoved,
    int NotesDropped,
    int NotesShifted,
    int SoundsFound,
    int InstrumentsMade,
    int NotesOnNearSound,
    bool UsesFilter,
    string Roles = "");

public sealed record SidImportResult(Song Song, SidImportReport Report);

public sealed class SidImportException(string message) : Exception(message);

/// <summary>
/// Turns what a SID tune did to the chip back into a MagSID song.
///
/// A .sid is a program, not a score: the notes, the sounds and the effects exist only as the
/// register writes it makes, 50 times a second. So a note is a gate-on, its pitch the note nearest
/// the frequency it holds, and its sound everything the voice does from there - the control byte
/// frame by frame becomes the wavetable, steps of pitch the note program, the pulse width and its
/// sweep, vibrato, ADSR and the filter carry over. Notes that do the same are one instrument; the
/// sixteen sounds used most are kept and the rest play on the nearest of them.
///
/// It is a transcription, not an emulation: what the format cannot say - a tempo that changes, a
/// slide, a filter sweeping across notes, a player called several times a frame - is heard the
/// way the nearest thing it can say sounds.
/// </summary>
public static partial class SidTranscriber
{
    /// <summary>Frames of a note looked at for its sound. The player's tables are one byte a frame,
    /// and a sound that has not settled in this long is near enough to one that has.</summary>
    private const int ProgramFrames = 32;

    /// <summary>A gate-off this close before the next gate-on is the tune's hard restart, not the
    /// end of the note: MagSID's player makes its own.</summary>
    private const int HardRestartFrames = 3;

    private const int MaxRowsPerPattern = 64;

    /// <summary>What playing a sound on one of another role costs, against <see cref="Sound.Distance"/>:
    /// more than another waveform or note program, so an arpeggio goes on an arpeggio, not the kick.</summary>
    private const int OtherRole = 4000;

    /// <summary>What every voice does in every frame, and where its notes start - worked out once
    /// per tune, before any choice is made.</summary>
    public sealed class Analysis
    {
        internal Analysis(VoiceFrame[][] voices, GlobalFrame[] global, List<NoteEvent>[] notes, double tuning)
        {
            Voices = voices;
            Global = global;
            Notes = notes;
            Tuning = tuning;
        }

        /// <summary>How far the tune's notes sit from MagSID's, in semitones (<see cref="TuningOf"/>).</summary>
        internal double Tuning { get; }

        internal VoiceFrame[][] Voices { get; }

        internal GlobalFrame[] Global { get; }

        internal List<NoteEvent>[] Notes { get; }

        public int FrameCount => Global.Length;

        public int NoteCount => Notes.Sum(n => n.Count);
    }

    internal readonly record struct VoiceFrame(int Frequency, int PulseWidth, byte Control, byte AttackDecay, byte SustainRelease);

    internal readonly record struct GlobalFrame(int Cutoff, byte ResonanceRouting, byte ModeVolume);

    /// <param name="Start">The frame the gate went on - or, for a legato note, the frame the pitch
    /// moved to it with the gate held.</param>
    /// <param name="GateOff">The frame it went off, if it did before the next note.</param>
    /// <param name="Legato">Started without the gate going on.</param>
    /// <param name="LegatoNext">The note after it is legato.</param>
    /// <param name="HardRestart">Where the tune's hard restart for the next note begins, if it
    /// has one - the note's own sound ends there.</param>
    internal sealed record NoteEvent(int Voice, int Start, int End, int? GateOff, bool Legato = false, bool LegatoNext = false, int? HardRestart = null)
    {
        /// <summary>Where the note's own sound ends: its gate-off, the next note's hard restart, or
        /// the next note.</summary>
        public int SoundEnd => GateOff ?? HardRestart ?? End;
    }

    /// <summary>Frames a pitch has to hold, on both sides of a change, for the change to be a new
    /// note played legato rather than an arpeggio, a vibrato or a drum's strike.</summary>
    private const int LegatoHold = 3;

    // ------------------------------------------------------------------------------------ analysis

    public static Analysis Analyze(SidTrace trace)
    {
        var registers = new byte[0x20];
        foreach (var (register, value) in trace.Init)
            registers[register & 0x1F] = (byte)value;

        int frames = trace.Frames.Count;
        var voices = new VoiceFrame[3][];
        for (int v = 0; v < 3; v++)
            voices[v] = new VoiceFrame[frames];
        var global = new GlobalFrame[frames];
        var rises = new List<int>[3] { [], [], [] };
        var falls = new List<int>[3] { [], [], [] };

        for (int f = 0; f < frames; f++)
        {
            bool[] rose = new bool[3], fell = new bool[3];
            foreach (var (register, value) in trace.Frames[f])
            {
                int r = register & 0x1F;
                if (r < 0x15 && r % 7 == 4)
                {
                    // A gate that goes on - even off and on again inside one call, which is how a
                    // player restarts a note without leaving a frame of silence - starts a note.
                    int v = r / 7;
                    bool was = (registers[r] & 1) != 0, now = (value & 1) != 0;
                    if (now && !was)
                        rose[v] = true;
                    else if (!now && was && !rose[v])
                        fell[v] = true;
                }

                registers[r] = (byte)value;
            }

            for (int v = 0; v < 3; v++)
            {
                int b = v * 7;
                voices[v][f] = new VoiceFrame(
                    registers[b] | (registers[b + 1] << 8),
                    registers[b + 2] | ((registers[b + 3] & 0x0F) << 8),
                    registers[b + 4], registers[b + 5], registers[b + 6]);
                if (rose[v])
                    rises[v].Add(f);
                if (fell[v])
                    falls[v].Add(f);
            }

            global[f] = new GlobalFrame((registers[0x15] & 7) | (registers[0x16] << 3), registers[0x17], registers[0x18]);
        }

        for (int v = 0; v < 3; v++)
            rises[v] = [.. rises[v].Concat(Retriggers(voices[v], rises[v])).Distinct().Order()];

        double tuning = TuningOf(voices);
        var notes = new List<NoteEvent>[3];
        for (int v = 0; v < 3; v++)
        {
            notes[v] = [];
            for (int i = 0; i < rises[v].Count; i++)
            {
                int start = rises[v][i];
                int end = i + 1 < rises[v].Count ? rises[v][i + 1] : frames;
                int? off = falls[v].Where(f => f > start && f < end).Select(f => (int?)f).FirstOrDefault();

                // Gate-off just before the next gate-on is the tune's hard restart.
                int? hardRestart = null;
                if (off is { } o && end < frames && end - o <= HardRestartFrames)
                {
                    hardRestart = o;
                    off = null;
                }

                // With the gate held, a pitch that moves to another note and stays is a new note.
                int from = start;
                bool legato = false;
                foreach (int change in LegatoChanges(voices[v], start, off ?? hardRestart ?? end, tuning))
                {
                    notes[v].Add(new NoteEvent(v, from, change, null, legato, LegatoNext: true));
                    from = change;
                    legato = true;
                }

                notes[v].Add(new NoteEvent(v, from, end, off, legato, HardRestart: hardRestart));
            }
        }

        return new Analysis(voices, global, notes, tuning);
    }

    /// <summary>
    /// How far the tune is tuned from MagSID's note table, in semitones: GoatTracker's table is at
    /// A-4 = 440 Hz, a quarter of a semitone above MagSID's, so every note of a GoatTracker tune
    /// sat just outside <see cref="OnNote"/>'s quarter semitone and the pitch was read from a
    /// drum's noise or the last note's leftover frequency instead. Read from the frames that hold a
    /// tonal pitch steady with the gate on - a vibrato or a slide passes through, a note holds.
    /// </summary>
    private static double TuningOf(VoiceFrame[][] voices)
    {
        double sin = 0, cos = 0;
        int count = 0;
        foreach (var frames in voices)
        {
            for (int f = 1; f < frames.Length; f++)
            {
                var frame = frames[f];
                if ((frame.Control & 0x01) == 0 || !Tonal(frame.Control) || frame.Frequency < 0x100
                    || frame.Frequency != frames[f - 1].Frequency)
                    continue;

                // The distance from the nearest note, as an angle: -0.5 and +0.5 are the same place.
                double semitones = 12 * Math.Log2(frame.Frequency / NoteFrequencies[0]);
                double angle = 2 * Math.PI * (semitones - Math.Round(semitones));
                sin += Math.Sin(angle);
                cos += Math.Cos(angle);
                count++;
            }
        }

        // Too few steady notes, or no one tuning they agree on: as MagSID's own.
        if (count < 16 || Math.Sqrt(sin * sin + cos * cos) < count * 0.5)
            return 0;
        return Math.Atan2(sin, cos) / (2 * Math.PI);
    }

    /// <summary>Pulse, saw or triangle, without noise or the test bit - a waveform with a pitch.</summary>
    private static bool Tonal(byte control) => (control & 0x70) != 0 && (control & 0x88) == 0;

    /// <summary>Silent this frame: the test bit holds the oscillator, or no waveform is chosen.</summary>
    private static bool Silent(byte control) => (control & 0x08) != 0 || (control & 0xF0) == 0;

    private static IEnumerable<int> LegatoChanges(VoiceFrame[] frames, int start, int end, double tuning)
    {
        for (int f = start + LegatoHold; f + LegatoHold <= end; f++)
        {
            int now = NearestNote(frames[f].Frequency, tuning);
            int before = NearestNote(frames[f - 1].Frequency, tuning);
            if (now < 0 || before < 0 || now == before)
                continue;

            bool held = true;
            for (int k = 1; k < LegatoHold && held; k++)
                held = NearestNote(frames[f + k].Frequency, tuning) == now && NearestNote(frames[f - 1 - k].Frequency, tuning) == before;
            if (held)
                yield return f;
        }
    }

    /// <summary>
    /// Notes a player starts without touching the gate: the gate stays on, the voice goes silent
    /// for a frame or more - the test bit, or no waveform - and sounds again on another pitch.
    /// SIDdump counts a note there; Laxity's player plays its notes so. On the same pitch it is a
    /// drum resetting its noise with the test bit, and stays one note. The note starts on its
    /// first silent frame, as a gated one does on the frame its hard restart's test bit is written.
    /// Measured over 202 tunes: Laxity's Hawalt +5.9 points of pitch heard, the mean unchanged.
    /// </summary>
    private static IEnumerable<int> Retriggers(VoiceFrame[] frames, List<int> rises)
    {
        bool sounding = false;
        int silentFrom = -1;
        for (int f = 0; f < frames.Length; f++)
        {
            var frame = frames[f];
            bool gate = (frame.Control & 1) != 0;
            if (!gate)
            {
                sounding = false;
                silentFrom = -1;
                continue;
            }

            if (Silent(frame.Control))
            {
                if (sounding && silentFrom < 0)
                    silentFrom = f;
                continue;
            }

            if (silentFrom >= 0 && !rises.Any(r => r >= silentFrom && r <= f)
                && frames[f].Frequency != frames[silentFrom - 1].Frequency)
                yield return silentFrom;
            silentFrom = -1;
            sounding = true;
        }
    }

    /// <summary>
    /// The row grid that puts the most note starts on a row: the longest one that still puts
    /// nine in ten there, from the phase that fits best. A tune with swing - rows of 6 and 5 frames
    /// in turn, GoatTracker's funktempo - fits a grid of two row lengths, which MagSID's funk
    /// speed plays; at the same average length one row length is preferred. One that fits neither
    /// gets the finest grid that does.
    /// </summary>
    public static (int FramesPerRow, int? FunkFramesPerRow) SuggestGrid(Analysis analysis, int firstFrame = 0, int? frameCount = null)
    {
        var starts = Starts(analysis, firstFrame, frameCount ?? analysis.FrameCount - firstFrame);
        if (starts.Count == 0)
            return (6, null);

        // A swing has to be heard often enough to be told from chance: fifteen notes that a
        // straight 6 fits fit some pair of 18 frames as well, and EmpireApproved became 11/7.
        bool swingShows = starts.Count >= MinStartsForFunk;

        // Longest pair of rows first; at each length one row length before two. A pair has to put
        // nine starts in ten on a row or a frame after it, not only within a frame either side:
        // through that tolerance a straight 6 passed as 11/7 and 8/5 as 9/4. Of the pairs that
        // fit, the one with most starts that close is the tune's.
        for (int pair = 32; pair >= 4; pair--)
        {
            if (pair % 2 == 0 && pair / 2 <= 16 && BestPhase(starts, pair / 2, pair / 2).Aligned >= starts.Count * 0.9)
                return (pair / 2, null);
            if (!swingShows)
                continue;

            var funk = Enumerable.Range(2, pair - 3)
                .Select(a => (A: a, B: pair - a))
                .Where(g => g.A != g.B && g.A <= 16 && g.B <= 16 && Math.Abs(g.A - g.B) <= MaxFunkDifference)
                .Select(g => (g.A, g.B, Fit: BestPhase(starts, g.A, g.B)))
                .Where(g => g.Fit.Aligned >= starts.Count * 0.9 && g.Fit.Exact >= starts.Count * 0.9)
                .OrderByDescending(g => g.Fit.Exact).ThenByDescending(g => g.A)
                .FirstOrDefault();
            if (funk.A != 0)
                return (funk.A, funk.B);
        }

        return (1, null);
    }

    /// <summary>The longest single row length: <see cref="SuggestGrid"/> without funk speed.</summary>
    public static int SuggestFramesPerRow(Analysis analysis, int firstFrame = 0, int? frameCount = null)
    {
        var starts = Starts(analysis, firstFrame, frameCount ?? analysis.FrameCount - firstFrame);
        if (starts.Count == 0)
            return 6;

        for (int framesPerRow = 16; framesPerRow > 1; framesPerRow--)
        {
            if (BestPhase(starts, framesPerRow, framesPerRow).Aligned >= starts.Count * 0.9)
                return framesPerRow;
        }

        return 1;
    }

    /// <summary>GoatTracker songs swing by a frame or a few - 6/5, 8/7, 9/6, 8/4 in Magnar's own;
    /// a wider pair would mostly be a straight grid seen through the one-frame tolerance.</summary>
    private const int MaxFunkDifference = 5;

    /// <summary>Note starts it takes to tell a swing from chance.</summary>
    private const int MinStartsForFunk = 64;

    private static List<int> Starts(Analysis analysis, int firstFrame, int frameCount) =>
        [.. analysis.Notes.SelectMany(n => n).Select(n => n.Start)
            .Where(s => s >= firstFrame && s < firstFrame + frameCount).Select(s => s - firstFrame)];

    /// <summary>The phase of the grid - rows of <paramref name="a"/> frames, or of a and
    /// <paramref name="b"/> in turn - that puts most starts on a row. From 4 frames a row up a
    /// start a frame either side counts as on it: a player's own hard restart moves a gate-on by a
    /// frame, depending on what played before.</summary>
    private static (int Phase, int Aligned, int Exact) BestPhase(List<int> starts, int a, int b)
    {
        int period = a == b ? a : a + b;
        int[] rows = a == b ? [0] : [0, a];
        bool tolerant = Math.Min(a, b) >= 4;
        var counts = new int[period];
        foreach (int s in starts)
            counts[s % period]++;

        int Near(int phase) => rows.Sum(r => tolerant
            ? counts[(phase + r) % period] + counts[(phase + r + 1) % period] + counts[(phase + r + period - 1) % period]
            : counts[(phase + r) % period]);
        int On(int phase) => rows.Sum(r => counts[(phase + r) % period]);
        // On the row or a frame after it, never before: GoatTracker plays some of a row's notes a
        // frame late - Game of Chance's every one of them on its first frame or its second.
        int OnOrLate(int phase) => rows.Sum(r => counts[(phase + r) % period] + counts[(phase + r + 1) % period]);

        // A pair's phase is the one that puts its rows at the front of those late clusters.
        int best = a == b
            ? Enumerable.Range(0, period).MaxBy(p => (Near(p), On(p)))
            : Enumerable.Range(0, period).MaxBy(p => (OnOrLate(p), Near(p), On(p)));
        return (best, Near(best), OnOrLate(best));
    }

    /// <summary>Where the song's rows fall: a row of <see cref="A"/> frames and then, with funk
    /// speed, one of <see cref="B"/>, in turn from <see cref="Origin"/> - the first row is an A
    /// row, as the player's first tick is v_Song_Speed's.</summary>
    private readonly record struct RowGrid(int A, int B, int Origin)
    {
        public bool Funk => A != B;

        public int Shortest => Math.Min(A, B);

        public int FrameOf(int row) => Funk ? Origin + row / 2 * (A + B) + (row & 1) * A : Origin + row * A;

        /// <summary>The row whose start is nearest the frame.</summary>
        public int RowOf(int frame)
        {
            if (!Funk)
                return (int)Math.Floor((frame - Origin) / (double)A + 0.5);

            int row = 2 * (int)Math.Floor((frame - Origin) / (double)(A + B));
            int nearest = row;
            for (int next = row + 1; next <= row + 2; next++)
            {
                if (Math.Abs(FrameOf(next) - frame) <= Math.Abs(FrameOf(nearest) - frame))
                    nearest = next;
            }

            return nearest;
        }

        /// <summary>Rows from the first until one starts at or past <paramref name="end"/>.</summary>
        public int RowsTo(int end)
        {
            int rows = Funk ? 2 * Math.Max(0, (end - Origin) / (A + B)) : Math.Max(0, (end - Origin) / A);
            while (FrameOf(rows) < end)
                rows++;
            return Math.Max(1, rows);
        }
    }

    /// <summary>The song's grid: on the best phase, its first row the one the first note starts
    /// on. Counted from the stretch's own start, the phase put Viva Las Vegas's first kick, seven
    /// frames in, on the second row. With funk speed a first note on a B row swaps the pair, so the
    /// song still starts on its first note, on a tick of the length it has.</summary>
    private static RowGrid GridOf(Analysis analysis, int first, int count, int a, int b)
    {
        var starts = Starts(analysis, first, count);
        var (phase, _, _) = BestPhase(starts, a, b);
        var grid = new RowGrid(a, b, first + phase);
        if (starts.Count == 0)
            return grid;

        int row = Math.Max(0, grid.RowOf(first + starts.Min()));
        return grid.Funk && (row & 1) == 1
            ? new RowGrid(b, a, grid.FrameOf(row))
            : grid with { Origin = grid.FrameOf(row) };
    }

    // ------------------------------------------------------------------------------------ import

    public static SidImportResult Import(Analysis analysis, SidImportOptions options)
    {
        int first = Math.Clamp(options.FirstFrame, 0, Math.Max(0, analysis.FrameCount - 1));
        int count = Math.Clamp(options.FrameCount, 1, analysis.FrameCount - first);

        var notes = analysis.Notes
            .Select(list => list.Where(n => n.Start >= first && n.Start < first + count).ToList())
            .ToArray();
        if (notes.All(n => n.Count == 0))
            throw new SidImportException("No notes start in this stretch of the tune - the gates never go on.");

        var grid = GridOf(analysis, first, count, Math.Clamp(options.FramesPerRow, 1, 64),
                          Math.Clamp(options.FunkFramesPerRow ?? options.FramesPerRow, 1, 64));
        int framesPerRow = grid.Shortest;
        int rowCount = grid.RowsTo(first + count);
        int RowOf(int frame) => grid.RowOf(frame);

        // The master volume is the song's, not a sound's: a tune that fades in sets it again on
        // every note, and as part of each filter setup it made five of every filtered sound.
        byte volume = (byte)Enumerable.Range(first, count).Select(f => analysis.Global[f].ModeVolume & 0x0F)
            .GroupBy(v => v).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;

        var stats = new Stats();
        var groups = new Dictionary<string, List<Sound>>();
        var usage = new Dictionary<Sound, Usage>();
        var placed = new List<(int Voice, int Row, Sound Sound, NoteName Note, int? OffRow)>();

        var heard = notes.SelectMany(n => n)
            .Select(note => (Note: note, Sound: SoundOf(analysis, note, options, framesPerRow, volume, out var root, stats), Root: root))
            .ToList();
        var relative = RelativeTransients(heard.Select(h => (h.Sound, h.Root)));

        foreach (var (note, heardSound, root) in heard)
        {
            var sound = TransientOf(heardSound, root) is { } transient && relative.Contains(transient)
                ? heardSound.WithoutNotes()
                : heardSound;
            if (!groups.TryGetValue(sound.Key, out var members))
                groups[sound.Key] = members = [];
            members.Add(sound);
            var known = members[0];

            int row = Math.Max(0, RowOf(note.Start));
            if (Math.Abs(note.Start - grid.FrameOf(row)) > (framesPerRow < 4 ? 0 : 1))
                stats.Moved++;

            int? offRow = sound.GateOffInProgram || note.GateOff is not { } off ? null : RowOf(off);
            placed.Add((note.Voice, row, known, root, offRow));

            if (!usage.TryGetValue(known, out var used))
                usage[known] = used = new Usage();
            used.Rows.Add(row);
            used.Roots.Add(root.TableIndex);
            used.Lengths.Add(note.SoundEnd - note.Start);
        }

        // A sound's settings - where its pulse starts and how it sweeps, its vibrato, its cutoff -
        // are what most of its notes do. The sixteen places go by role - snares, then the bass and
        // kick-bass, then leads, then arpeggios - and within a role to the sounds used most. The rest
        // play on the nearest sound of their own role, so a snare is never played on a lead.
        var sounds = groups.Values.ToDictionary(g => g[0], Sound.Typical);
        var families = sounds.Keys.GroupBy(FamilyOf)
            .ToDictionary(g => g.Key, g => g.SelectMany(k => usage[k].Rows).ToList());
        var roles = sounds.ToDictionary(s => s.Key, s => RoleOf(s.Value, usage[s.Key], families[FamilyOf(s.Key)]));
        var kept = TakeInTurn(sounds, roles);
        var instruments = new List<Instrument>();
        var numberOf = new Dictionary<Sound, int>();
        foreach (var sound in kept)
        {
            var instrument = sounds[sound].ToInstrument(instruments.Count + 1, NameFor(sounds[sound], roles[sound], instruments));
            instruments.Add(instrument);
            numberOf[sound] = instrument.Number;
        }

        int NumberFor(Sound sound)
        {
            if (numberOf.TryGetValue(sound, out int number))
                return number;
            // Only onto an instrument that plays the row's note: one whose note program loops or holds
            // fixed notes plays those whatever the row says - Viva Las Vegas's bass came out as F-4.
            var own = sounds[sound].Notes;
            var candidates = kept.Where(k => sounds[k].Notes is null || sounds[k].Ending == ArpeggioEnding.Root
                                             || (own is not null && sounds[k].Notes!.SequenceEqual(own))).ToList();
            var nearest = (candidates.Count > 0 ? candidates : kept)
                .MinBy(k => sounds[k].Distance(sounds[sound]) + (roles[k] == roles[sound] ? 0 : OtherRole))!;
            return numberOf[sound] = numberOf[nearest];
        }
        var keptSet = kept.ToHashSet();

        var lines = new List<PatternRow[]?>();
        for (int v = 0; v < Song.ChannelCount; v++)
        {
            var mine = placed.Where(p => p.Voice == v).OrderBy(p => p.Row).ToList();
            if (mine.Count == 0)
            {
                lines.Add(null);
                continue;
            }

            var rows = new PatternRow[rowCount];
            Array.Fill(rows, new HoldRow());
            for (int i = 0; i < mine.Count; i++)
            {
                var (_, row, sound, note, offRow) = mine[i];
                if (row >= rowCount)
                    continue;
                if (rows[row] is NoteRow)
                {
                    stats.Dropped++;
                    continue;
                }

                rows[row] = new NoteRow(note, NumberFor(sound));
                stats.Placed++;
                if (!keptSet.Contains(sound))
                    stats.NearSound++;

                int next = i + 1 < mine.Count ? mine[i + 1].Row : rowCount;
                if (offRow is { } o && o > row && o < next && o < rowCount)
                    rows[o] = new NoteOffRow();
            }

            lines.Add(rows);
        }

        SidImportResult? best = null;
        string? lastProblem = null;
        foreach (int rowsPerPattern in new[] { 16, 32, 64 })
        {
            if (rowsPerPattern > MaxRowsPerPattern)
                break;

            var song = MidiImporter.Build(lines, instruments, grid.A - 1, rowsPerPattern);
            if (grid.Funk)
                song.FunkSpeed = grid.B - 1;
            if (MidiImporter.Problem(song) is { } problem)
            {
                lastProblem = problem;
                continue;
            }

            int size = SongSizeCalculator.Measure(song).TotalBytes;
            if (best is null || size < best.Report.TotalBytes)
            {
                best = new SidImportResult(song, new SidImportReport(
                    count, grid.A, rowCount, rowsPerPattern, song.Patterns.Count, size,
                    stats.Placed, stats.Moved, stats.Dropped, stats.Shifted,
                    sounds.Count, instruments.Count, stats.NearSound,
                    instruments.Any(i => i.Filter.Count > 0),
                    DescribeRoles(kept.Select(k => roles[k]))));
            }
        }

        return best ?? throw new SidImportException(lastProblem?.Replace("import fewer bars", "import a shorter stretch")
            ?? "The song could not be built.");
    }

    /// <summary>
    /// The most frames from <see cref="SidImportOptions.FirstFrame"/> that import - a long tune can
    /// need more song order than the player holds. Null when not even a few seconds do.
    /// </summary>
    public static int? LongestThatFits(Analysis analysis, SidImportOptions options)
    {
        bool Fits(int frames)
        {
            try
            {
                Import(analysis, options with { FrameCount = frames });
                return true;
            }
            catch (SidImportException)
            {
                return false;
            }
        }

        int most = analysis.FrameCount - Math.Clamp(options.FirstFrame, 0, analysis.FrameCount - 1);
        if (Fits(most))
            return most;

        int low = Math.Min(most, 50 * 4), high = most;
        if (!Fits(low))
            return null;

        // Seconds are fine enough; each try is a whole import.
        while (high - low > 50)
        {
            int middle = (low + high) / 2;
            if (Fits(middle))
                low = middle;
            else
                high = middle;
        }

        return low;
    }

    private sealed class Stats
    {
        public int Placed, Moved, Dropped, Shifted, NearSound;
    }

    // ------------------------------------------------------------------------------------ sounds

    /// <summary>One sound, as a note of the tune plays it - what becomes an instrument.</summary>
    private sealed class Sound
    {
        public required byte AttackDecay { get; init; }
        public required byte SustainRelease { get; init; }
        public required byte[] Controls { get; init; }
        public required NoteName[]? Notes { get; init; }
        public required ArpeggioEnding Ending { get; init; }
        public required int LoopPoint { get; init; }
        public required int PulseWidth { get; init; }
        public required int PulseSweep { get; init; }
        public required byte VibratoDelaySpeed { get; init; }
        public required byte VibratoDepth { get; init; }
        public required FilterStep[] Filter { get; init; }
        public required bool GateOffInProgram { get; init; }

        /// <summary>The next note follows legato - MagSID's NoHardRestart, which belongs to the
        /// note being left.</summary>
        public required bool NoHardRestart { get; init; }

        public int Uses { get; set; }

        /// <summary>What makes two notes the same sound: the envelope, the waveform frame by frame,
        /// the note program, whether there is vibrato, how the filter is set up, and legato. Where
        /// the pulse starts, the sweep, the vibrato's numbers and the cutoff's path are settings
        /// of the sound, taken from what most of its notes do (<see cref="Typical"/>).</summary>
        public string Key => string.Join("|",
            AttackDecay, SustainRelease, Convert.ToHexString(Controls),
            Notes is null ? "" : string.Join(",", Notes.Select(n => n.TableIndex)), Ending, LoopPoint,
            VibratoDepth != 0, NoHardRestart, GateOffInProgram,
            Filter.Length == 0 ? "" : $"{Filter[0].ModeVolume}/{Filter[0].ResonanceRouting}");

        /// <summary>One sound standing for all the notes that are it: each setting the one most of
        /// them have.</summary>
        public static Sound Typical(List<Sound> members)
        {
            static T Most<T>(IEnumerable<T> values) where T : notnull =>
                values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

            var first = members[0];
            var vibrato = Most(members.Select(m => (m.VibratoDelaySpeed, m.VibratoDepth)));
            string filterKey = Most(members.Select(m => FilterKey(m.Filter)));
            return new Sound
            {
                AttackDecay = first.AttackDecay,
                SustainRelease = first.SustainRelease,
                Controls = first.Controls,
                Notes = first.Notes,
                Ending = first.Ending,
                LoopPoint = first.LoopPoint,
                PulseWidth = Most(members.Select(m => m.PulseWidth)),
                PulseSweep = Most(members.Select(m => m.PulseSweep)),
                VibratoDelaySpeed = vibrato.VibratoDelaySpeed,
                VibratoDepth = vibrato.VibratoDepth,
                Filter = members.First(m => FilterKey(m.Filter) == filterKey).Filter,
                GateOffInProgram = first.GateOffInProgram,
                NoHardRestart = first.NoHardRestart,
                Uses = members.Count,
            };

            static string FilterKey(FilterStep[] filter) =>
                string.Join(",", filter.Select(f => $"{f.ModeVolume}/{f.ResonanceRouting}/{f.Cutoff}"));
        }

        /// <summary>How unlike two sounds are, for putting a sound that did not get an instrument
        /// on one that did: the waveform matters most, then the note program, then the envelope.</summary>
        public int Distance(Sound other)
        {
            // A plain note on an instrument whose note program plays other pitches is the worst a
            // match can do - it is another tune - so that outweighs everything.
            int distance = 0;
            if (Notes is null && other.Notes is not null)
                distance += 5000;
            else if (Notes is not null && other.Notes is null)
                distance += 300;
            else if (Notes is not null && !Notes.SequenceEqual(other.Notes!))
                distance += 2000;
            // The first waveform heard: a hard restart's test-bit frame comes before it.
            static int Heard(byte[] controls) => controls.Select(c => c & 0xF0).FirstOrDefault(w => w != 0);
            if (Heard(Controls) != Heard(other.Controls))
                distance += 1000;
            if ((Filter.Length > 0) != (other.Filter.Length > 0))
                distance += 60;
            distance += 4 * Nibbles(AttackDecay, other.AttackDecay) + 4 * Nibbles(SustainRelease, other.SustainRelease);
            distance += 2 * Math.Abs(Controls.Length - other.Controls.Length);
            distance += Math.Abs(PulseWidth - other.PulseWidth) + Math.Abs(PulseSweep - other.PulseSweep) / 8;
            distance += VibratoDepth == other.VibratoDepth ? 0 : 10;
            return distance;

            static int Nibbles(byte a, byte b) => Math.Abs((a >> 4) - (b >> 4)) + Math.Abs((a & 15) - (b & 15));
        }

        /// <summary>The same sound playing only the row's note.</summary>
        public Sound WithoutNotes() => new()
        {
            AttackDecay = AttackDecay,
            SustainRelease = SustainRelease,
            Controls = Controls,
            Notes = null,
            Ending = ArpeggioEnding.Loop,
            LoopPoint = 0,
            PulseWidth = PulseWidth,
            PulseSweep = PulseSweep,
            VibratoDelaySpeed = VibratoDelaySpeed,
            VibratoDepth = VibratoDepth,
            Filter = Filter,
            GateOffInProgram = GateOffInProgram,
            NoHardRestart = NoHardRestart,
            Uses = Uses,
        };

        public Instrument ToInstrument(int number, string name)
        {
            var instrument = new Instrument
            {
                Number = number,
                Name = name,
                AttackDecay = AttackDecay,
                SustainRelease = SustainRelease,
                Waveform = (byte)(Controls[0] | 1),
                PulseWidth = PulseWidth,
                PulseSweep = PulseSweep,
                VibratoDelaySpeed = VibratoDelaySpeed,
                VibratoDepth = VibratoDepth,
                NoHardRestart = NoHardRestart,
                ArpeggioEnding = ArpeggioEnding.Loop,
            };

            // A program that is one gated byte is the plain note an empty wavetable plays.
            if (Controls.Length > 1 || (Controls[0] & 1) == 0)
                instrument.Wavetable.AddRange(Controls);

            if (Notes is not null)
            {
                instrument.Arpeggio.AddRange(Notes);
                instrument.ArpeggioEnding = Ending;
                instrument.ArpeggioLoopPoint = LoopPoint;
            }

            instrument.Filter.AddRange(Filter);
            return instrument;
        }
    }

    /// <summary>
    /// The families whose short strike before the note moves with the note - a GoatTracker bass's
    /// first frame of saw an octave up, say. MagSID's note programs are absolute, so kept it would
    /// make one instrument for every note the bass plays; played on the row's note instead it is
    /// one. A strike on fixed notes - a kick falling from B-7 - is the same on every note, and stays.
    /// </summary>
    private static HashSet<string> RelativeTransients(IEnumerable<(Sound Sound, NoteName Root)> heard) =>
        heard.Where(h => TransientOf(h.Sound, h.Root) is not null)
            .GroupBy(h => TransientOf(h.Sound, h.Root)!)
            .Where(g => g.Select(h => h.Root).Distinct().Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet();

    /// <summary>"family|+12" - a strike of at most three frames before the row's note, as steps
    /// away from it; null for any other program.</summary>
    private static string? TransientOf(Sound sound, NoteName root) =>
        sound.Notes is { Length: <= 3 } steps && sound.Ending == ArpeggioEnding.Root
            ? $"{FamilyOf(sound)}|{string.Join(",", steps.Select(n => n.TableIndex - root.TableIndex))}"
            : null;

    private static Sound SoundOf(Analysis analysis, NoteEvent note, SidImportOptions options, int framesPerRow,
                                 byte volume, out NoteName root, Stats stats)
    {
        var frames = analysis.Voices[note.Voice];
        int length = Math.Min((note.HardRestart ?? note.End) - note.Start, ProgramFrames);

        // A gate that goes off inside the note's own first row cannot be a note-off row; it goes
        // into the wavetable at the frame it happened instead - unless it is in the row's last two
        // frames, where GoatTracker's early gate-off ends a note it was told to end on the next row.
        // Rows of four frames or fewer are too short to tell the two apart.
        int inFirstRow = Math.Max(framesPerRow - (HardRestartFrames - 1), Math.Min(framesPerRow, 4));
        bool gateOffInProgram = note.GateOff is { } off && off - note.Start < inFirstRow && off - note.Start < ProgramFrames;

        // After that the sound lasts as long as its release, or until the oscillator is held
        // silent for the next note: cut there, every strike of a drum is the same program however
        // far away the next note is.
        if (gateOffInProgram)
        {
            int gateOff = note.GateOff!.Value;
            int heard = gateOff - note.Start + ReleaseFrames(frames[gateOff].SustainRelease);
            int silence = Enumerable.Range(gateOff, Math.Max(0, note.Start + length - gateOff)).FirstOrDefault(f => Silent(frames[f].Control), -1);
            length = Math.Min(length, silence < 0 ? heard : Math.Min(heard, silence - note.Start));
        }
        var window = Enumerable.Range(note.Start, Math.Max(1, length)).Select(f => frames[f]).ToArray();

        // The pitch is read while the gate is on: in the release a player is free to set up the
        // next note's frequency already - unless its program still runs there, as a snare's
        // noise and pulse take turns after the gate. Only frames that sit on a note are steps; a
        // vibrato or a slide passes between notes, and the nearest note flickering under it is not
        // a program.
        int gated = Math.Clamp(note.SoundEnd - note.Start, 1, window.Length);
        int changes = Enumerable.Range(gated, Math.Max(0, window.Length - gated))
            .Count(f => window[f].Control != window[f - 1].Control || window[f].Frequency != window[f - 1].Frequency);
        bool runsOn = gateOffInProgram && changes >= 2;
        var pitched = window.Take(runsOn ? window.Length : gated).ToArray();
        int[] index = [.. pitched.Select(f => PitchOf(f, analysis.Tuning))];
        // A silent frame - the test bit restarting the oscillator - plays whatever comes next.
        for (int i = index.Length - 2; i >= 0; i--)
            if (Silent(pitched[i].Control))
                index[i] = index[i + 1];
        int rootIndex = RootOf(pitched, analysis.Tuning);
        int played = rootIndex;
        while (played < 0) played += 12;
        while (played >= NoteName.AllNotes.Count) played -= 12;
        if (played != rootIndex)
            stats.Shifted++;
        root = NoteName.AllNotes[played];

        int controlFrames = note.GateOff is { } o && !gateOffInProgram ? Math.Min(length, o - note.Start) : length;
        // $00 ends a wavetable in MagSID's format; the test bit with the gate off is as silent.
        var controls = Trim([.. window.Take(Math.Max(1, controlFrames)).Select(f => f.Control == 0 ? (byte)0x08 : f.Control)]);

        // Pitch steps away from the root, one a frame, become a note program. One that the gate
        // cuts off away from the root, and that is back on it in the release, ends on the root.
        int[] offsets = [.. index.Select(i => i < 0 ? 0 : i - rootIndex)];
        if (!runsOn && gated < window.Length && offsets.Length > 0 && offsets[^1] != 0
            && PitchOf(window[gated], analysis.Tuning) == rootIndex)
            offsets = [.. offsets, 0];
        var (notes, ending, loopPoint) = NoteProgram(offsets, played);

        // Pulse: where it starts, and how far it moves a frame. Read where the pulse first sounds -
        // on a hard restart's first frame the width is still the last note's.
        int pulseWidth = Instrument.DefaultPulseWidth, sweep = 0;
        if (window.Any(f => (f.Control & 0x40) != 0))
        {
            int sounds = Array.FindIndex(window, f => (f.Control & 0x48) == 0x40);
            pulseWidth = window[Math.Max(0, sounds)].PulseWidth >> 8;
            var deltas = window.Zip(window.Skip(1), (a, b) => b.PulseWidth - a.PulseWidth).Where(d => d != 0).ToList();
            if (deltas.Count > window.Length / 3)
                sweep = Math.Clamp(Median(deltas), -128, 127);
        }

        var (delaySpeed, depth) = options.Vibrato ? Vibrato(frames, note, offsets, played) : ((byte)0, (byte)0);
        var filter = options.Filter ? FilterProgram(analysis, note, length, volume) : [];

        return new Sound
        {
            AttackDecay = window[0].AttackDecay,
            SustainRelease = window[0].SustainRelease,
            Controls = controls,
            Notes = notes,
            Ending = ending,
            LoopPoint = loopPoint,
            PulseWidth = pulseWidth,
            PulseSweep = sweep,
            VibratoDelaySpeed = delaySpeed,
            VibratoDepth = depth,
            Filter = filter,
            GateOffInProgram = gateOffInProgram,
            NoHardRestart = note.LegatoNext,
        };
    }

    /// <summary>The note is the pitch it spends longest on - the root of an arpeggio, where a drum
    /// settles after its strike - and the lowest of those that tie.</summary>
    private static int RootOf(int[] index)
    {
        int root = index.Where(i => i >= 0).GroupBy(i => i)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault(-1);
        return root < 0 ? 4 * 12 : root;
    }

    private static int RootOf(Analysis analysis, NoteEvent note)
    {
        int length = Math.Max(1, Math.Min(note.SoundEnd - note.Start, ProgramFrames));
        return RootOf([.. Enumerable.Range(note.Start, length).Select(f => analysis.Voices[note.Voice][f])], analysis.Tuning);
    }

    /// <summary>The note a frame sounds, or -1 for none: silent, or between notes. A drum's
    /// strike at $FFFF is B-7 in any tuning - the table goes no higher.</summary>
    private static int PitchOf(VoiceFrame frame, double tuning) =>
        Silent(frame.Control) ? -1
        : frame.Frequency >= 0xFFF0 ? NoteName.AllNotes.Count - 1
        : OnNote(frame.Frequency, tuning);

    /// <summary>Frames a release lasts, from the SID's release times (6 ms to 24 s).</summary>
    private static int ReleaseFrames(byte sustainRelease)
    {
        int[] milliseconds = [6, 24, 48, 72, 114, 168, 204, 240, 300, 750, 1500, 2400, 3000, 9000, 15000, 24000];
        return (milliseconds[sustainRelease & 0x0F] + 19) / 20;
    }

    /// <summary>
    /// The note a sound plays, from its frames while the gate is on. The tonal frames decide it -
    /// pulse, saw, triangle - when there are any: a drum's strike on noise, or a click a frame long
    /// before the bass, is colour, not the note. Without that a bass with a one-frame click had as
    /// many frames on the click as on its note, and every bass note became an instrument of its own.
    /// </summary>
    private static int RootOf(VoiceFrame[] frames, double tuning)
    {
        int[] tonal = [.. frames.Where(f => Tonal(f.Control)).Select(f => OnNote(f.Frequency, tuning)).Where(i => i >= 0)];
        if (tonal.Length > 0)
            return RootOf(tonal);

        // A frame with the test bit on still holds the last note's frequency; it is no pitch.
        var sounding = frames.Where(f => !Silent(f.Control)).ToArray();
        if (sounding.Length == 0)
            sounding = frames;
        int[] on = [.. sounding.Select(f => OnNote(f.Frequency, tuning))];
        return RootOf(on.Any(i => i >= 0) ? on : [.. sounding.Select(f => NearestNote(f.Frequency, tuning))]);
    }

    // ------------------------------------------------------------------------------------ check

    /// <summary>
    /// How much of the tune the import kept: the share of the original's notes, in the imported
    /// stretch, that the imported song plays too - same voice, same note, within half a row of the
    /// same time. <paramref name="imported"/> is the imported song played through MagSID's own
    /// player, from its first frame. Octaves the import had to fold into C-0..B-7 count as kept.
    /// </summary>
    public static double Agreement(Analysis original, SidImportOptions options, Analysis imported)
    {
        int first = Math.Clamp(options.FirstFrame, 0, Math.Max(0, original.FrameCount - 1));
        int count = Math.Clamp(options.FrameCount, 1, original.FrameCount - first);
        int shortest = Math.Min(options.FramesPerRow, options.FunkFramesPerRow ?? options.FramesPerRow);
        int tolerance = Math.Max(1, shortest / 2);

        var want = new List<(int Voice, int Frame, int Note)>();
        var got = new List<(int Voice, int Frame, int Note)>();
        int origin = GridOf(original, first, count, Math.Clamp(options.FramesPerRow, 1, 64),
                            Math.Clamp(options.FunkFramesPerRow ?? options.FramesPerRow, 1, 64)).Origin - first;
        for (int v = 0; v < 3; v++)
        {
            want.AddRange(original.Notes[v].Where(n => n.Start >= first && n.Start < first + count)
                .Select(n => (v, n.Start - first, Fold(RootOf(original, n)))));
            got.AddRange(imported.Notes[v].Select(n => (v, n.Start + origin, Fold(RootOf(imported, n)))));
        }

        if (want.Count == 0)
            return 1;

        // The imported song starts on its first row, the one the first note is on, and MagSID's
        // player starts a note a frame or two into its row: the best shift is found.
        int best = 0;
        for (int shift = -options.FramesPerRow - 4; shift <= options.FramesPerRow + 4; shift++)
            best = Math.Max(best, Matches(want, got, shift, tolerance));
        return best / (double)want.Count;

        static int Fold(int note)
        {
            while (note < 0) note += 12;
            while (note >= NoteName.AllNotes.Count) note -= 12;
            return note;
        }
    }

    private static int Matches(List<(int Voice, int Frame, int Note)> want, List<(int Voice, int Frame, int Note)> got, int shift, int tolerance)
    {
        int matches = 0;
        for (int v = 0; v < 3; v++)
        {
            var heard = got.Where(g => g.Voice == v).Select(g => (Frame: g.Frame + shift, g.Note)).ToList();
            var used = new bool[heard.Count];
            int from = 0;
            foreach (var w in want.Where(w => w.Voice == v))
            {
                while (from < heard.Count && heard[from].Frame < w.Frame - tolerance)
                    from++;
                for (int i = from; i < heard.Count && heard[i].Frame <= w.Frame + tolerance; i++)
                {
                    if (!used[i] && heard[i].Note == w.Note)
                    {
                        used[i] = true;
                        matches++;
                        break;
                    }
                }
            }
        }

        return matches;
    }

    /// <summary>A program without the repeats at its end - the player holds the last step.</summary>
    private static byte[] Trim(byte[] steps)
    {
        int last = steps.Length - 1;
        while (last > 0 && steps[last] == steps[last - 1])
            last--;
        return steps[..(last + 1)];
    }

    /// <summary>
    /// The steps of pitch away from the root as a note program: one that comes back to the root
    /// and stays ends on ROOT, one that cycles loops from where its cycle starts, and anything
    /// else holds its last step. None at all when the pitch never leaves the root. The steps are
    /// absolute notes - MagSID's note programs are - so the same arpeggio on another root is
    /// another sound.
    /// </summary>
    private static (NoteName[]? Notes, ArpeggioEnding Ending, int LoopPoint) NoteProgram(int[] offsets, int root)
    {
        if (offsets.All(o => o == 0))
            return (null, ArpeggioEnding.Loop, 0);

        NoteName At(int offset) => NoteName.AllNotes[Math.Clamp(root + offset, 0, NoteName.AllNotes.Count - 1)];

        // A cycle, seen at least twice round - first, as a cycle through the root can stop on it
        // where the note was cut off, as a snare ringing on C-3 and D#7 in turn does. A constant
        // tail is no cycle: it is a note held.
        for (int start = 0; start <= offsets.Length / 2; start++)
        {
            for (int period = 2; period <= 16 && start + 2 * period <= offsets.Length; period++)
            {
                bool cycles = offsets.Skip(start).Take(period).Distinct().Count() > 1;
                for (int i = start + period; i < offsets.Length && cycles; i++)
                    cycles = offsets[i] == offsets[start + (i - start) % period];
                if (cycles)
                    return ([.. offsets.Take(start + period).Select(At)], ArpeggioEnding.Loop, start);
            }
        }

        // Comes back to the root at the end: a strike, then the note.
        int lastAway = Array.FindLastIndex(offsets, o => o != 0);
        if (lastAway < offsets.Length - 1)
            return ([.. offsets.Take(lastAway + 1).Select(At)], ArpeggioEnding.Root, 0);

        int lastChange = offsets.Length - 1;
        while (lastChange > 0 && offsets[lastChange] == offsets[lastChange - 1])
            lastChange--;
        return ([.. offsets.Take(lastChange + 1).Select(At)], ArpeggioEnding.Hold, 0);
    }

    /// <summary>
    /// Vibrato, if the pitch wobbles around the note: how long before it starts, how many frames
    /// it moves one way, and how far each frame - MagSID's three vibrato numbers. Looked for over
    /// the whole note, since a delayed vibrato starts well after its sound has settled.
    /// </summary>
    private static (byte DelaySpeed, byte Depth) Vibrato(VoiceFrame[] frames, NoteEvent note, int[] offsets, int root)
    {
        int end = note.SoundEnd;
        if (end - note.Start < 8 || offsets.Any(o => o != 0))
            return (0, 0);

        var deltas = new List<(int Frame, int Delta)>();
        for (int f = note.Start + 1; f < end; f++)
            deltas.Add((f - note.Start, frames[f].Frequency - frames[f - 1].Frequency));

        var moving = deltas.Where(d => d.Delta != 0).ToList();
        int turns = moving.Zip(moving.Skip(1), (a, b) => Math.Sign(a.Delta) != Math.Sign(b.Delta) ? 1 : 0).Sum();
        if (moving.Count < 4 || turns < 3)
            return (0, 0);

        // A vibrato wobbles around its note and comes back. A pitch that swings further than two
        // semitones, or ends up somewhere else, is a bend or a slide - and played as a vibrato it
        // drags the note off its pitch, as it did the bass of Viva Las Vegas.
        double pitch = NoteFrequencies[Math.Clamp(root, 0, NoteFrequencies.Length - 1)];
        int low = int.MaxValue, high = int.MinValue;
        for (int f = note.Start; f < end; f++)
        {
            low = Math.Min(low, frames[f].Frequency);
            high = Math.Max(high, frames[f].Frequency);
        }
        int drift = Math.Abs(frames[end - 1].Frequency - frames[note.Start].Frequency);
        if (high - low > pitch * 0.12 || drift > (high - low) / 2)
            return (0, 0);

        // Runs of frames moving the same way.
        var runs = new List<int>();
        int run = 1;
        for (int i = 1; i < moving.Count; i++)
        {
            if (Math.Sign(moving[i].Delta) == Math.Sign(moving[i - 1].Delta))
                run++;
            else
            {
                runs.Add(run);
                run = 1;
            }
        }

        int delay = Math.Clamp(moving[0].Frame / 4, 0, 15);
        int speed = Math.Clamp(Median(runs) - 1, 0, 15);
        int depth = Median([.. moving.Select(m => Math.Abs(m.Delta))]);
        if (depth > 255)
            return (0, 0);
        return ((byte)((delay << 4) | speed), (byte)Math.Max(1, depth));
    }

    /// <summary>
    /// The filter, if the note's voice goes through it: a setup step with the tune's mode,
    /// resonance and routing, then the cutoff frame by frame - and a setup again where the mode or
    /// the resonance changes, as a GoatTracker kick goes from band-pass to low-pass. MagSID's
    /// filter belongs to the last note that sets it up, so a sweep the tune runs across many notes
    /// starts again with each.
    /// </summary>
    private static FilterStep[] FilterProgram(Analysis analysis, NoteEvent note, int length, byte volume)
    {
        // A hard restart's first frame, with the test bit on, still has the last note's filter:
        // the note's own begins where the voice first sounds.
        var voice = analysis.Voices[note.Voice];
        int sounds = note.Start;
        while (sounds < note.Start + length - 1 && Silent(voice[sounds].Control))
            sounds++;

        var at = analysis.Global[sounds];
        if ((at.ResonanceRouting & (1 << note.Voice)) == 0)
            return [];

        (byte Mode, byte Resonance, byte Cutoff) State(int frame)
        {
            var g = analysis.Global[Math.Max(frame, sounds)];
            return ((byte)(g.ModeVolume & 0xF0), g.ResonanceRouting,
                    (byte)Math.Clamp(g.Cutoff >> 3, FilterStep.MinCutoff, FilterStep.MaxCutoff));
        }

        var states = Enumerable.Range(note.Start, Math.Max(1, length)).Select(State).ToList();
        while (states.Count > 1 && states[^1] == states[^2])
            states.RemoveAt(states.Count - 1);

        var steps = new List<FilterStep>();
        for (int i = 0; i < states.Count; i++)
        {
            var (mode, resonance, cutoff) = states[i];
            steps.Add(i == 0 || (mode, resonance) != (states[i - 1].Mode, states[i - 1].Resonance)
                ? FilterStep.Setup((byte)(mode | volume), resonance, cutoff)
                : FilterStep.Sweep(cutoff));
        }

        return [.. steps];
    }

    private static string NameFor(Sound sound, SoundRole role, List<Instrument> made)
    {
        string kind = RoleName(role, sound);
        if (sound.Filter.Length > 0)
            kind += " filter";

        int same = made.Count(i => i.Name.StartsWith(kind, StringComparison.Ordinal));
        return same == 0 ? kind : $"{kind} {same + 1}";
    }

    // ------------------------------------------------------------------------------------ pitch

    private static readonly double[] NoteFrequencies = BuildFrequencies();

    private static double[] BuildFrequencies()
    {
        var table = new double[NoteName.AllNotes.Count];
        for (int i = 0; i < table.Length; i++)
        {
            var (lo, hi) = NotePalTable.GetFreq(NoteName.AllNotes[i]);
            table[i] = lo | (hi << 8);
        }
        return table;
    }

    /// <summary>The note nearest a SID frequency, by ratio, as an index that may run past either
    /// end of C-0..B-7 - an octave out is still the right note name. -1 for silence.</summary>
    internal static int NearestNote(int frequency, double tuning = 0)
    {
        if (frequency <= 0)
            return -1;

        double semitones = 12 * Math.Log2(frequency / NoteFrequencies[0]) - tuning;
        return (int)Math.Round(semitones);
    }

    /// <summary>The note a frequency sits on, within a quarter of a semitone of the tune's own
    /// tuning - or -1 for one between notes, as a vibrato or a slide passes through.</summary>
    internal static int OnNote(int frequency, double tuning = 0)
    {
        if (frequency <= 0)
            return -1;

        double semitones = 12 * Math.Log2(frequency / NoteFrequencies[0]) - tuning;
        int nearest = (int)Math.Round(semitones);
        return Math.Abs(semitones - nearest) <= 0.25 ? nearest : -1;
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
