namespace MagSid.Playback;

/// <summary>
/// How long a tune plays before it starts repeating itself.
///
/// A PSID tune is a deterministic program with no input: everything it will ever do is decided by
/// its own memory. So if that memory ever returns to a state it has been in before, the music from
/// then on is exactly what it was last time - the tune has looped, and where is not a guess.
///
/// Which memory: zero page plus the tune's own bytes. Not the stack, because this player resets the
/// stack pointer before each call and whatever is left below it does not affect anything - hashing
/// it would leave a tune looking different on every pass and no loop would ever be found. Not the
/// SID's own registers either; they are write-only and the chip's state follows from what the
/// program writes rather than the other way round.
///
/// It is a heuristic all the same. A tune that keeps a counter it never wraps, or reseeds something
/// from a source outside this model, has no repeating state and gets no length - which is reported
/// as "unknown" rather than as a number that would be wrong.
/// </summary>
public static class SidSongLength
{
    /// <summary>
    /// Thirty minutes at 50Hz, about three seconds of searching.
    ///
    /// A real ceiling rather than a safety limit. The three channels run independent order lists
    /// and their patterns carry their own LE loops, so a tune only repeats once every one of them
    /// lines up again - which can be far longer than any of the parts. SOS in this repository runs
    /// 14:20 before it does, on lists of 32, 86 and 68 rows. Past this ceiling a length is reported
    /// as unknown, which is the only honest thing to put beside a running clock.
    /// </summary>
    public const int DefaultMaxFrames = 50 * 60 * 30;

    /// <param name="TotalFrames">Frames until the music starts repeating - what a listener would
    /// call the length.</param>
    /// <param name="LoopStartFrame">Where the repeating part begins. Greater than zero when the
    /// tune has an intro it never returns to.</param>
    /// <param name="CallsPerSecond">How often play is called - 50 on the vertical blank, the
    /// timer's own rate for a tune on CIA 1. A "frame" here is one play call.</param>
    public sealed record Result(int TotalFrames, int LoopStartFrame, double CallsPerSecond = 50.0)
    {
        public int LoopFrames => TotalFrames - LoopStartFrame;

        public TimeSpan Total => TimeSpan.FromSeconds(TotalFrames / CallsPerSecond);

        public TimeSpan LoopStart => TimeSpan.FromSeconds(LoopStartFrame / CallsPerSecond);

        public TimeSpan Loop => TimeSpan.FromSeconds(LoopFrames / CallsPerSecond);
    }

    /// <summary>Null when the tune shows no repeating state inside <paramref name="maxFrames"/>.
    /// <paramref name="song"/> is the subtune, 1-based; the file's start song when left out.</summary>
    public static Result? Measure(SidFile file, int maxFrames = DefaultMaxFrames, int? song = null)
    {
        if (file.UnsupportedReason() is not null)
            return null;

        var memory = new byte[0x10000];
        file.Data.CopyTo(memory, file.LoadAddress);

        bool timerSet = false;
        var cpu = new Mos6502(memory, (address, _) => timerSet |= PlayRate.IsTimerLatch(address));
        int subtune = song ?? file.StartSong;
        cpu.A = Math.Max(0, subtune - 1);

        try
        {
            cpu.Call(file.InitAddress);
        }
        catch (SidPlaybackException)
        {
            return null;
        }

        // The rate init left the timer at; a tune that changes it while playing is measured at this
        // one, which is the tempo it starts with.
        double callsPerSecond = PlayRate.PalClock / PlayRate.CyclesPerCall(file, subtune, memory, timerSet);

        int from = file.LoadAddress;
        int to = Math.Min(0xFFFF, file.LoadAddress + file.Data.Length - 1);

        // The frame each state was first seen at. A hit means everything from that frame onwards
        // repeats, so the answer is both numbers at once.
        var seen = new Dictionary<ulong, int>(maxFrames) { [Hash(memory, from, to)] = 0 };

        for (int frame = 1; frame <= maxFrames; frame++)
        {
            try
            {
                cpu.Call(file.PlayAddress);
            }
            catch (SidPlaybackException)
            {
                return null;
            }

            ulong hash = Hash(memory, from, to);
            if (seen.TryGetValue(hash, out int first))
                return new Result(frame, first, callsPerSecond);

            seen[hash] = frame;
        }

        return null;
    }

    /// <summary>FNV-1a over zero page and the tune's own bytes. A hash rather than kept snapshots
    /// because ten minutes of 64 KiB images is memory nobody has; a collision would misreport a
    /// length, and at 64 bits over a few tens of thousands of frames that is not a risk worth
    /// paying real memory to remove.</summary>
    private static ulong Hash(byte[] memory, int from, int to)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;

        for (int i = 0; i < 0x100; i++)
        {
            hash ^= memory[i];
            hash *= prime;
        }

        for (int i = from; i <= to; i++)
        {
            hash ^= memory[i];
            hash *= prime;
        }

        return hash;
    }
}
