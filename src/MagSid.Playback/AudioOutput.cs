using NAudio.Wave;

namespace MagSid.Playback;

/// <summary>
/// A sound card, playing what an <see cref="IWaveProvider"/> renders. The engines here only produce
/// samples; which device and which API take them is the host's business - WinMM's waveOut on
/// Windows, PortAudio (Core Audio, ALSA) on a Mac or Linux - so the engines run the same everywhere.
/// </summary>
public interface IAudioOutput : IDisposable
{
    void Play();

    void Pause();

    void Stop();

    bool IsPlaying { get; }

    /// <summary>Samples the device has actually played, as near as it can say - behind what the
    /// provider has rendered by however much is still queued. Drawing at this position keeps a
    /// playhead on the music rather than ahead of it.</summary>
    long SamplesPlayed { get; }

    /// <summary>The device stopped by itself - the provider ran dry, or the device failed (the
    /// exception, when there is one).</summary>
    event Action<Exception?>? Stopped;
}

/// <summary>Where the engines get their output from. Set once by the host at start-up.</summary>
public static class AudioOutput
{
    /// <summary>Opens the default output device for <paramref name="source"/>, with about
    /// <paramref name="latencyMs"/> of buffering, ready to <see cref="IAudioOutput.Play"/>.</summary>
    public static Func<IWaveProvider, int, IAudioOutput>? Open { get; set; }

    public static IAudioOutput Start(IWaveProvider source, int latencyMs)
    {
        var open = Open ?? throw new InvalidOperationException(
            "No audio output has been set up - the application sets AudioOutput.Open when it starts.");
        var output = open(source, latencyMs);
        output.Play();
        return output;
    }
}
