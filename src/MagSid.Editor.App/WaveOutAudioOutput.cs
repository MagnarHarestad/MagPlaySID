using MagSid.Playback;
using NAudio.Wave;

namespace MagSid.Editor.App;

/// <summary>The Windows editor's sound card: WinMM's waveOut, as it always played through.</summary>
public sealed class WaveOutAudioOutput : IAudioOutput
{
    private readonly WaveOutEvent _output;
    private readonly int _sampleRate;

    private WaveOutAudioOutput(IWaveProvider source, int latencyMs)
    {
        _output = new WaveOutEvent { DesiredLatency = latencyMs, NumberOfBuffers = 3 };
        _output.Init(source);
        _sampleRate = source.WaveFormat.SampleRate;
        _output.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public static IAudioOutput Open(IWaveProvider source, int latencyMs) => new WaveOutAudioOutput(source, latencyMs);

    public void Play() => _output.Play();

    public void Pause() => _output.Pause();

    public void Stop() => _output.Stop();

    public bool IsPlaying => _output.PlaybackState == PlaybackState.Playing;

    public long SamplesPlayed => _output.GetPosition() / _output.OutputWaveFormat.BlockAlign;

    public event Action<Exception?>? Stopped;

    public void Dispose() => _output.Dispose();
}
