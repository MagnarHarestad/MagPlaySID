using MagPlay.Score;

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
        var score = new MagPlay.Score.Score { Machine = recording.Machine, NoteTable = table };
        var sounds = new List<Sound>();
        for (int v = 0; v < 3; v++)
            Voice(score, v, frames[v], rises[v], sounds);

        foreach (var sound in sounds)
            score.Instruments.Add(sound.ToInstrument(score));
        Filter(score, Globals(recording));
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
        var registers = new int[0x19];
        int calls = recording.Calls.Count;
        var frames = new Frame[3][];
        rises = [[], [], []];
        for (int v = 0; v < 3; v++)
            frames[v] = new Frame[calls];

        for (int c = 0; c < calls; c++)
        {
            var rose = new bool[3];
            foreach (var write in recording.CallWrites[c])
            {
                int r = write.Register;
                if (r < 0x15 && r % 7 == 4 && (write.Value & 1) != 0 && (registers[r] & 1) == 0)
                    rose[r / 7] = true;
                registers[r] = write.Value;
            }

            for (int v = 0; v < 3; v++)
            {
                int b = v * 7;
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

    internal static Global[] Globals(Recording recording)
    {
        var registers = new int[0x19];
        var globals = new Global[recording.Calls.Count];
        for (int c = 0; c < globals.Length; c++)
        {
            foreach (var write in recording.CallWrites[c])
                registers[write.Register] = write.Value;
            globals[c] = new Global((registers[0x15] & 7) | (registers[0x16] << 3), registers[0x17], registers[0x18]);
        }
        return globals;
    }

    /// <summary>
    /// The filter as a program from the first call: a step for each run of calls the cutoff moves
    /// by the same amount in, with the routing, mode and volume unchanged - a sweep is one step,
    /// a held cutoff one more.
    /// </summary>
    private static void Filter(MagPlay.Score.Score score, Global[] globals)
    {
        if (globals.All(g => g == default))
            return;

        score.FilterProgram = score.FilterTable.Count;
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

    // ------------------------------------------------------------------ notes

    /// <summary>One voice's notes as rows - one long pattern, each row as long as its note - each
    /// on the first instrument that plays it as the tune does, or a new one that does.</summary>
    private static void Voice(MagPlay.Score.Score score, int v, Frame[] frames, List<int> rises, List<Sound> sounds)
    {
        if (rises.Count == 0)
            return;

        var pattern = new Pattern { Number = v + 1 };
        if (rises[0] > 0)
            pattern.Rows.Add(Row.Hold with { Calls = rises[0] });

        var notes = new List<(int Start, int Length, int Tail, int Index)>();
        for (int i = 0; i < rises.Count; i++)
        {
            int start = rises[i], end = i + 1 < rises.Count ? rises[i + 1] : frames.Length;
            notes.Add((start, end - start, i + 1 < rises.Count ? Tail(frames, start, end) : 0, i));
        }

        // Longest first, so an instrument is made from the most of its program there is.
        var rows = new Row[notes.Count];
        foreach (var note in notes.OrderByDescending(n => n.Length - n.Tail))
        {
            var heard = Sound.Hear(score.NoteTable, frames, note.Start, note.Length, note.Tail);
            int number = sounds.FindIndex(s => s.Plays(heard));
            if (number < 0)
            {
                sounds.Add(heard);
                number = sounds.Count - 1;
            }
            rows[note.Index] = Row.Of(heard.Note, number + 1) with { Calls = note.Length };
        }

        pattern.Rows.AddRange(rows);
        score.Patterns.Add(pattern);
        score.Voices[v].Order.Add(new OrderEntry(pattern.Number));
        score.Voices[v].LoopTo = -1;
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

    /// <summary>
    /// What a voice does while a note sounds, call by call - its control byte and pitch, its
    /// envelope, its pulse width - and how it is quietened before the next note. Made from one
    /// note; it plays another when it does the same for as long as that one lasts, its wave
    /// program holding its last step past its end, and its pulse carrying on its last sweep.
    /// </summary>
    private sealed class Sound
    {
        public required int Note { get; init; }
        public required List<(int Control, Pitch Pitch, int AttackDecay, int SustainRelease)> Wave { get; init; }
        public required List<int> Pulse { get; init; }
        public required int Tail { get; init; }
        public required int TailAttackDecay { get; init; }
        public required int TailSustainRelease { get; init; }

        public static Sound Hear(int[] table, Frame[] frames, int start, int length, int tail)
        {
            int sounding = length - tail;
            int note = BaseNote(table, frames, start, sounding);
            var wave = new List<(int, Pitch, int, int)>();
            var pulse = new List<int>();
            for (int c = start; c < start + sounding; c++)
            {
                var f = frames[c];
                wave.Add((f.Control, PitchOf(table, note, f.Frequency), f.AttackDecay, f.SustainRelease));
                pulse.Add(f.Pulse);
            }

            var quiet = tail > 0 ? frames[start + sounding] : default;
            return new Sound
            {
                Note = note, Wave = wave, Pulse = pulse, Tail = tail,
                TailAttackDecay = quiet.AttackDecay, TailSustainRelease = quiet.SustainRelease,
            };
        }

        /// <summary>The note a note is: its first pitch that is one of the table's.</summary>
        private static int BaseNote(int[] table, Frame[] frames, int start, int length)
        {
            for (int c = start; c < start + length; c++)
            {
                if (frames[c].Tonal && Array.BinarySearch(table, frames[c].Frequency) is >= 0 and var n)
                    return n;
            }
            for (int c = start; c < start + length; c++)
            {
                if (Array.BinarySearch(table, frames[c].Frequency) is >= 0 and var n)
                    return n;
            }

            int nearest = Array.BinarySearch(table, frames[start].Frequency);
            return Math.Clamp(nearest < 0 ? ~nearest : nearest, 0, table.Length - 1);
        }

        private static Pitch PitchOf(int[] table, int note, int frequency) =>
            Array.BinarySearch(table, frequency) is >= 0 and var n ? Pitch.Relative(n - note) : Pitch.Frequency(frequency);

        /// <summary>Plays <paramref name="other"/>'s note as it sounds: the same quietening, and for
        /// as many calls as it lasts the same as this does.</summary>
        public bool Plays(Sound other)
        {
            if (other.Tail != Tail || other.TailAttackDecay != TailAttackDecay || other.TailSustainRelease != TailSustainRelease)
                return false;
            for (int c = 0; c < other.Wave.Count; c++)
            {
                if (Wave[Math.Min(c, Wave.Count - 1)] != other.Wave[c] || PulseAt(c) != other.Pulse[c])
                    return false;
            }
            return true;
        }

        /// <summary>The pulse width on call <paramref name="c"/>, the last sweep carried on past the end.</summary>
        private int PulseAt(int c)
        {
            if (c < Pulse.Count)
                return Pulse[c];
            int step = Pulse.Count > 1 ? Delta(Pulse[^2], Pulse[^1]) : 0;
            return (Pulse[^1] + step * (c - Pulse.Count + 1)) & 0xFFF;
        }

        private static int Delta(int from, int to) => ((to - from + 0x800) & 0xFFF) - 0x800;

        /// <summary>The instrument: its envelope, quietening and programs, its programs added to
        /// the score's tables.</summary>
        public Instrument ToInstrument(MagPlay.Score.Score score)
        {
            var (_, _, ad, sr) = Wave[0];
            var instrument = new Instrument
            {
                Number = score.Instruments.Count + 1,
                AttackDecay = ad,
                SustainRelease = sr,
                GateTimer = Tail,
                HardRestart = Tail == 0 ? HardRestartKind.None
                    : TailAttackDecay == Wave[^1].AttackDecay && TailSustainRelease == Wave[^1].SustainRelease ? HardRestartKind.GateOff
                    : HardRestartKind.Envelope,
                HardRestartEnvelope = (TailAttackDecay << 8) | TailSustainRelease,
                WaveProgram = score.WaveTable.Count,
                PulseProgram = score.PulseTable.Count,
            };

            for (int c = 0; c < Wave.Count;)
            {
                var (control, pitch, attackDecay, sustainRelease) = Wave[c];
                if (c > 0 && attackDecay != Wave[c - 1].AttackDecay)
                    score.WaveTable.Add(new EffectStep(new SetAttackDecay(attackDecay)));
                if (c > 0 && sustainRelease != Wave[c - 1].SustainRelease)
                    score.WaveTable.Add(new EffectStep(new SetSustainRelease(sustainRelease)));
                int run = 1;
                while (c + run < Wave.Count && Wave[c + run] == Wave[c])
                    run++;
                score.WaveTable.Add(new WaveStep(control, pitch, run));
                c += run;
            }
            score.WaveTable.Add(new JumpStep(-1));

            // The width, then each run of calls it moves by the same amount, the last run on and on.
            var runs = new List<(int Add, int Calls)>();
            for (int c = 1; c < Pulse.Count; c++)
            {
                int add = Delta(Pulse[c - 1], Pulse[c]);
                if (runs.Count > 0 && runs[^1].Add == add)
                    runs[^1] = (add, runs[^1].Calls + 1);
                else
                    runs.Add((add, 1));
            }

            int Calls(int i) => i == runs.Count - 1 && runs[i].Add != 0 ? int.MaxValue / 2 : runs[i].Calls;
            if (runs.Count == 0)
                score.PulseTable.Add(new PulseStep(Pulse[0]));
            else
                score.PulseTable.Add(new PulseStep(Pulse[0], runs[0].Add, Calls(0) == int.MaxValue / 2 ? Calls(0) : 1 + runs[0].Calls));
            for (int i = 1; i < runs.Count; i++)
                score.PulseTable.Add(new PulseStep(null, runs[i].Add, Calls(i)));
            score.PulseTable.Add(new JumpStep(-1));
            return instrument;
        }
    }
}
