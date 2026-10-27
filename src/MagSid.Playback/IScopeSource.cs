namespace MagSid.Playback;

/// <summary>
/// What a spectrum or a scope draws from: an engine's last samples - each voice's own output and
/// the mix - ending at the sample the listener is hearing. <see cref="SidFilePlaybackEngine"/> is
/// one; anything else that renders a SID can be too.
/// </summary>
public interface IScopeSource
{
    /// <summary>The <see cref="SidFilePlaybackEngine.ScopeSamples"/> samples of one voice's own
    /// output ending at <paramref name="endingAt"/>, scaled so a full-amplitude voice swings by 2.</summary>
    void CaptureVoiceScope(int voice, float[] destination, long endingAt);

    /// <summary>The <see cref="SidFilePlaybackEngine.ScopeSamples"/> samples of the mix ending at
    /// <paramref name="endingAt"/>.</summary>
    void CaptureMix(float[] destination, long endingAt);
}
