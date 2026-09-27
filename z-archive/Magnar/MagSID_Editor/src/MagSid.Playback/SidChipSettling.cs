using MagSid.Sid;

namespace MagSid.Playback;

internal static class SidChipSettling
{
    /// <summary>
    /// Clocks a freshly configured chip past its power-on settling, discarding the samples.
    ///
    /// reSID's external filter starts from zero, so the first output after the volume register
    /// is written is a large DC step - measured at ~15000 peak-to-peak over the first 50ms,
    /// which reaches the speakers as a thump the instant the output device opens. Every player
    /// here calls this once at construction so the first buffer handed to the sound card is
    /// settled silence instead.
    /// </summary>
    public static void SettleStartupTransient(this SidChip sid, double clockFrequency)
    {
        int cyclesRemaining = (int)(clockFrequency * 0.05);
        var discard = new short[2048];

        while (cyclesRemaining > 0)
        {
            int before = cyclesRemaining;
            sid.ClockAndSample(ref cyclesRemaining, discard, 0, discard.Length);
            if (cyclesRemaining == before)
            {
                break; // no progress - bail rather than spin
            }
        }
    }
}
