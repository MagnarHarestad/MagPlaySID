using System.Numerics;
using System.Runtime.InteropServices;
using MagPlay.Score;
using MagSid.Playback;

namespace MagPlay.C64;

/// <summary>
/// How long a tune plays before it repeats, measured on the <see cref="C64"/> - so for any tune,
/// RSIDs included, which <see cref="SidSongLength"/> cannot run.
///
/// The same reasoning as there: a tune with no input is decided by its memory, so when its memory
/// at the start of a call is as it was at the start of an earlier one, it has looped. The memory
/// that counts is the zero page, the tune's own bytes, and every page the tune has written to by
/// then - an RSID unpacks code and keeps buffers wherever it likes - but not the stack. A page
/// written for the first time makes the states seen before it incomparable, and they are let go.
/// </summary>
public static class C64SongLength
{
    /// <summary>Twenty minutes of the tune, some twenty seconds of running: SOS repeats after 14:20.</summary>
    public const double MaxSeconds = 20 * 60;

    /// <summary>Seconds without a single call after which a tune is given up on: what it plays comes
    /// from its main loop or an NMI, never from a call, and its memory is only looked at as a call
    /// starts - so twenty minutes of it would find nothing.</summary>
    private const double NoCalls = 10;

    /// <summary>Cycles the C64 is run at a time while the calls are watched.</summary>
    private const int Slice = 100_000;

    /// <summary>Seconds every voice has to stay quiet for a tune to have ended there - longer than
    /// any pause inside a tune, which a player that loops a silent pattern at its end never leaves.</summary>
    private const double QuietEnd = 30;

    /// <summary>The length in calls - "frames" - and the call rate they are timed at: the tune's
    /// own, from its first call to the one it loops on. Null when the tune cannot be started, has no
    /// calls, or shows no repeating state within <paramref name="maxSeconds"/>.</summary>
    public static SidSongLength.Result? Measure(SidFile file, int song, double maxSeconds = MaxSeconds) =>
        SidTune.Unsupported(file) is not null ? null : Measure(SidTune.Load(file, song, record: false), maxSeconds);

    /// <summary>The length of <paramref name="tune"/>, run from where it is until it loops or
    /// <paramref name="maxSeconds"/> have played - recording as it was loaded to, so a tune can be
    /// measured and recorded in one run. It is left where the loop was found.</summary>
    public static SidSongLength.Result? Measure(SidTune tune, double maxSeconds = MaxSeconds)
    {
        if (tune.File.IsBasic)
            return MeasureBasic(tune, maxSeconds);
        var file = tune.File;
        var computer = tune.Computer;
        var pages = new bool[256];
        pages[0] = true;
        for (int page = file.LoadAddress >> 8; page <= Math.Min(0xFF, (file.LoadAddress + file.Data.Length - 1) >> 8); page++)
            pages[page] = true;
        pages[1] = false;

        var seen = new Dictionary<ulong, int>();
        int calls = 0;
        SidSongLength.Result? found = null;
        double clock = Machines.ClockHz(computer.Machine);
        void Watch()
        {
            if (found is not null)
                return;

            // Quiet for long enough: the tune ended where the quiet started. A player that loops a
            // silent pattern at its end keeps its pulse and filter programs running, so its memory
            // may never come round again.
            if (computer.QuietSince() is { } quiet && (computer.Cycle - quiet) / clock >= QuietEnd && computer.Calls.Count > 0 && quiet > computer.Calls[0])
            {
                var cycles = computer.Calls;
                int ended = 0;
                while (ended < cycles.Count && cycles[ended] < quiet)
                    ended++;
                double rate = ended > 1 ? (ended - 1) / ((cycles[ended - 1] - cycles[0]) / clock) : 50;
                found = new SidSongLength.Result(ended, ended, rate > 0 ? rate : 50, EndsQuiet: true);
                return;
            }

            bool grew = false;
            for (int page = 2; page < 256; page++)
            {
                if (!pages[page] && computer.PageWritten(page))
                    pages[page] = grew = true;
            }
            if (grew)
                seen.Clear();

            ulong hash = Hash(computer.Ram, pages);
            if (seen.TryGetValue(hash, out int first))
            {
                var cycles = computer.Calls;
                double seconds = (cycles[^1] - cycles[0]) / Machines.ClockHz(computer.Machine);
                found = new SidSongLength.Result(calls, first, seconds > 0 ? calls / seconds : 50);
                return;
            }

            seen[hash] = calls++;
        }

        long end = (long)(maxSeconds * Machines.ClockHz(computer.Machine));
        computer.CallStarting += Watch;
        try
        {
            long noCalls = (long)(NoCalls * clock);
            while (found is null && computer.Cycle < end)
            {
                if (computer.Calls.Count == 0 && computer.Cycle >= noCalls)
                    return null;
                if (!tune.RunUnlessHalted(Slice))
                    return Halted(tune);
            }
        }
        catch (SidPlaybackException)
        {
            return null;
        }
        finally
        {
            computer.CallStarting -= Watch;
        }

        return found;
    }

    /// <summary>
    /// A BASIC tune's length: the same reasoning, but its memory looked at as each statement starts,
    /// where a loop of the program comes round - an interrupt finds BASIC somewhere else each time. The
    /// memory that counts is as for any tune, with the stack in use, where BASIC keeps its FOR loops
    /// and GOSUBs - and without the jiffy clock, which never repeats, unless the program reads TI: a
    /// program that waits for the clock is in the same state while it waits. Timed in sixtieths of a
    /// second - the KERNAL's interrupt's, which a BASIC tune's rows are framed by too, see
    /// <see cref="BasicFrame"/> - by the clock, as some programs turn the interrupt off to run faster.
    /// </summary>
    private static SidSongLength.Result? MeasureBasic(SidTune tune, double maxSeconds)
    {
        var file = tune.File;
        var computer = tune.Computer;
        var ram = computer.Ram;
        var pages = new bool[256];
        pages[0] = true;
        for (int page = file.LoadAddress >> 8; page <= Math.Min(0xFF, (file.LoadAddress + file.Data.Length - 1) >> 8); page++)
            pages[page] = true;
        pages[1] = false;

        var seen = new Dictionary<ulong, int>();
        SidSongLength.Result? found = null;
        double clock = Machines.ClockHz(computer.Machine);
        bool readsClock = ReadsClock(file);
        int frame = BasicFrame(computer.Machine);
        double rate = clock / frame;
        int Frames(long cycle) => (int)(Math.Max(0, cycle - BasicStart(computer)) / frame);
        long lastQuiet = 0;
        bool heard = false;
        void Quiet()
        {
            if (found is not null || computer.Cycle - lastQuiet < frame)
                return;
            lastQuiet = computer.Cycle;
            // Quiet only after it has been heard: many a program sets the chip up and then reads its
            // DATA in silence for longer than a pause could be.
            var since = computer.QuietSince();
            heard |= since is null;
            if (heard && since is { } quiet && (computer.Cycle - quiet) / clock >= QuietEnd)
            {
                int ended = Frames(quiet);
                found = new SidSongLength.Result(ended, ended, rate, EndsQuiet: true);
            }
        }
        void Watch()
        {
            Quiet();
            if (found is not null)
                return;

            bool grew = false;
            for (int page = 2; page < 256; page++)
            {
                if (!pages[page] && computer.PageWritten(page))
                    pages[page] = grew = true;
            }
            if (grew)
                seen.Clear();

            // The clock out, the stack in use in.
            byte t0 = ram[0xA0], t1 = ram[0xA1], t2 = ram[0xA2];
            if (!readsClock)
                ram[0xA0] = ram[0xA1] = ram[0xA2] = 0;
            ulong hash = Hash(ram, pages);
            (ram[0xA0], ram[0xA1], ram[0xA2]) = (t0, t1, t2);
            int sp = computer.Cpu.SP & 0xFF;
            hash = Merge(hash, (ulong)sp);
            for (int at = 0x100 + sp + 1; at < 0x200; at++)
                hash = Merge(hash, ram[at]);

            // A state again within a second is a program waiting - for the clock, the beam, a key -
            // not its music coming round; one it is still in a second on, that waits for ever.
            if (seen.TryGetValue(hash, out int first))
            {
                if (Frames(computer.Cycle) - first >= rate)
                    found = new SidSongLength.Result(Frames(computer.Cycle), first, rate);
                return;
            }
            seen[hash] = Frames(computer.Cycle);
        }

        long end = (long)(maxSeconds * clock);
        computer.Watch = BasicRoms.NextStatement;
        computer.Reached += Watch;
        computer.CallStarting += Quiet;
        try
        {
            while (found is null && computer.Cycle < end)
            {
                if (!tune.RunUnlessHalted(Slice))
                    return Halted(tune);
            }
        }
        catch (SidPlaybackException)
        {
            return null;
        }
        finally
        {
            computer.Reached -= Watch;
            computer.CallStarting -= Quiet;
            computer.Watch = -1;
        }
        return found;
    }

    /// <summary>The cycles of a BASIC tune's frame: the KERNAL's interrupt's, sixty times a second.</summary>
    public static int BasicFrame(Machine machine) => SidTune.KernalTimer(machine) + 1;

    /// <summary>Where a BASIC tune's frames start: its first interrupt, where it is first heard from.</summary>
    public static long BasicStart(C64 computer) => computer.Calls.Count > 0 ? computer.Calls[0] : 0;

    /// <summary>Whether a BASIC program reads the jiffy clock: TI or TI$ outside quotes and REMs, or a
    /// PEEK of 160 to 162.</summary>
    private static bool ReadsClock(SidFile file)
    {
        var text = file.Data;
        bool quoted = false, rem = false;
        for (int i = 0; i < text.Length; i++)
        {
            byte b = text[i];
            if (b == 0)
            {
                quoted = rem = false;
                i += 4;     // the next line's link and number
                continue;
            }
            if (rem)
                continue;
            if (b == 0x22)
                quoted = !quoted;
            else if (quoted)
                continue;
            else if (b == 0x8F)
                rem = true;
            else if (b == 'T' && i + 1 < text.Length && text[i + 1] == 'I' && (i == 0 || !char.IsLetterOrDigit((char)text[i - 1])))
                return true;
            else if (b == 0xC2 && i + 4 < text.Length && text[i + 1] == '(' && text[i + 2] == '1' && text[i + 3] == '6' && text[i + 4] is (byte)'0' or (byte)'1' or (byte)'2')
                return true;
        }
        return false;
    }

    /// <summary>The length of a tune that has halted the CPU: it ended there, as Warlock's do - Cat
    /// for Dinner at 5:17, where HVSC has 5:22. Null while it runs.</summary>
    public static SidSongLength.Result? Halted(SidTune tune)
    {
        if (!tune.Halted)
            return null;
        var computer = tune.Computer;
        double clock = Machines.ClockHz(computer.Machine);
        // Timed by the clock, not the calls, which may have stopped well before the CPU did.
        var calls = computer.Calls;
        double seconds = calls.Count > 1 ? (calls[^1] - calls[0]) / clock : 0;
        double rate = seconds > 0 ? (calls.Count - 1) / seconds : 50;
        // Quiet already, the tune ended as it went quiet: Dark Side's music stops at 2:34, its CPU at
        // 2:51.
        long end = computer.QuietSince() is { } quiet && quiet > 0 && quiet < computer.Cycle ? quiet : computer.Cycle;
        int frames = (int)Math.Round(end / clock * rate);
        return frames > 0 ? new SidSongLength.Result(frames, frames, rate, EndsQuiet: true) : null;
    }

    private const ulong Prime1 = 11400714785074694791, Prime2 = 14029467366897019727, Prime3 = 1609587929392839161,
                        Prime4 = 9650029242287828579;

    /// <summary>The pages that count as one number: xxHash64's rounds, eight bytes at a time in four
    /// lanes that run side by side - a call's look at a tune of many pages costs a tenth of what a
    /// byte at a time did.</summary>
    internal static ulong Hash(byte[] ram, bool[] pages)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(ram.AsSpan(0, 0x10000));
        ulong a = unchecked(Prime1 + Prime2), b = Prime2, c = 0, d = unchecked(0 - Prime1);
        int counted = 0;
        for (int page = 0; page < 256; page++)
        {
            if (!pages[page])
                continue;
            counted++;
            var row = words.Slice(page * 32, 32);
            for (int i = 0; i < 32; i += 4)
            {
                a = Round(a, row[i]);
                b = Round(b, row[i + 1]);
                c = Round(c, row[i + 2]);
                d = Round(d, row[i + 3]);
            }
        }
        ulong hash = BitOperations.RotateLeft(a, 1) + BitOperations.RotateLeft(b, 7) + BitOperations.RotateLeft(c, 12) + BitOperations.RotateLeft(d, 18);
        hash = Merge(Merge(Merge(Merge(hash, a), b), c), d) + (ulong)counted;
        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        return hash ^ (hash >> 32);
    }

    private static ulong Round(ulong lane, ulong input) => BitOperations.RotateLeft(lane + input * Prime2, 31) * Prime1;

    private static ulong Merge(ulong hash, ulong lane) => (hash ^ Round(0, lane)) * Prime1 + Prime4;
}
