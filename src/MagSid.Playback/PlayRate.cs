namespace MagSid.Playback;

/// <summary>
/// How often a tune's play routine is called. A PSID song's speed bit says which clock drives it:
/// the vertical blank, 50 times a second on a PAL machine, or timer A of CIA 1. A tune on the timer
/// usually sets the timer's latch itself, at $DC04-$DC05, from init or from play - often for a
/// tempo the frame rate cannot give. One that leaves the latch alone runs at the KERNAL's own
/// setting, $4025, which is 60 calls a second on PAL.
///
/// The latch is read back from memory: the player's 64 KiB is flat, so what the tune wrote to
/// $DC04-$DC05 is what is there.
/// </summary>
public static class PlayRate
{
    public const double PalClock = 985248.0;

    public const int TimerLow = 0xDC04;
    public const int TimerHigh = 0xDC05;

    private const int KernalLatch = 0x4025;

    /// <summary>A floor under a tune's own latch: a few hundred cycles is a sample player's rate,
    /// not a music player's, and calling play that often would leave no time for anything else.</summary>
    private const int MinimumLatch = 0x400;

    /// <summary>Cycles from one play call to the next. <paramref name="timerSet"/> is whether the
    /// tune has written the latch yet.</summary>
    public static double CyclesPerCall(SidFile file, int song, byte[] memory, bool timerSet)
    {
        if (!file.UsesCiaTimer(song))
            return PalClock / 50.0;

        int latch = memory[TimerLow] | (memory[TimerHigh] << 8);
        return (timerSet && latch > 0 ? Math.Max(latch, MinimumLatch) : KernalLatch) + 1;
    }

    public static bool IsTimerLatch(int address) => address is TimerLow or TimerHigh;
}
