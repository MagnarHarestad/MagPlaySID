using MagPlay.C64;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.Transcribe;

/// <summary>
/// How long a song is by what it plays - the length a listener would give it - where its memory
/// says something longer.
///
/// The C64's memory coming round again proves a song loops, but a player can keep something that
/// comes round only after several passes of the music, or never: a counter, a pulse or filter sweep
/// running apart from the notes, a random seed. Zardax's memory takes four passes of its 1:48 to
/// repeat; Pacos Pete's song 2, two of its 0:31. What the SID is told to play cannot repeat less
/// often than the memory that tells it, so its period divides the memory's: and within one loop of
/// the memory, the shortest such period that holds through all of it is proven to hold for ever. It
/// is looked for at four levels, each looser than the one before - every write of each call; the
/// voices' waveforms, envelopes and the pitches that can be heard, without the pulse, the filter,
/// the volume, a noise's random pitch or one under the test bit; the note starts, with their
/// waveform and envelope; and the note starts by pitch and waveform, which Dune's Life keeps while
/// its sustain fades from pass to pass - and the shortest that comes out is the song's length,
/// unless its loop starts no note at all, which is a song that has ended, timed by its writes.
///
/// Last, looser still, the notes are compared pass for pass, voice by voice, allowing each a call or
/// two either way and a drum of noise played at random - see <see cref="Loose"/> - from the loop back
/// as far as they hold, which finds where a song begins that a strict level sees only from its
/// second pass. Where the voices repeat apart, each with a period of its own, the song is as long as
/// it takes every voice to play its part once - see <see cref="EveryVoiceOnce"/>; and where the loop
/// starts no note, though a voice holds a drone on for ever, the song ended as its last notes did.
/// Against HVSC's own lengths for every song in it, all this matches more than four in five, where
/// the memory alone matched two.
///
/// A song whose memory never repeats has nothing to prove a period by: there, only one that repeats
/// exactly to the end of the recording, twice over from its first half, is taken - the strictest
/// level first. A song heard to end in silence keeps the length its silence gives it.
/// </summary>
public static class SongPeriods
{
    /// <summary>The shortest period, in seconds, taken without a memory loop to prove it.</summary>
    private const double ShortestUnproven = 2;

    /// <summary>How far, in seconds, a note may come from where it came a pass before and still be the
    /// same pass of the music.</summary>
    private const double Jitter = 0.04;

    /// <summary>How far, as a share of the period, a BASIC tune's note may come from where it came a
    /// pass before: a pass after a CLR finds its variables in another order, and runs a little faster
    /// or slower for it.</summary>
    private const double BasicDrift = 0.02;

    /// <summary>The song's length by what it plays, from <paramref name="recording"/> of its player's
    /// calls up to where <paramref name="memory"/> found it looping - or the memory's own when what it
    /// plays says nothing shorter, or null when neither says anything.</summary>
    public static SidSongLength.Result? Refine(Recording recording, SidSongLength.Result? memory)
    {
        if (memory is { EndsQuiet: true })
            return memory;
        int calls = recording.CallWrites.Count;
        if (calls < 2)
            return memory;
        double seconds = (recording.Calls[^1] - recording.Calls[0]) / Machines.ClockHz(recording.Machine);
        double rate = memory?.CallsPerSecond ?? (seconds > 0 ? (calls - 1) / seconds : 50);

        var writes = WriteHashes(recording);
        var tones = ToneHashes(recording);
        var notes = NoteHashes(recording);
        var sounds = NoteHashes(recording, soundOnly: true);

        if (memory is null)
        {
            int shortest = (int)(ShortestUnproven * rate);
            foreach (var level in new[] { writes, tones, notes })
                if (Free(level, shortest) is { } free && Starts(notes, free))
                    return Result(free, rate);
            return null;
        }

        if (memory.TotalFrames > calls || memory.LoopFrames <= 0)
            return memory;
        (int Start, int Period)? best = null;
        foreach (var (level, mustStartNotes) in new[] { (writes, false), (tones, true), (notes, true), (sounds, true) })
        {
            if (Dividing(level, memory.LoopStartFrame, memory.TotalFrames) is not { } found)
                continue;
            // A loop with no note in it is a song that has ended: its writes say when.
            if (mustStartNotes && !Starts(notes, found))
                continue;
            if (best is not { } b || found.Start + found.Period < b.Start + b.Period)
                best = found;
        }
        var voices = VoiceNotes(recording);
        if (Loose(voices, memory.LoopStartFrame, memory.TotalFrames, rate, recording.Basic ? BasicDrift : 0) is { } loose &&
            (best is not { } bl || loose.Start + loose.Period < bl.Start + bl.Period))
            best = loose;
        int whole = best is { } bw ? bw.Start + bw.Period : memory.TotalFrames;
        if (EveryVoiceOnce(voices, calls, memory.LoopStartFrame, memory.TotalFrames) is { } once &&
            once.Start + once.Period < whole)
            best = once;
        // A loop that starts no note is a song that has ended, though a voice may hold a drone on for
        // ever: it ended as its last notes did - unless the memory stands still, which is an end in
        // itself, where a slide or a long note that no new note starts dies away: Retrograde's song 8.
        if (memory.LoopFrames >= ShortestUnproven * rate && !Starts(notes, (memory.LoopStartFrame, memory.LoopFrames)) &&
            LastNotesEnd(recording, rate) is { } ended &&
            ended < (best is { } bn ? bn.Start + bn.Period : memory.TotalFrames))
            return new SidSongLength.Result(ended, ended, rate, EndsQuiet: true);
        return best is { } shortestLoop ? Result(shortestLoop, rate) : memory;
    }

    /// <summary>
    /// Where every voice has played its part once, when the voices repeat apart: each voice's notes
    /// repeat with a period of their own, proven as the song's is by dividing the memory's loop, and
    /// the song as a whole only once they all come round together. At War's two voices repeat every 4
    /// and 12 seconds and meet again after 19 minutes; it is as long as its longer part, as HVSC
    /// times such a song - for six in ten of the songs whose whole repeat took over ten minutes, HVSC's
    /// length, where the whole repeat gave none. A
    /// voice that plays only before the loop has played its part by its last note.
    /// </summary>
    internal static (int Start, int Period)? EveryVoiceOnce(List<NoteStart>[] voices, int calls, int loopStart, int total)
    {
        int end = 0, latestStart = 0;
        bool apart = false;
        int? firstPeriod = null;
        for (int v = 0; v < voices.Length; v++)
        {
            var notes = voices[v];
            if (notes.Count == 0)
                continue;
            if (!notes.Exists(n => n.Call >= loopStart && n.Call < total))
            {
                end = Math.Max(end, notes[^1].Call + 1);
                continue;
            }
            var keys = new ulong[Math.Min(calls, total)];
            foreach (var n in notes)
                if (n.Call < keys.Length)
                    keys[n.Call] = n.Note | 1UL << 63;
            if (Dividing(keys, loopStart, keys.Length) is not { } own)
                return null;
            end = Math.Max(end, own.Start + own.Period);
            latestStart = Math.Max(latestStart, own.Start);
            apart |= firstPeriod is { } p && p != own.Period;
            firstPeriod ??= own.Period;
        }
        return apart && end > latestStart ? (latestStart, end - latestStart) : null;
    }

    /// <summary>The call the last notes have ended by: each voice's last, once its gate went off and
    /// its release has run its course - or, held, once its attack and decay have; null with none.
    /// Tekkno's last notes end at 2:28, though a ringing triangle sounds on under them.</summary>
    internal static int? LastNotesEnd(Recording recording, double rate)
    {
        var registers = Registers(recording);
        var edge = new int[9];
        var gate = new bool[9];
        for (int v = 0; v < 9; v++)
            gate[v] = (registers[Base(v) + 4] & 1) != 0;
        bool any = gate.Any(g => g);
        int changed = -1;
        double end = -1;
        long Ms(int ms) => (long)Math.Ceiling(ms * rate / 1000);

        // Until when voice v is heard, by its envelope since its gate last changed - for ever while it
        // holds a sustain.
        long HeardUntil(int v)
        {
            int b = Base(v), attackDecay = registers[b + 5], sustainRelease = registers[b + 6];
            if (!gate[v])
                return edge[v] + Ms(EnvelopeTimes.DecayMs[sustainRelease & 0x0F]);
            return sustainRelease >> 4 == 0
                ? edge[v] + Ms(EnvelopeTimes.AttackMs[attackDecay >> 4]) + Ms(EnvelopeTimes.DecayMs[attackDecay & 0x0F])
                : long.MaxValue;
        }

        for (int c = 0; c < recording.CallWrites.Count; c++)
            foreach (var w in recording.CallWrites[c])
            {
                if (w.Register >= 0x60)
                    continue;
                int before = registers[w.Register];
                var (voice, part) = VoiceOf(w.Register);
                if (voice is >= 0 and < 9 && part == 4 && ((w.Value ^ before) & 1) != 0)
                {
                    // The note before goes on sounding as it would have.
                    if (any && HeardUntil(voice) is var heard && heard != long.MaxValue)
                        end = Math.Max(end, Math.Min(heard, c));
                    (gate[voice], edge[voice]) = ((w.Value & 1) != 0, c);
                    any |= gate[voice];
                }
                registers[w.Register] = w.Value;
                // Nothing is heard of a chip turned down to nothing.
                if (w.Value == before || !any || (registers[(w.Register & 0x60) + 0x18] & 0x0F) == 0)
                    continue;
                // A pitch or a waveform changed while its voice is heard, or the volume while any is:
                // a slide, a fade - sound going on.
                bool heardNow = voice is >= 0 and < 9
                    ? part is 0 or 1 or 4 && c < HeardUntil(voice)
                    : (w.Register & 0x1F) == 0x18 && ((w.Value ^ before) & 0x0F) != 0 &&
                      Enumerable.Range(0, 9).Any(v => c < HeardUntil(v));
                if (heardNow)
                    changed = c;
            }

        if (!any)
            return null;
        for (int v = 0; v < 9; v++)
            if (edge[v] > 0 || gate[v])
            {
                long heard = HeardUntil(v);
                if (heard != long.MaxValue)
                    end = Math.Max(end, heard);
                else if (gate[v])
                    end = Math.Max(end, edge[v] + Ms(EnvelopeTimes.AttackMs[registers[Base(v) + 5] >> 4]) + Ms(EnvelopeTimes.DecayMs[registers[Base(v) + 5] & 0x0F]));
            }
        end = Math.Max(end, changed + 1);
        return end <= 0 ? null : (int)end;
    }

    /// <summary>A voice's note start: its call, the note as one number - pitch, waveform, envelope,
    /// without the pitch where the waveform lets none be heard - and whether it is a noise's.</summary>
    internal readonly record struct NoteStart(int Call, ulong Note, bool Noise);

    /// <summary>Whether a voice's control register lets its pitch be heard: a waveform chosen, the
    /// test bit off.</summary>
    private static bool Sounding(int control) => (control & 0xF0) != 0 && (control & 0x08) == 0;

    /// <summary>Each voice's note starts, in order.</summary>
    internal static List<NoteStart>[] VoiceNotes(Recording recording)
    {
        var registers = Registers(recording);
        var voices = new List<NoteStart>[9];
        for (int v = 0; v < 9; v++)
            voices[v] = [];
        var rose = new bool[9];
        // A note started silent, waiting for the call it sounds in: its place in its voice's list.
        var waiting = new int[9];
        Array.Fill(waiting, -1);
        // A BASIC tune's legato: a new pitch on a voice whose gate stays on, a note to a listener. It
        // is taken a call on, once a POKE of the other half of the pitch has come too.
        var pitch = new int[9];
        var settling = new int[9];
        Array.Fill(settling, -1);
        for (int c = 0; c < recording.CallWrites.Count; c++)
        {
            Array.Clear(rose);
            foreach (var w in recording.CallWrites[c])
            {
                if (w.Register >= 0x60)
                    continue;
                if (VoiceOf(w.Register) is (>= 0 and < 9, 4) and var (voice, _) && (w.Value & 1) != 0 && (registers[w.Register] & 1) == 0)
                    rose[voice] = true;
                registers[w.Register] = w.Value;
            }
            for (int v = 0; v < 9; v++)
            {
                int b = Base(v), control = registers[b + 4];
                ulong sound = ((ulong)(uint)control << 20) | (uint)(registers[b] | (registers[b + 1] << 8));
                if (rose[v])
                {
                    ulong envelope = ((ulong)(uint)registers[b + 5] << 28) | ((ulong)(uint)registers[b + 6] << 36);
                    bool sounds = Sounding(control);
                    voices[v].Add(new NoteStart(c, envelope | (sounds ? sound : (ulong)(uint)control << 20), (control & 0x80) != 0));
                    waiting[v] = sounds ? -1 : voices[v].Count - 1;
                }
                else if (waiting[v] >= 0 && (control & 1) == 0)
                    waiting[v] = -1;
                else if (waiting[v] >= 0 && Sounding(control))
                {
                    var start = voices[v][waiting[v]];
                    voices[v][waiting[v]] = start with { Note = (start.Note & ~0xFFFFFFFUL) | sound, Noise = (control & 0x80) != 0 };
                    waiting[v] = -1;
                }
                if (!recording.Basic)
                    continue;
                int frequency = registers[b] | (registers[b + 1] << 8);
                if (settling[v] >= 0)
                {
                    var start = voices[v][settling[v]];
                    voices[v][settling[v]] = start with { Note = (start.Note & ~0xFFFFFFFUL) | sound };
                    settling[v] = -1;
                }
                else if (!rose[v] && frequency != pitch[v] && (control & 1) != 0 && Sounding(control) && voices[v].Count > 0)
                {
                    ulong envelope = ((ulong)(uint)registers[b + 5] << 28) | ((ulong)(uint)registers[b + 6] << 36);
                    voices[v].Add(new NoteStart(c, envelope | sound, (control & 0x80) != 0));
                    settling[v] = voices[v].Count - 1;
                }
                pitch[v] = frequency;
            }
        }
        return voices;
    }

    /// <summary>
    /// The shortest period a proven loop, from <paramref name="loopStart"/> to <paramref name="total"/>,
    /// divides into passes of the same music - each voice's notes, one for one, the same as a pass
    /// before, within <see cref="Jitter"/> of where they came. A player that counts its tempo apart
    /// from its music may play a pass a call longer or shorter than the one before, so that the exact
    /// repeat is two passes: The Last Ninja's 4:51 played twice over. A hit of noise may stand for any
    /// note of its voice, a drum played at random: Hunters Moon's. What the passes play is otherwise
    /// the same, note for note: the same tune played on other instruments is not the same pass.
    /// </summary>
    internal static (int Start, int Period)? Loose(List<NoteStart>[] voices, int loopStart, int total, double rate, double drift = 0)
    {
        int loop = total - loopStart;
        int tolerance = Math.Max(1, (int)Math.Round(Jitter * rate));
        int shortest = Math.Max(1, (int)(ShortestUnproven * rate));
        var ranges = new (int From, int Count)[voices.Length];
        bool any = false;
        for (int v = 0; v < voices.Length; v++)
        {
            var notes = voices[v];
            int from = 0;
            while (from < notes.Count && notes[from].Call < loopStart)
                from++;
            int to = from;
            while (to < notes.Count && notes[to].Call < total)
                to++;
            ranges[v] = (from, to - from);
            any |= to > from;
        }
        if (!any)
            return null;
        (int Start, int Period)? best = null;
        for (int passes = loop / shortest; passes >= 1; passes--)
        {
            double period = (double)loop / passes;
            // A BASIC tune's passes may differ in speed: as much as their drift of a period, more.
            tolerance = Math.Max(tolerance, (int)Math.Round(drift * period));
            bool holds = true;
            for (int v = 0; v < voices.Length && holds; v++)
            {
                var (from, count) = ranges[v];
                if (count == 0)
                    continue;
                if (count % passes != 0)
                {
                    holds = false;
                    break;
                }
                int each = count / passes;
                var notes = voices[v];
                for (int i = 0; i < count && holds; i++)
                {
                    var a = notes[from + i];
                    int j = i + each;
                    var b = j < count ? notes[from + j] : notes[from + j - count];
                    double at = j < count ? b.Call : b.Call + loop;
                    holds = Math.Abs(at - a.Call - period) <= tolerance && (a.Note == b.Note || a.Noise || b.Noise);
                }
            }
            if (!holds)
                continue;
            // Back from the loop, as far as each voice's notes are those a pass later.
            int start = 0;
            for (int v = 0; v < voices.Length; v++)
            {
                var (from, count) = ranges[v];
                var notes = voices[v];
                int each = count / passes;
                for (int i = from - 1; i >= 0; i--)
                {
                    var a = notes[i];
                    bool same = count > 0 && i + each < notes.Count && notes[i + each] is var b &&
                                Math.Abs(b.Call - a.Call - period) <= tolerance && (a.Note == b.Note || a.Noise || b.Noise);
                    if (!same)
                    {
                        start = Math.Max(start, a.Call + 1);
                        break;
                    }
                }
            }
            start = Math.Min(start, loopStart);
            var found = (start, (int)Math.Round(period));
            if (best is not { } b2 || found.start + found.Item2 < b2.Start + b2.Period)
                best = found;
        }
        return best;
    }

    private static SidSongLength.Result Result((int Start, int Period) loop, double rate) =>
        new(loop.Start + loop.Period, loop.Start, rate);

    /// <summary>Whether a note starts anywhere in the loop.</summary>
    private static bool Starts(ulong[] notes, (int Start, int Period) loop)
    {
        for (int c = loop.Start; c < loop.Start + loop.Period && c < notes.Length; c++)
            if (notes[c] != 0)
                return true;
        return false;
    }

    private const ulong Basis = 14695981039346656037, Prime = 1099511628211;

    /// <summary>Each call's writes - register and value, in the order made - as one number.</summary>
    internal static ulong[] WriteHashes(Recording recording)
    {
        var hashes = new ulong[recording.CallWrites.Count];
        for (int c = 0; c < hashes.Length; c++)
        {
            ulong h = Basis;
            foreach (var w in recording.CallWrites[c])
                h = (((h ^ (ulong)w.Register) * Prime) ^ (ulong)w.Value) * Prime;
            hashes[c] = h;
        }
        return hashes;
    }

    /// <summary>Which voice a register is of, on any chip - and which of its seven registers - or -1
    /// for the filter and volume.</summary>
    private static (int Voice, int Part) VoiceOf(int register)
    {
        int own = register & 0x1F;
        return own < 0x15 ? ((register >> 5) * 3 + own / 7, own % 7) : (-1, -1);
    }

    /// <summary>The voices' registers as init left them, before the first call.</summary>
    private static int[] Registers(Recording recording)
    {
        var registers = new int[0x60];
        long first = recording.Calls.Count > 0 ? recording.Calls[0] : long.MaxValue;
        foreach (var w in recording.Between)
            if (w.Cycle < first && w.Register < 0x60)
                registers[w.Register] = w.Value;
        return registers;
    }

    /// <summary>Whether a voice's pitch is heard after a call: its waveform sounding, and not noise,
    /// whose pitch a player may set at random.</summary>
    private static bool PitchHeard(int control) => Sounding(control) && (control & 0x80) == 0;

    /// <summary>Each call's writes to the voices' waveform and envelope, in order, and the pitch of
    /// each voice whose pitch is heard after the call - when the call set it, or the voice has just
    /// come to be heard: not the pulse, the filter or the volume, which may sweep apart from the music,
    /// nor a pitch that cannot be heard - a noise's, or one set under the test bit or with no waveform,
    /// as a hard restart may leave it.</summary>
    internal static ulong[] ToneHashes(Recording recording)
    {
        var registers = Registers(recording);
        var heard = new bool[9];
        var pitched = new bool[9];
        for (int v = 0; v < 9; v++)
            heard[v] = PitchHeard(registers[Base(v) + 4]);
        var hashes = new ulong[recording.CallWrites.Count];
        for (int c = 0; c < hashes.Length; c++)
        {
            Array.Clear(pitched);
            ulong h = Basis;
            foreach (var w in recording.CallWrites[c])
            {
                if (w.Register >= 0x60)
                    continue;
                registers[w.Register] = w.Value;
                var (voice, part) = VoiceOf(w.Register);
                if (voice < 0)
                    continue;
                if (part < 2)
                    pitched[voice] = true;
                else if (part > 3)
                    h = (((h ^ (ulong)w.Register) * Prime) ^ (ulong)w.Value) * Prime;
            }
            for (int v = 0; v < 9; v++)
            {
                int b = Base(v);
                bool hears = PitchHeard(registers[b + 4]);
                if (hears && (pitched[v] || !heard[v]))
                    h = (((h ^ (ulong)(0x100 + v)) * Prime) ^ (ulong)(uint)(registers[b] | (registers[b + 1] << 8))) * Prime;
                heard[v] = hears;
            }
            hashes[c] = h;
        }
        return hashes;
    }

    /// <summary>The first of voice <paramref name="v"/>'s registers, on its chip.</summary>
    private static int Base(int v) => (v / 3) * 0x20 + (v % 3) * 7;

    /// <summary>Each call's note starts - each voice whose gate went on, with its pitch, waveform and
    /// envelope after the call, from the registers as init left them, or its pitch and waveform alone
    /// - as one number; 0 for a call that starts none.</summary>
    internal static ulong[] NoteHashes(Recording recording, bool soundOnly = false)
    {
        var registers = Registers(recording);

        var hashes = new ulong[recording.CallWrites.Count];
        var rose = new bool[9];
        for (int c = 0; c < hashes.Length; c++)
        {
            Array.Clear(rose);
            foreach (var w in recording.CallWrites[c])
            {
                if (w.Register >= 0x60)
                    continue;
                if (VoiceOf(w.Register) is (>= 0 and < 9, 4) and var (voice, _) && (w.Value & 1) != 0 && (registers[w.Register] & 1) == 0)
                    rose[voice] = true;
                registers[w.Register] = w.Value;
            }

            ulong h = 0;
            for (int v = 0; v < 9; v++)
            {
                if (!rose[v])
                    continue;
                int b = Base(v);
                ulong note = (uint)(v + 1) | ((ulong)(uint)(registers[b] | (registers[b + 1] << 8)) << 4);
                note |= (ulong)(uint)registers[b + 4] << 20;
                if (!soundOnly)
                    note |= ((ulong)(uint)registers[b + 5] << 28) | ((ulong)(uint)registers[b + 6] << 36);
                h = ((h ^ note) * Prime) + 1;
            }
            hashes[c] = h;
        }
        return hashes;
    }

    /// <summary>The shortest period dividing the memory's loop, from <paramref name="loopStart"/> to
    /// <paramref name="total"/>, that the calls repeat with through all of it - proven, as the memory
    /// goes on repeating - and the earliest call from which it holds.</summary>
    internal static (int Start, int Period)? Dividing(ulong[] calls, int loopStart, int total)
    {
        int loop = total - loopStart;
        for (int period = 1; period <= loop; period++)
        {
            if (loop % period != 0)
                continue;
            bool holds = true;
            for (int c = loopStart; c + period < total && holds; c++)
                holds = calls[c] == calls[c + period];
            if (!holds)
                continue;
            int start = loopStart;
            while (start > 0 && calls[start - 1] == calls[start - 1 + period])
                start--;
            return (start, period);
        }
        return null;
    }

    /// <summary>The shortest period, of <paramref name="shortest"/> calls or more, that the calls
    /// repeat with from some call in the first half of the recording to its end, twice over - taken
    /// with nothing to prove it goes on.</summary>
    internal static (int Start, int Period)? Free(ulong[] calls, int shortest)
    {
        int n = calls.Length;
        for (int period = Math.Max(1, shortest); period <= n / 2; period++)
        {
            int back = n - 1;
            while (back >= period && calls[back] == calls[back - period])
                back--;
            int start = back - period + 1;
            if (n - start >= 2 * period && start <= n / 2)
                return (start, period);
        }
        return null;
    }
}
