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

    /// <summary>Cycles the C64 is run at a time while the calls are watched.</summary>
    private const int Slice = 100_000;

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
        void Watch()
        {
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
            while (found is null && computer.Cycle < end)
                tune.Run(Slice);
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

    /// <summary>FNV-1a over the pages that count.</summary>
    private static ulong Hash(byte[] ram, bool[] pages)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;
        for (int page = 0; page < 256; page++)
        {
            if (!pages[page])
                continue;
            for (int i = page << 8, end = i + 0x100; i < end; i++)
            {
                hash ^= ram[i];
                hash *= prime;
            }
        }
        return hash;
    }
}
