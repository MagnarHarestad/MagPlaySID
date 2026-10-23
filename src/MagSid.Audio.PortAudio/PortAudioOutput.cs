using System.Diagnostics;
using System.Runtime.InteropServices;
using MagSid.Playback;
using NAudio.Wave;
using PortAudioSharp;
using Pa = PortAudioSharp.PortAudio;

namespace MagSid.Audio.PortAudio;

/// <summary>
/// The sound card on a Mac (Core Audio) or Linux (ALSA, PulseAudio), through PortAudio - and on
/// Windows too, where it runs on its own MME/WASAPI support. The native library ships inside the
/// PortAudioSharp2 package for each of those systems, so nothing has to be installed.
///
/// PortAudio asks for samples from its own thread, the way waveOut does: the callback reads the
/// provider for exactly what the device wants. Paused, it hands the device silence without reading,
/// so the engine stands still - the same as pausing waveOut.
/// </summary>
public sealed class PortAudioOutput : IAudioOutput
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    private readonly IWaveProvider _source;
    private readonly PortAudioSharp.Stream _stream;

    // Held for the stream's lifetime: PortAudio keeps only a native pointer to it, and a collected
    // delegate is a crash on the audio thread.
    private readonly PortAudioSharp.Stream.Callback _callback;

    private readonly int _blockAlign;
    private readonly int _sampleRate;
    private byte[] _buffer = [];

    private volatile bool _paused = true;
    private bool _started;
    private Exception? _failure;

    // Where the device was at the last callback: frames handed over before it, less those still
    // queued in front of the speaker, and when that was - SamplesPlayed runs on from there. The two
    // are written and read together under a lock: read apart, a new position with the old time ran
    // a whole callback ahead and then fell back, and the DAW's playhead flickered with it.
    private readonly object _clockLock = new();
    private long _framesRendered;
    private double _playedAtCallback;
    private long _callbackTicks;
    private long _lastReported;

    private PortAudioOutput(IWaveProvider source, int latencyMs)
    {
        var format = source.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16)
            throw new ArgumentException("PortAudioOutput plays 16-bit PCM.", nameof(source));

        EnsureInitialized();

        int device = Pa.DefaultOutputDevice;
        if (device == Pa.NoDevice)
            throw new InvalidOperationException("There is no audio output device.");

        var info = Pa.GetDeviceInfo(device);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = format.Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = Math.Max(info.defaultLowOutputLatency, latencyMs / 1000.0),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        _source = source;
        _blockAlign = format.BlockAlign;
        _sampleRate = format.SampleRate;
        _callback = Render;
        _stream = new PortAudioSharp.Stream(null, parameters, format.SampleRate,
            Pa.FramesPerBufferUnspecified, StreamFlags.ClipOff, _callback, null!);
        _stream.SetFinishedCallback(_ => Stopped?.Invoke(_failure));
    }

    public static IAudioOutput Open(IWaveProvider source, int latencyMs) => new PortAudioOutput(source, latencyMs);

    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
                return;

            Pa.Initialize();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Pa.Terminate();
            _initialized = true;
        }
    }

    public void Play()
    {
        _paused = false;
        if (_started)
            return;

        _stream.Start();
        _started = true;
    }

    public void Pause() => _paused = true;

    public void Stop()
    {
        _paused = true;
        if (!_started)
            return;

        _stream.Stop();
        _started = false;
    }

    public bool IsPlaying => _started && !_paused;

    /// <summary>Never goes backwards: a device's reported latency wanders, and a clock that steps
    /// back across a pattern's end turns the DAW's roll back to the previous clip.</summary>
    public long SamplesPlayed
    {
        get
        {
            lock (_clockLock)
            {
                double since = (Stopwatch.GetTimestamp() - _callbackTicks) / (double)Stopwatch.Frequency;
                double played = _playedAtCallback + (_paused ? 0 : since * _sampleRate);
                long now = (long)Math.Clamp(played, 0, _framesRendered);
                _lastReported = Math.Max(_lastReported, now);
                return _lastReported;
            }
        }
    }

    public event Action<Exception?>? Stopped;

    private StreamCallbackResult Render(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int bytes = (int)frameCount * _blockAlign;
        if (_buffer.Length < bytes)
            _buffer = new byte[bytes];

        int read = 0;
        if (!_paused)
        {
            try
            {
                read = _source.Read(_buffer, 0, bytes);
            }
            catch (Exception ex)
            {
                _failure = ex;
                return StreamCallbackResult.Abort;
            }
        }

        if (read < bytes)
            Array.Clear(_buffer, read, bytes - read);
        Marshal.Copy(_buffer, 0, output, bytes);

        if (!_paused)
        {
            // This buffer starts playing outputBufferDacTime - currentTime from now; everything
            // handed over before it has played except what that gap still holds.
            double queued = Math.Max(0, timeInfo.outputBufferDacTime - timeInfo.currentTime) * _sampleRate;
            lock (_clockLock)
            {
                long before = _framesRendered;
                _framesRendered += frameCount;
                _playedAtCallback = Math.Max(0, before - queued);
                _callbackTicks = Stopwatch.GetTimestamp();
            }
        }

        return StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        try
        {
            if (_started)
                _stream.Stop();
        }
        catch (PortAudioException)
        {
        }

        _stream.Dispose();
    }
}
