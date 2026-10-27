using MagPlay.Score;
using MagSid.Core.SidImport;

namespace MagPlay.Transcribe;

/// <summary>
/// The music a recording is heard as, as a score: each voice's notes - a gate going on starts
/// one, and it lasts until the next - on the tune's own note table, each played by an instrument
/// that says what the voice does call by call while the note sounds: its envelope, its wave
/// program - control byte and pitch, the pitch in semitones from the note wherever it is a note -
/// and its pulse program, with the tune's hard restart before the next note taken out as the
/// instrument's. Notes the tune plays alike share an instrument. The filter and the volume are
/// a program of their own, running from the first call: its cutoff in sweeps of an even step.
///
/// The rows are on the tune's grid - Import SID's, of the tune's speed or a funk pair - a note
/// starting a few calls into its row delayed by as many, and a row two notes start in split into
/// rows of their own lengths. They are cut into patterns of as many rows as make the fewest rows
/// and order entries in all: a stretch that plays again is its pattern again, transposed if
/// its notes are. A 2SID or 3SID tune's other chips are heard the same way: three voices more
/// each, and a filter program each.
///
/// Whatever this gets wrong the transcriber writes raw, so nothing here has to be right - only
/// right more often, which the raw layer's share measures.
/// </summary>
public static class Music
{
    /// <summary>Calls before a note at most that a hard restart is looked for in.</summary>
    private const int LongestHardRestart = 4;

    /// <summary>The score's music for <paramref name="recording"/>, without timing or raw layer -
    /// the transcriber adds those; null when the tune has too few calls to hear notes in.</summary>
    public static MagPlay.Score.Score? Of(Recording recording)
    {
        if (recording.Calls.Count < 2)
            return null;

        var frames = Frames(recording, out var rises);
        var table = NoteTable(frames);
        var score = new MagPlay.Score.Score { Machine = recording.Machine, NoteTable = table, Chips = recording.Chips };
        var sounds = new List<Sound>();
        var grid = Grid.Of(rises);
        score.Tempo = grid.A;
        score.FunkTempo = grid.Funk ? grid.B : null;
        var voices = new List<(int GridRow, Row Row)>?[frames.Length];
        for (int v = 0; v < frames.Length; v++)
            voices[v] = Voice(score, frames[v], rises[v], sounds, grid);
        Arrange(score, voices, PatternLength(voices));

        foreach (var sound in sounds)
            score.Instruments.Add(sound.ToInstrument(score));
        for (int chip = 0; chip < recording.Chips; chip++)
            Filter(score, chip, Globals(recording, chip));
        return score;
    }

    /// <summary>A voice's registers after a call.</summary>
    internal readonly record struct Frame(int Frequency, int Pulse, int Control, int AttackDecay, int SustainRelease)
    {
        public bool Gate => (Control & 1) != 0;

        /// <summary>A waveform with a pitch: pulse, saw or triangle, without noise or the test bit.</summary>
        public bool Tonal => (Control & 0x70) != 0 && (Control & 0x88) == 0;
    }

    /// <summary>Each voice's registers after each call, from the calls' own writes, and the calls
    /// in which its gate went on - from off, or off and on again within the call.</summary>
    internal static Frame[][] Frames(Recording recording, out List<int>[] rises)
    {
        var registers = new int[0x20 * recording.Chips];
        int calls = recording.Calls.Count;
        int count = 3 * recording.Chips;
        var frames = new Frame[count][];
        rises = new List<int>[count];
        for (int v = 0; v < count; v++)
        {
            frames[v] = new Frame[calls];
            rises[v] = [];
        }

        for (int c = 0; c < calls; c++)
        {
            var rose = new bool[count];
            foreach (var write in recording.CallWrites[c])
            {
                int r = write.Register, chip = r >> 5, own = r & 0x1F;
                if (own < 0x15 && own % 7 == 4 && (write.Value & 1) != 0 && (registers[r] & 1) == 0)
                    rose[chip * 3 + own / 7] = true;
                registers[r] = write.Value;
            }

            for (int v = 0; v < count; v++)
            {
                int b = MagPlay.Score.Score.RegisterOf(v, 0);
                frames[v][c] = new Frame(registers[b] | (registers[b + 1] << 8),
                                         registers[b + 2] | ((registers[b + 3] & 0x0F) << 8),
                                         registers[b + 4], registers[b + 5], registers[b + 6]);
                if (rose[v])
                    rises[v].Add(c);
            }
        }

        return frames;
    }

    /// <summary>The filter and volume after a call: cutoff, resonance and routing, mode and volume.</summary>
    internal readonly record struct Global(int Cutoff, int Control, int ModeVolume);

    /// <summary>Each call's filter and volume on <paramref name="chip"/>.</summary>
    internal static Global[] Globals(Recording recording, int chip = 0)
    {
        var registers = new int[0x19];
        var globals = new Global[recording.Calls.Count];
        for (int c = 0; c < globals.Length; c++)
        {
            foreach (var write in recording.CallWrites[c])
            {
                if (write.Register >> 5 == chip)
                    registers[write.Register & 0x1F] = write.Value;
            }
            globals[c] = new Global((registers[0x15] & 7) | (registers[0x16] << 3), registers[0x17], registers[0x18]);
        }
        return globals;
    }

    /// <summary>
    /// The filter as a program from the first call: a step for each run of calls the cutoff moves
    /// by the same amount in, with the routing, mode and volume unchanged - a sweep is one step,
    /// a held cutoff one more.
    /// </summary>
    private static void Filter(MagPlay.Score.Score score, int chip, Global[] globals)
    {
        if (globals.All(g => g == default))
            return;

        score.FilterPrograms[chip] = score.FilterTable.Count;
        int volume = 0;
        for (int c = 0; c < globals.Length;)
        {
            var g = globals[c];
            int add = 0, run = 1;
            bool Same(Global other) => other.Control == g.Control && other.ModeVolume == g.ModeVolume;
            if (c + 1 < globals.Length && Same(globals[c + 1]))
            {
                add = ((globals[c + 1].Cutoff - g.Cutoff + 0x400) & 0x7FF) - 0x400;
                while (c + run < globals.Length && Same(globals[c + run])
                       && ((globals[c + run].Cutoff - globals[c + run - 1].Cutoff) & 0x7FF) == (add & 0x7FF))
                    run++;
            }

            if ((g.ModeVolume & 0x0F) != volume)
                score.FilterTable.Add(new EffectStep(new MasterVolume(g.ModeVolume & 0x0F)));
            volume = g.ModeVolume & 0x0F;
            score.FilterTable.Add(new FilterStep(g.Cutoff, g.Control, g.ModeVolume & 0xF0, add, run));
            c += run;
        }
        score.FilterTable.Add(new JumpStep(-1));
    }

    // ------------------------------------------------------------------ the note table

    /// <summary>
    /// The tune's own frequency for each note: the frequency it holds most often near each
    /// semitone, once the whole tune's tuning - how far it sits from MagSID's table - is known,
    /// and MagSID's table shifted by that tuning for the notes it never holds. Players differ by
    /// a fraction of a semitone; a note's frequency only comes out exactly from the tune's table.
    /// </summary>
    internal static int[] NoteTable(Frame[][] frames)
    {
        var standard = NoteTables.MagSidPal();
        var held = new List<int>();
        foreach (var voice in frames)
        {
            for (int c = 1; c < voice.Length; c++)
            {
                if (voice[c].Gate && voice[c].Tonal && voice[c].Frequency >= 0x100 && voice[c].Frequency == voice[c - 1].Frequency)
                    held.Add(voice[c].Frequency);
            }
        }

        double tuning = Tuning(held, standard);
        var table = new int[standard.Length];
        for (int n = 0; n < table.Length; n++)
            table[n] = (int)Math.Min(0xFFFF, Math.Round(standard[n] * Math.Pow(2, tuning / 12)));

        foreach (var group in held.GroupBy(f => Nearest(f, standard, tuning)).Where(g => g.Key >= 0))
            table[group.Key] = group.GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

        // Each note above the one below, so a frequency names one note at most.
        for (int n = 1; n < table.Length; n++)
            table[n] = Math.Min(0xFFFF, Math.Max(table[n], table[n - 1] + 1));
        return table;
    }

    /// <summary>The semitones the held frequencies sit from <paramref name="standard"/>, -0.5 to 0.5:
    /// their circular mean, so notes either side of a half semitone agree.</summary>
    private static double Tuning(List<int> held, int[] standard)
    {
        double sin = 0, cos = 0;
        foreach (int f in held)
        {
            double semitones = 12 * Math.Log2(f / (double)standard[0]);
            double angle = 2 * Math.PI * (semitones - Math.Round(semitones));
            sin += Math.Sin(angle);
            cos += Math.Cos(angle);
        }
        return held.Count < 16 || Math.Sqrt(sin * sin + cos * cos) < held.Count * 0.5 ? 0 : Math.Atan2(sin, cos) / (2 * Math.PI);
    }

    private static int Nearest(int frequency, int[] standard, double tuning)
    {
        if (frequency <= 0)
            return -1;
        int n = (int)Math.Round(12 * Math.Log2(frequency / (double)standard[0]) - tuning);
        return n >= 0 && n < standard.Length ? n : -1;
    }

    // ------------------------------------------------------------------ the grid

    /// <summary>
    /// Where the rows fall: from <see cref="Origin"/>, rows of <see cref="A"/> calls - and with funk
    /// tempo of A and <see cref="B"/> in turn - and before it, a row of its own. Rows of the tempo
    /// are left to it; with funk tempo every row says its length, since a row split for two notes
    /// would put the turn of A and B out of step.
    /// </summary>
    /// <param name="Shifts">Calls where a straight grid starts again - the music moved by a call or
    /// a few, a bar of another length, and went on in rows of the same length from there.</param>
    internal sealed record Grid(int A, int B, int Origin, IReadOnlyList<int>? Shifts = null)
    {
        /// <summary>Gaps between note starts that have to be whole rows for a row length to be the
        /// tune's, when no one phase puts nine notes in ten on a row.</summary>
        private const double GapsOnRows = 0.9;

        /// <summary>Notes after a note off the grid that have to be on the phase it starts for the
        /// grid to start again there; fewer, and it is a note delayed.</summary>
        private const int ShiftHolds = 3;

        public bool Funk => A != B;

        private int Period => Funk ? A + B : A;

        public int CallOf(int row) => Origin + row / (Funk ? 2 : 1) * Period + (Funk && (row & 1) == 1 ? A : 0);

        /// <summary>The grid Import SID fits to the notes' starts, on the phase that delays them
        /// least - a player that starts some notes a call late, or early, has them delayed by one,
        /// not by a row less one - and its first row on or before the first note.</summary>
        public static Grid Of(List<int>[] rises)
        {
            var starts = rises.SelectMany(r => r).Order().ToList();
            var (a, funk) = SidTranscriber.SuggestGrid(starts);
            int b = funk ?? a;
            if (starts.Count == 0)
                return new Grid(a, b, 0);
            if (a == 1 && funk is null && Shifting(starts.Distinct().ToList()) is { } shifting)
                return shifting;

            var grid = Enumerable.Range(0, a + (funk is null ? 0 : b))
                .SelectMany(phase => funk is null ? [new Grid(a, b, phase)] : new[] { new Grid(a, b, phase), new Grid(b, a, phase) })
                .MinBy(g => g.Delays(starts))!;
            while (grid.Origin + grid.Period <= starts[0])
                grid = grid with { Origin = grid.Origin + grid.Period };
            return grid;
        }

        /// <summary>
        /// A grid for a tune no one phase fits: the longest row length that nine gaps between note
        /// starts in ten are whole rows of, starting again wherever the notes move off it and stay
        /// off - the phase a tune is in moving by a call at a bar of another length. Null when no
        /// row length is.
        /// </summary>
        private static Grid? Shifting(List<int> starts)
        {
            if (starts.Count < 2)
                return null;
            var gaps = starts.Zip(starts.Skip(1), (x, y) => y - x).ToList();
            int length = Enumerable.Range(2, 15).Reverse().FirstOrDefault(a => gaps.Count(g => g % a == 0) >= gaps.Count * GapsOnRows);
            if (length == 0)
                return null;

            var shifts = new List<int>();
            int phase = starts[0];
            for (int i = 1; i < starts.Count; i++)
            {
                int start = starts[i];
                if ((start - phase) % length == 0)
                    continue;
                var after = starts.Skip(i + 1).Take(ShiftHolds).ToList();
                if (after.Count == ShiftHolds && after.All(t => (t - start) % length == 0))
                {
                    shifts.Add(start);
                    phase = start;
                }
            }
            return new Grid(length, length, starts[0], shifts);
        }

        /// <summary>The calls <paramref name="starts"/> are into their rows, all told.</summary>
        private long Delays(List<int> starts)
        {
            long total = 0;
            foreach (int start in starts)
            {
                int from = Origin + (int)Math.Floor((start - Origin) / (double)Period) * Period;
                int into = start - from;
                total += Funk && into >= A ? into - A : into;
            }
            return total;
        }

        /// <summary>A voice's notes - each its call and its row - laid on the grid up to
        /// <paramref name="end"/>: held rows between them, a note's delay into its row as an effect.
        /// Each row comes with the grid's row it is in, -1 for the one before the first.</summary>
        public List<(int GridRow, Row Row)> Lay(List<(int Start, Row Row)> notes, int end)
        {
            var rows = new List<(int GridRow, Row Row)>();
            int next = 0, gridRow = -1;
            void Region(int from, int to, bool own)
            {
                var inside = new List<(int Start, Row Row)>();
                while (next < notes.Count && notes[next].Start < to)
                    inside.Add(notes[next++]);
                if (inside.Count == 0)
                {
                    rows.Add((gridRow, own ? Row.Hold with { Calls = to - from } : Row.Hold));
                    return;
                }

                // Two notes or more: a row from each to the next, the first from the region's start.
                for (int i = 0; i < inside.Count; i++)
                {
                    int rowFrom = i == 0 ? from : inside[i].Start;
                    int rowTo = i + 1 < inside.Count ? inside[i + 1].Start : to;
                    int delay = inside[i].Start - rowFrom;
                    var row = delay > 0 ? inside[i].Row with { Effects = [.. inside[i].Row.Effects, new NoteDelay(delay)] } : inside[i].Row;
                    rows.Add((gridRow, own || inside.Count > 1 ? row with { Calls = rowTo - rowFrom } : row));
                }
            }

            if (Origin > 0)
                Region(0, Origin, own: true);
            if (Shifts is not { Count: > 0 } shifts)
            {
                for (gridRow = 0; CallOf(gridRow) < end; gridRow++)
                    Region(CallOf(gridRow), CallOf(gridRow + 1), own: Funk);
                return rows;
            }

            // Rows of A from the origin; where the grid starts again, the row before it ends there.
            int at = Origin, shift = 0;
            for (gridRow = 0; at < end; gridRow++)
            {
                while (shift < shifts.Count && shifts[shift] <= at)
                    shift++;
                int to = at + A;
                bool cut = shift < shifts.Count && shifts[shift] < to;
                if (cut)
                    to = shifts[shift];
                Region(at, to, own: cut);
                at = to;
            }
            return rows;
        }
    }

    // ------------------------------------------------------------------ patterns

    /// <summary>Grid rows a pattern may have.</summary>
    private static readonly int[] PatternLengths = [4, 8, 16, 32, 64];

    /// <summary>The pattern length that makes the fewest rows and order entries; the longer of
    /// two that make as few.</summary>
    private static int PatternLength(List<(int GridRow, Row Row)>?[] voices) =>
        PatternLengths.OrderBy(length => Arrange(null, voices, length)).ThenByDescending(length => length).First();

    /// <summary>
    /// Each voice's rows cut into patterns of <paramref name="length"/> grid rows - the row before
    /// the grid's first a pattern of its own - and each stretch the pattern already made of it, or
    /// of it transposed, or a new one. Into <paramref name="score"/>, or, without one, only counted:
    /// the rows of the patterns and the order entries, all told.
    /// </summary>
    private static int Arrange(MagPlay.Score.Score? score, List<(int GridRow, Row Row)>?[] voices, int length)
    {
        var made = new Dictionary<string, (int Number, int Base)>();
        int cost = 0;
        for (int v = 0; v < voices.Length; v++)
        {
            if (voices[v] is not { } rows)
                continue;

            var order = new List<OrderEntry>();
            foreach (var stretch in rows.GroupBy(r => r.GridRow < 0 ? -1 : r.GridRow / length).Select(g => g.Select(r => r.Row).ToList()))
            {
                int based = stretch.FirstOrDefault(r => r.Note.Kind == RowNoteKind.Note)?.Note.Note ?? 0;
                string shape = string.Join("|", stretch.Select(r => Shape(r, based)));
                if (!made.TryGetValue(shape, out var pattern))
                {
                    pattern = (made.Count + 1, based);
                    made[shape] = pattern;
                    cost += stretch.Count;
                    if (score is not null)
                    {
                        var added = new Pattern { Number = pattern.Number };
                        added.Rows.AddRange(stretch);
                        score.Patterns.Add(added);
                    }
                }

                var entry = new OrderEntry(pattern.Number, based - pattern.Base);
                if (order.Count > 0 && order[^1] with { Repeat = 1 } == entry)
                    order[^1] = order[^1] with { Repeat = order[^1].Repeat + 1 };
                else
                    order.Add(entry);
            }

            cost += order.Count;
            if (score is not null)
            {
                score.Voices[v].Order.AddRange(order);
                score.Voices[v].LoopTo = -1;
            }
        }
        return cost;
    }

    /// <summary>A row as it plays, its note from <paramref name="based"/>.</summary>
    private static string Shape(Row row, int based) =>
        $"{row.Note.Kind}{(row.Note.Kind == RowNoteKind.Note ? row.Note.Note - based : 0)}/{row.Instrument}/{row.Calls}/{string.Join(",", row.Effects)}";

    // ------------------------------------------------------------------ notes

    /// <summary>A note of a voice: the calls it starts and ends on, the calls of its hard restart,
    /// whether its first call keeps the voice as it was, the calls its pulse program runs for
    /// (-1 when it leaves the one before it running), and the width its row sets, if any.</summary>
    private readonly record struct NoteSpan(int Start, int End, int Tail, bool KeepsFirstCall, int PulseCalls, int? Width = null)
    {
        /// <summary>Where its wave and pulse programs start.</summary>
        public int Program => Start + (KeepsFirstCall ? 1 : 0);
    }

    /// <summary>One voice's notes as rows on the grid, each on the first instrument that plays it
    /// as the tune does, or a new one that does; null for a voice without notes.</summary>
    private static List<(int GridRow, Row Row)>? Voice(MagPlay.Score.Score score, Frame[] frames, List<int> rises, List<Sound> sounds, Grid grid)
    {
        if (rises.Count == 0)
            return null;


        var spans = new NoteSpan[rises.Count];
        for (int i = 0; i < rises.Count; i++)
        {
            int start = rises[i], end = i + 1 < rises.Count ? rises[i + 1] : frames.Length;
            int tail = i + 1 < rises.Count ? Tail(frames, start, end) : 0;
            bool keeps = start > 0 && end - tail - start >= 2
                         && frames[start].Frequency == frames[start - 1].Frequency && frames[start].Pulse == frames[start - 1].Pulse;
            spans[i] = new NoteSpan(start, end, tail, keeps, 0);
        }

        // A note restarts the pulse unless its program's first calls carry on the sweep before
        // it; one that restarts it runs its program until the next that does.
        var restarts = Enumerable.Range(0, spans.Length)
            .Where(i => i == 0 || !CarriesPulseOn(frames, spans[i].Program, spans[i].End)).ToList();
        // A width held all the while needs no program, once the one before has stopped moving:
        // the row sets it, if it is another.
        bool held = true;
        for (int r = 0; r < restarts.Count; r++)
        {
            var span = spans[restarts[r]];
            int until = r + 1 < restarts.Count ? spans[restarts[r + 1]].Program : frames.Length;
            int width = frames[span.Program].Pulse, before = span.Program > 0 ? frames[span.Program - 1].Pulse : 0;
            bool constant = Enumerable.Range(span.Program, until - span.Program).All(c => frames[c].Pulse == width);
            if (held && constant && (!span.KeepsFirstCall || width == before))
            {
                spans[restarts[r]] = span with { PulseCalls = -1, Width = width != before ? width : null };
                restarts.RemoveAt(r--);
                continue;
            }

            spans[restarts[r]] = span with { PulseCalls = until - span.Program };
            held = until - span.Program >= 2 && frames[until - 1].Pulse == frames[until - 2].Pulse;
        }
        for (int i = 0; i < spans.Length; i++)
        {
            if (restarts.BinarySearch(i) < 0 && spans[i].PulseCalls >= 0)
                spans[i] = spans[i] with { PulseCalls = -1 };
        }

        // Longest first, so an instrument is made from the most of its program there is.
        var rows = new Row[spans.Length];
        foreach (int i in Enumerable.Range(0, spans.Length).OrderByDescending(i => spans[i].End - spans[i].Tail - spans[i].Start + Math.Max(0, spans[i].PulseCalls)))
        {
            var heard = Sound.Hear(score.NoteTable, frames, spans[i]);
            int number = sounds.FindIndex(s => s.Plays(heard));
            if (number < 0)
            {
                sounds.Add(heard);
                number = sounds.Count - 1;
            }
            else
            {
                sounds[number].Join(heard);
            }
            // A note sounding with another envelope than its instrument's sets it on its row.
            var sound = sounds[number];
            var effects = new List<Effect>();
            if (heard.AttackDecay != sound.AttackDecay)
                effects.Add(new SetAttackDecay(heard.AttackDecay));
            if (heard.SustainRelease != sound.SustainRelease)
                effects.Add(new SetSustainRelease(heard.SustainRelease));
            if (spans[i].Width is { } set)
                effects.Add(new SetPulse(set));
            rows[i] = Row.Of(heard.Note, number + 1, [.. effects]);
        }

        return grid.Lay([.. spans.Select((span, i) => (span.Start, rows[i]))], frames.Length);
    }

    /// <summary>Calls a sweep has to go on through for a note to be taken to leave it running.</summary>
    private const int SweepCalls = 3;

    /// <summary>The pulse width on the calls from <paramref name="call"/> is where the sweep before
    /// was going: moved by as much as on the call before, or held.</summary>
    private static bool CarriesPulseOn(Frame[] frames, int call, int end)
    {
        if (call < 2)
            return false;
        for (int c = call; c < Math.Min(end, call + SweepCalls); c++)
        {
            if (frames[c].Pulse != ((2 * frames[c - 1].Pulse - frames[c - 2].Pulse) & 0xFFF))
                return false;
        }
        return true;
    }

    /// <summary>The calls at the end of a note, before the next, that the gate is off for - the
    /// tune's hard restart, when there are few enough of them.</summary>
    private static int Tail(Frame[] frames, int start, int end)
    {
        int tail = 0;
        while (tail < LongestHardRestart && end - 1 - tail > start && !frames[end - 1 - tail].Gate)
            tail++;
        return tail < LongestHardRestart && end - 1 - tail > start ? tail : 0;
    }

    /// <summary>A call of a wave program: the control byte, the envelope where it changes - null
    /// where it is as on the call before - and every way of saying the pitch that fits each note
    /// the program plays.</summary>
    private sealed record WaveCall(int Control, int? AttackDecay, int? SustainRelease, HashSet<Pitch> Pitches);

    /// <summary>A call of a pulse program: the width set outright, or moved by an amount.</summary>
    private readonly record struct PulseCall(bool Set, int Value);

    /// <summary>
    /// What a voice does while a note sounds, call by call - its control byte and pitch, its
    /// envelope, its pulse width - and how it is quietened before the next note. Made from one
    /// note; it plays another when it does the same for as long as that one lasts, its programs
    /// holding their last step past their end, a pulse sweep going on.
    ///
    /// A pitch can be said several ways: in semitones from the row's note, as a note of its own,
    /// as a frequency added to the note's, or as a frequency outright; a pulse width set, or moved.
    /// An instrument keeps, call by call, the ways that fit every note it plays, and is written in
    /// the first of them - a drum's noise on one note whatever the row's, an arpeggio in semitones
    /// from it, a sweep moving on from wherever the width is.
    /// </summary>
    private sealed class Sound
    {
        public required int Note { get; init; }

        /// <summary>The control byte of a first call that keeps the voice as it was; null for none.</summary>
        public required int? First { get; init; }

        public required int AttackDecay { get; init; }
        public required int SustainRelease { get; init; }
        public required List<WaveCall> Wave { get; init; }

        /// <summary>The pulse from the program's start until the next note that restarts it; null
        /// for a note that leaves the pulse program before it running.</summary>
        public required List<HashSet<PulseCall>>? Pulse { get; init; }

        public required int Tail { get; init; }

        /// <summary>The envelope the hard restart sets, AD high and SR low; -1 for none - only the
        /// gate goes off.</summary>
        public required int TailEnvelope { get; init; }

        public static Sound Hear(int[] table, Frame[] frames, NoteSpan span)
        {
            int sounding = span.End - span.Tail;
            int note = BaseNote(table, frames, span.Program, sounding);
            var wave = new List<WaveCall>();
            var was = frames[span.Start];
            for (int c = span.Program; c < sounding; c++)
            {
                var f = frames[c];
                wave.Add(new WaveCall(f.Control, f.AttackDecay != was.AttackDecay ? f.AttackDecay : null,
                                      f.SustainRelease != was.SustainRelease ? f.SustainRelease : null, Pitches(table, note, f.Frequency)));
                was = f;
            }

            List<HashSet<PulseCall>>? pulse = null;
            if (span.PulseCalls >= 0)
            {
                pulse = [];
                int before = span.Program > 0 ? frames[span.Program - 1].Pulse : 0;
                for (int c = span.Program; c < span.Program + span.PulseCalls; c++)
                {
                    pulse.Add([new PulseCall(true, frames[c].Pulse), new PulseCall(false, Delta(before, frames[c].Pulse))]);
                    before = frames[c].Pulse;
                }
            }

            var (quiet, sounded) = span.Tail > 0 ? (frames[sounding], frames[sounding - 1]) : (default, default);
            return new Sound
            {
                Note = note,
                First = span.KeepsFirstCall ? frames[span.Start].Control : null,
                AttackDecay = frames[span.Start].AttackDecay,
                SustainRelease = frames[span.Start].SustainRelease,
                Wave = wave, Pulse = pulse, Tail = span.Tail,
                TailEnvelope = quiet.AttackDecay == sounded.AttackDecay && quiet.SustainRelease == sounded.SustainRelease
                    ? -1 : (quiet.AttackDecay << 8) | quiet.SustainRelease,
            };
        }

        /// <summary>The note a note is: its first pitch that is one of the table's.</summary>
        private static int BaseNote(int[] table, Frame[] frames, int start, int end)
        {
            for (int c = start; c < end; c++)
            {
                if (frames[c].Tonal && Array.BinarySearch(table, frames[c].Frequency) is >= 0 and var n)
                    return n;
            }
            for (int c = start; c < end; c++)
            {
                if (Array.BinarySearch(table, frames[c].Frequency) is >= 0 and var n)
                    return n;
            }

            int nearest = Array.BinarySearch(table, frames[start].Frequency);
            return Math.Clamp(nearest < 0 ? ~nearest : nearest, 0, table.Length - 1);
        }

        private static HashSet<Pitch> Pitches(int[] table, int note, int frequency)
        {
            var pitches = new HashSet<Pitch>();
            if (Array.BinarySearch(table, frequency) is >= 0 and var n)
            {
                pitches.Add(Pitch.Relative(n - note));
                pitches.Add(Pitch.Absolute(n));
            }
            pitches.Add(Pitch.Offset(frequency - table[note]));
            pitches.Add(Pitch.Frequency(frequency));
            return pitches;
        }

        /// <summary>Plays <paramref name="other"/>'s note as it sounds: the same start and
        /// quietening, and for as many calls as it lasts the same as this does.</summary>
        public bool Plays(Sound other)
        {
            if (other.First != First || other.Tail != Tail || other.TailEnvelope != TailEnvelope)
                return false;
            if ((other.Pulse is null) != (Pulse is null))
                return false;
            if (Pulse is { } pulse && !Fits(pulse, other.Pulse!))
                return false;

            for (int c = 0; c < other.Wave.Count; c++)
            {
                var mine = Wave[Math.Min(c, Wave.Count - 1)];
                var theirs = other.Wave[c];
                if (mine.Control != theirs.Control || mine.AttackDecay != theirs.AttackDecay || mine.SustainRelease != theirs.SustainRelease)
                    return false;
            }
            return Fits([.. Wave.Select(w => w.Pitches)], [.. other.Wave.Select(w => w.Pitches)]);
        }

        /// <summary>Some way of saying each call fits both: call by call while <paramref name="mine"/>
        /// lasts, and past its end the one its last call holds on with.</summary>
        private static bool Fits<T>(List<HashSet<T>> mine, List<HashSet<T>> theirs)
        {
            HashSet<T>? held = null;
            for (int c = 0; c < theirs.Count; c++)
            {
                if (c < mine.Count - 1)
                {
                    if (!mine[c].Overlaps(theirs[c]))
                        return false;
                    continue;
                }

                held ??= [.. mine[^1]];
                held.IntersectWith(theirs[c]);
                if (held.Count == 0)
                    return false;
            }
            return true;
        }

        /// <summary>Takes <paramref name="other"/>'s note on: only the ways of saying each call that
        /// fit it too are kept.</summary>
        public void Join(Sound other)
        {
            for (int c = 0; c < other.Wave.Count; c++)
                Wave[Math.Min(c, Wave.Count - 1)].Pitches.IntersectWith(other.Wave[c].Pitches);
            if (Pulse is { } pulse)
            {
                for (int c = 0; c < other.Pulse!.Count; c++)
                    pulse[Math.Min(c, pulse.Count - 1)].IntersectWith(other.Pulse[c]);
            }
        }

        private static int Delta(int from, int to) => ((to - from + 0x800) & 0xFFF) - 0x800;

        /// <summary>The way of saying a pitch an instrument is written in, of those that fit.</summary>
        private static Pitch Chosen(HashSet<Pitch> pitches) =>
            pitches.OrderBy(p => p.Kind switch { PitchKind.Relative => 0, PitchKind.Absolute => 1, PitchKind.Offset => 2, _ => 3 })
                   .ThenBy(p => Math.Abs(p.Value)).First();

        /// <summary>The instrument: its envelope, quietening and programs, its programs added to
        /// the score's tables.</summary>
        public Instrument ToInstrument(MagPlay.Score.Score score)
        {
            var instrument = new Instrument
            {
                Number = score.Instruments.Count + 1,
                AttackDecay = AttackDecay,
                SustainRelease = SustainRelease,
                FirstControl = First,
                GateTimer = Tail,
                HardRestart = Tail == 0 ? HardRestartKind.None : TailEnvelope < 0 ? HardRestartKind.GateOff : HardRestartKind.Envelope,
                HardRestartEnvelope = Math.Max(0, TailEnvelope),
                WaveProgram = score.WaveTable.Count,
            };

            var steps = Wave.Select(w => (w.Control, Pitch: Chosen(w.Pitches), w.AttackDecay, w.SustainRelease)).ToList();
            for (int c = 0; c < steps.Count;)
            {
                var (control, pitch, attackDecay, sustainRelease) = steps[c];
                if (attackDecay is { } ad)
                    score.WaveTable.Add(new EffectStep(new SetAttackDecay(ad)));
                if (sustainRelease is { } sr)
                    score.WaveTable.Add(new EffectStep(new SetSustainRelease(sr)));
                int run = 1;
                while (c + run < steps.Count && steps[c + run] == (control, pitch, null, null))
                    run++;
                score.WaveTable.Add(new WaveStep(control, pitch, run));
                c += run;
            }
            score.WaveTable.Add(new JumpStep(-1));

            if (Pulse is { } pulse)
            {
                instrument.PulseProgram = score.PulseTable.Count;
                Pulses(score, pulse);
            }
            return instrument;
        }

        /// <summary>A pulse program: a width set and each run of calls it moves by the same amount
        /// after - the last run on and on - or a run of moves from wherever the width is.</summary>
        private static void Pulses(MagPlay.Score.Score score, List<HashSet<PulseCall>> pulse)
        {
            // A width set where the program starts, if it can be; after that, moves where they can be.
            var calls = pulse.Select((ways, c) => ways.Where(w => w.Set == (c == 0)).DefaultIfEmpty(ways.First()).First()).ToList();

            const int OnAndOn = int.MaxValue / 2;
            for (int c = 0; c < calls.Count;)
            {
                int? width = calls[c].Set ? calls[c].Value : null;
                int next = width is null ? c : c + 1;
                int add = next < calls.Count && !calls[next].Set ? calls[next].Value : 0;
                int end = next;
                while (end < calls.Count && !calls[end].Set && calls[end].Value == add)
                    end++;
                end = Math.Max(end, c + 1);
                score.PulseTable.Add(new PulseStep(width, add, end >= calls.Count && add != 0 ? OnAndOn : end - c));
                c = end;
            }
            score.PulseTable.Add(new JumpStep(-1));
        }
    }
}
