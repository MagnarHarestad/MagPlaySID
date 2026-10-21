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
public sealed record SidImportOptions(int FramesPerRow, int FirstFrame, int FrameCount, bool Filter = true, bool Vibrato = true);

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
    bool UsesFilter);

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
public static class SidTranscriber
{
    /// <summary>Frames of a note looked at for its sound. The player's tables are one byte a frame,
    /// and a sound that has not settled in this long is near enough to one that has.</summary>
    private const int ProgramFrames = 32;

    /// <summary>A gate-off this close before the next gate-on is the tune's hard restart, not the
    /// end of the note: MagSID's player makes its own.</summary>
    private const int HardRestartFrames = 3;

    private const int MaxRowsPerPattern = 64;

    /// <summary>What every voice does in every frame, and where its notes start - worked out once
    /// per tune, before any choice is made.</summary>
    public sealed class Analysis
    {
        internal Analysis(VoiceFrame[][] voices, GlobalFrame[] global, List<NoteEvent>[] notes)
        {
            Voices = voices;
            Global = global;
            Notes = notes;
        }

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
                foreach (int change in LegatoChanges(voices[v], start, off ?? hardRestart ?? end))
                {
                    notes[v].Add(new NoteEvent(v, from, change, null, legato, LegatoNext: true));
                    from = change;
                    legato = true;
                }

                notes[v].Add(new NoteEvent(v, from, end, off, legato, HardRestart: hardRestart));
            }
        }

        return new Analysis(voices, global, notes);
    }

    private static IEnumerable<int> LegatoChanges(VoiceFrame[] frames, int start, int end)
    {
        for (int f = start + LegatoHold; f + LegatoHold <= end; f++)
        {
            int now = NearestNote(frames[f].Frequency);
            int before = NearestNote(frames[f - 1].Frequency);
            if (now < 0 || before < 0 || now == before)
                continue;

            bool held = true;
            for (int k = 1; k < LegatoHold && held; k++)
                held = NearestNote(frames[f + k].Frequency) == now && NearestNote(frames[f - 1 - k].Frequency) == before;
            if (held)
                yield return f;
        }
    }

    /// <summary>
    /// The row grid that puts the most note starts on a row: the longest one that still puts
    /// nine in ten there, from the phase that fits best. A tune with swing (rows of 6 and 5 frames
    /// in turn) has none that fits, and gets the finest that does.
    /// </summary>
    public static int SuggestFramesPerRow(Analysis analysis, int firstFrame = 0, int? frameCount = null)
    {
        var starts = Starts(analysis, firstFrame, frameCount ?? analysis.FrameCount - firstFrame);
        if (starts.Count == 0)
            return 6;

        for (int framesPerRow = 16; framesPerRow > 1; framesPerRow--)
        {
            var (_, aligned) = BestPhase(starts, framesPerRow);
            if (framesPerRow < 4 && aligned < starts.Count * 0.9)
                continue;
            if (aligned >= starts.Count * 0.9)
                return framesPerRow;
        }

        return 1;
    }

    private static List<int> Starts(Analysis analysis, int firstFrame, int frameCount) =>
        [.. analysis.Notes.SelectMany(n => n).Select(n => n.Start)
            .Where(s => s >= firstFrame && s < firstFrame + frameCount).Select(s => s - firstFrame)];

    /// <summary>The phase of the grid that puts most starts on a row. From 4 frames a row up a start
    /// a frame either side counts as on it: a player's own hard restart moves a gate-on by a frame,
    /// depending on what played before.</summary>
    private static (int Phase, int Aligned) BestPhase(List<int> starts, int framesPerRow)
    {
        var counts = new int[framesPerRow];
        foreach (int s in starts)
            counts[s % framesPerRow]++;

        int Near(int phase) => framesPerRow < 4
            ? counts[phase]
            : counts[phase] + counts[(phase + 1) % framesPerRow] + counts[(phase + framesPerRow - 1) % framesPerRow];

        int best = Enumerable.Range(0, framesPerRow).MaxBy(p => (Near(p), counts[p]));
        return (best, Near(best));
    }

    // ------------------------------------------------------------------------------------ import

    public static SidImportResult Import(Analysis analysis, SidImportOptions options)
    {
        int framesPerRow = Math.Clamp(options.FramesPerRow, 1, 64);
        int first = Math.Clamp(options.FirstFrame, 0, Math.Max(0, analysis.FrameCount - 1));
        int count = Math.Clamp(options.FrameCount, 1, analysis.FrameCount - first);

        var notes = analysis.Notes
            .Select(list => list.Where(n => n.Start >= first && n.Start < first + count).ToList())
            .ToArray();
        if (notes.All(n => n.Count == 0))
            throw new SidImportException("No notes start in this stretch of the tune - the gates never go on.");

        var (phase, _) = BestPhase(Starts(analysis, first, count), framesPerRow);
        int origin = first + phase;
        int rowCount = Math.Max(1, (count - phase + framesPerRow - 1) / framesPerRow);
        int RowOf(int frame) => (int)Math.Floor((frame - origin) / (double)framesPerRow + 0.5);

        var stats = new Stats();
        var groups = new Dictionary<string, List<Sound>>();
        var placed = new List<(int Voice, int Row, Sound Sound, NoteName Note, int? OffRow)>();

        foreach (var voiceNotes in notes)
        {
            foreach (var note in voiceNotes)
            {
                var sound = SoundOf(analysis, note, options, framesPerRow, out var root, stats);
                if (!groups.TryGetValue(sound.Key, out var members))
                    groups[sound.Key] = members = [];
                members.Add(sound);
                var known = members[0];

                int row = Math.Max(0, RowOf(note.Start));
                if (Math.Abs(note.Start - origin - row * framesPerRow) > (framesPerRow < 4 ? 0 : 1))
                    stats.Moved++;

                int? offRow = sound.GateOffInProgram || note.GateOff is not { } off ? null : RowOf(off);
                placed.Add((note.Voice, row, known, root, offRow));
            }
        }

        // A sound's settings - where its pulse starts and how it sweeps, its vibrato, its cutoff -
        // are what most of its notes do. The sixteen sounds used most become instruments; the rest
        // play on the nearest of them.
        var sounds = groups.Values.ToDictionary(g => g[0], Sound.Typical);
        var kept = sounds.OrderByDescending(s => s.Value.Uses).Take(SongValidator.MaxInstrumentNumber).Select(s => s.Key).ToList();
        var instruments = new List<Instrument>();
        var numberOf = new Dictionary<Sound, int>();
        foreach (var sound in kept)
        {
            var instrument = sounds[sound].ToInstrument(instruments.Count + 1, NameFor(sounds[sound], instruments));
            instruments.Add(instrument);
            numberOf[sound] = instrument.Number;
        }

        int NumberFor(Sound sound)
        {
            if (numberOf.TryGetValue(sound, out int number))
                return number;
            var nearest = kept.MinBy(k => sounds[k].Distance(sounds[sound]))!;
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

            var song = MidiImporter.Build(lines, instruments, framesPerRow - 1, rowsPerPattern);
            if (MidiImporter.Problem(song) is { } problem)
            {
                lastProblem = problem;
                continue;
            }

            int size = SongSizeCalculator.Measure(song).TotalBytes;
            if (best is null || size < best.Report.TotalBytes)
            {
                best = new SidImportResult(song, new SidImportReport(
                    count, framesPerRow, rowCount, rowsPerPattern, song.Patterns.Count, size,
                    stats.Placed, stats.Moved, stats.Dropped, stats.Shifted,
                    sounds.Count, instruments.Count, stats.NearSound,
                    instruments.Any(i => i.Filter.Count > 0)));
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
            if ((Controls[0] & 0xF0) != (other.Controls[0] & 0xF0))
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

    private static Sound SoundOf(Analysis analysis, NoteEvent note, SidImportOptions options, int framesPerRow,
                                 out NoteName root, Stats stats)
    {
        var frames = analysis.Voices[note.Voice];
        int length = Math.Min((note.HardRestart ?? note.End) - note.Start, ProgramFrames);
        var window = Enumerable.Range(note.Start, Math.Max(1, length)).Select(f => frames[f]).ToArray();

        // The pitch is read while the gate is on: in the release a player is free to set up the
        // next note's frequency already. Only frames that sit on a note are steps; a vibrato or a
        // slide passes between notes, and the nearest note flickering under it is not a program.
        var pitched = window.Take(Math.Clamp(note.SoundEnd - note.Start, 1, window.Length)).ToArray();
        int[] index = [.. pitched.Select(f => OnNote(f.Frequency))];
        int rootIndex = RootOf(index.Any(i => i >= 0) ? index : [.. pitched.Select(f => NearestNote(f.Frequency))]);
        int played = rootIndex;
        while (played < 0) played += 12;
        while (played >= NoteName.AllNotes.Count) played -= 12;
        if (played != rootIndex)
            stats.Shifted++;
        root = NoteName.AllNotes[played];

        // A gate that goes off inside the note's own first row cannot be a note-off row; it goes
        // into the wavetable at the frame it happened instead.
        bool gateOffInProgram = note.GateOff is { } off && off - note.Start < framesPerRow && off - note.Start < ProgramFrames;
        int controlFrames = note.GateOff is { } o && !gateOffInProgram ? Math.Min(length, o - note.Start) : length;
        // $00 ends a wavetable in MagSID's format; the test bit with the gate off is as silent.
        var controls = Trim([.. window.Take(Math.Max(1, controlFrames)).Select(f => f.Control == 0 ? (byte)0x08 : f.Control)]);

        // Pitch steps away from the root, one a frame, become a note program.
        int[] offsets = [.. index.Select(i => i < 0 ? 0 : i - rootIndex)];
        var (notes, ending, loopPoint) = NoteProgram(offsets, played);

        // Pulse: where it starts, and how far it moves a frame.
        int pulseWidth = Instrument.DefaultPulseWidth, sweep = 0;
        if (window.Any(f => (f.Control & 0x40) != 0))
        {
            pulseWidth = window[0].PulseWidth >> 8;
            var deltas = window.Zip(window.Skip(1), (a, b) => b.PulseWidth - a.PulseWidth).Where(d => d != 0).ToList();
            if (deltas.Count > window.Length / 3)
                sweep = Math.Clamp(Median(deltas), -128, 127);
        }

        var (delaySpeed, depth) = options.Vibrato ? Vibrato(frames, note, offsets) : ((byte)0, (byte)0);
        var filter = options.Filter ? FilterProgram(analysis, note, length) : [];

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
        var frames = Enumerable.Range(note.Start, length).Select(f => analysis.Voices[note.Voice][f].Frequency).ToArray();
        int[] on = [.. frames.Select(OnNote)];
        return RootOf(on.Any(i => i >= 0) ? on : [.. frames.Select(NearestNote)]);
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
        int tolerance = Math.Max(1, options.FramesPerRow / 2);

        var want = new List<(int Voice, int Frame, int Note)>();
        var got = new List<(int Voice, int Frame, int Note)>();
        for (int v = 0; v < 3; v++)
        {
            want.AddRange(original.Notes[v].Where(n => n.Start >= first && n.Start < first + count)
                .Select(n => (v, n.Start - first, Fold(RootOf(original, n)))));
            got.AddRange(imported.Notes[v].Select(n => (v, n.Start, Fold(RootOf(imported, n)))));
        }

        if (want.Count == 0)
            return 1;

        // The imported song starts on its first row, which is the grid's phase into the stretch,
        // and MagSID's player starts a note a frame or two into its row: the best shift is found.
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

        // Comes back to the root and stays: a strike, then the note.
        int lastAway = Array.FindLastIndex(offsets, o => o != 0);
        if (lastAway < offsets.Length - 1 && offsets.Length - lastAway >= 3)
            return ([.. offsets.Take(lastAway + 1).Select(At)], ArpeggioEnding.Root, 0);

        // A cycle, seen at least twice round.
        for (int start = 0; start <= offsets.Length / 2; start++)
        {
            for (int period = 2; period <= 8 && start + 2 * period <= offsets.Length; period++)
            {
                bool cycles = true;
                for (int i = start + period; i < offsets.Length && cycles; i++)
                    cycles = offsets[i] == offsets[start + (i - start) % period];
                if (cycles)
                    return ([.. offsets.Take(start + period).Select(At)], ArpeggioEnding.Loop, start);
            }
        }

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
    private static (byte DelaySpeed, byte Depth) Vibrato(VoiceFrame[] frames, NoteEvent note, int[] offsets)
    {
        int end = note.SoundEnd;
        if (end - note.Start < 8 || offsets.Any(o => o != 0))
            return (0, 0);

        var deltas = new List<(int Frame, int Delta)>();
        for (int f = note.Start + 1; f < end; f++)
            deltas.Add((f - note.Start, frames[f].Frequency - frames[f - 1].Frequency));

        var moving = deltas.Where(d => d.Delta != 0).ToList();
        int turns = moving.Zip(moving.Skip(1), (a, b) => Math.Sign(a.Delta) != Math.Sign(b.Delta) ? 1 : 0).Sum();
        if (moving.Count < 4 || turns < 2)
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
        int depth = Math.Clamp(Median([.. moving.Select(m => Math.Abs(m.Delta))]), 1, 255);
        return ((byte)((delay << 4) | speed), (byte)depth);
    }

    /// <summary>
    /// The filter, if the note's voice goes through it: a setup step with the tune's mode, volume,
    /// resonance and routing, then the cutoff frame by frame. MagSID's filter belongs to the last
    /// note that sets it up, so a sweep the tune runs across many notes starts again with each.
    /// </summary>
    private static FilterStep[] FilterProgram(Analysis analysis, NoteEvent note, int length)
    {
        var at = analysis.Global[note.Start];
        if ((at.ResonanceRouting & (1 << note.Voice)) == 0)
            return [];

        byte Cutoff(int frame) => (byte)Math.Clamp(analysis.Global[frame].Cutoff >> 3, FilterStep.MinCutoff, FilterStep.MaxCutoff);

        var cutoffs = Trim([.. Enumerable.Range(note.Start, Math.Max(1, length)).Select(Cutoff)]);
        var steps = new List<FilterStep> { FilterStep.Setup(at.ModeVolume, at.ResonanceRouting, cutoffs[0]) };
        steps.AddRange(cutoffs.Skip(1).Select(FilterStep.Sweep));
        return [.. steps];
    }

    private static string NameFor(Sound sound, List<Instrument> made)
    {
        int waveform = sound.Controls.Select(c => c & 0xF0).GroupBy(w => w).OrderByDescending(g => g.Count()).First().Key;
        string kind = sound.Notes is not null && sound.Ending == ArpeggioEnding.Loop ? "Arp"
            : (sound.Controls[0] & 0x80) != 0 && sound.Controls.Length > 1 ? "Drum"
            : waveform switch
            {
                0x10 => "Triangle",
                0x20 => "Saw",
                0x40 => "Pulse",
                0x80 => "Noise",
                0x50 or 0x60 or 0x70 => "Pulse mix",
                0x30 => "Tri-saw",
                _ => "Sound",
            };
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
    internal static int NearestNote(int frequency)
    {
        if (frequency <= 0)
            return -1;

        double semitones = 12 * Math.Log2(frequency / NoteFrequencies[0]);
        return (int)Math.Round(semitones);
    }

    /// <summary>The note a frequency sits on, within a quarter of a semitone - or -1 for one between
    /// notes, as a vibrato or a slide passes through.</summary>
    internal static int OnNote(int frequency)
    {
        if (frequency <= 0)
            return -1;

        double semitones = 12 * Math.Log2(frequency / NoteFrequencies[0]);
        int nearest = (int)Math.Round(semitones);
        return Math.Abs(semitones - nearest) <= 0.25 ? nearest : -1;
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
