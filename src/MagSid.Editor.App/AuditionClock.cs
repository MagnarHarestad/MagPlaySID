using NAudio.Wave;

namespace MagSid.Editor.App;

/// <summary>
/// Reads how far a live NAudio playback has actually reached, for driving a UI playhead.
/// MainForm's tracker-grid scroll, SignalPathForm's trace marker and DigiToSidOscForm's trace
/// marker each need exactly this number and had separately arrived at the same non-obvious
/// answer, so it lives here once instead of three times.
/// </summary>
internal static class AuditionClock
{
    /// <summary>
    /// The device's own play position, in samples - not <c>SongPlaybackEngine.SamplesProduced</c>,
    /// which only advances once per buffer refill (measured ~10-11 times a second against
    /// WaveOutEvent's default 300ms latency, versus ~63 times a second for this). Produced samples
    /// are ones already handed to the sound card, up to 300ms before anyone hears them; this is
    /// what is coming out of the speakers right now.
    ///
    /// Returns null if the device is mid-teardown and throws querying it - a stale frame is not
    /// worth crashing the UI over.
    /// </summary>
    public static long? SamplesPlayed(WaveOutEvent output, IWaveProvider engine)
    {
        try
        {
            return output.GetPosition() / engine.WaveFormat.BlockAlign;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
