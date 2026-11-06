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
