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
///
/// It follows the system's own output: when the default device changes - headphones chosen in
/// place of the speakers, or the speakers unplugged - every stream is closed, PortAudio started
/// again so it knows the devices as they are now, and the streams opened on the new default, each
/// going on from where it was. See <see cref="DefaultDeviceWatcher"/>.
/// </summary>
public sealed class PortAudioOutput : IAudioOutput
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    /// <summary>The outputs open now: all of them closed and opened again when the device changes,
    /// as PortAudio can only learn of new devices with no stream open.</summary>
    private static readonly List<PortAudioOutput> Outputs = [];

    private readonly IWaveProvider _source;
    private readonly int _latencyMs;
    private PortAudioSharp.Stream _stream;

    /// <summary>Set while the stream is closed to move to another device: its end is not the
    /// output stopping.</summary>
    private volatile bool _moving;
    private bool _disposed;

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
    private bool _prioritised;

    private PortAudioOutput(IWaveProvider source, int latencyMs)
    {
        var format = source.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16)
            throw new ArgumentException("PortAudioOutput plays 16-bit PCM.", nameof(source));

        _source = source;
        _latencyMs = latencyMs;
        _blockAlign = format.BlockAlign;
        _sampleRate = format.SampleRate;
        _callback = Render;
        lock (InitLock)
        {
            EnsureInitialized();
            _stream = OpenStream();
            Outputs.Add(this);
        }
    }

    public static IAudioOutput Open(IWaveProvider source, int latencyMs) => new PortAudioOutput(source, latencyMs);

    /// <summary>A stream on the default output device as PortAudio knows it now.</summary>
    private PortAudioSharp.Stream OpenStream()
    {
        int device = Pa.DefaultOutputDevice;
        if (device == Pa.NoDevice)
            throw new InvalidOperationException("There is no audio output device.");

        var info = Pa.GetDeviceInfo(device);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = _source.WaveFormat.Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = Math.Max(info.defaultLowOutputLatency, _latencyMs / 1000.0),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };
        var stream = new PortAudioSharp.Stream(null, parameters, _sampleRate,
            Pa.FramesPerBufferUnspecified, StreamFlags.ClipOff, _callback, null!);
        stream.SetFinishedCallback(_ =>
        {
            if (!_moving)
                Stopped?.Invoke(_failure);
        });
        return stream;
    }

    private static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
                return;

            Pa.Initialize();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (InitLock)
                    Pa.Terminate();
            };
            _initialized = true;
            DefaultDeviceWatcher.Start(MoveToDefaultDevice);
        }
    }

    /// <summary>Called when the system's default output changes: every stream closed, PortAudio
    /// started again to know the devices as they are now, and every stream opened on the new
    /// default and started again if it was playing. One that cannot be opened there stays closed
    /// until the device changes again.</summary>
    public static void MoveToDefaultDevice()
    {
        lock (InitLock)
        {
            if (!_initialized)
                return;
            var outputs = Outputs.ToArray();
            var playing = new bool[outputs.Length];
            for (int i = 0; i < outputs.Length; i++)
            {
                var output = outputs[i];
                output._moving = true;
                playing[i] = output._started;
                try
                {
                    if (output._started)
                        output._stream.Stop();
                }
                catch (PortAudioException)
                {
                }
                output._stream.Dispose();
                output._started = false;
            }

            Pa.Terminate();
            Pa.Initialize();

            for (int i = 0; i < outputs.Length; i++)
            {
                var output = outputs[i];
                try
                {
                    output._stream = output.OpenStream();
                    if (playing[i])
                    {
                        output._stream.Start();
                        output._started = true;
                    }
                }
                catch (Exception ex) when (ex is PortAudioException or InvalidOperationException)
                {
                    // No device to play on: it waits, closed, for the next change.
                    output._stream = null!;
                    Trace.WriteLine($"MagPlaySID: cannot open the new output device: {ex.Message}");
                }
                output._moving = false;
            }
            Moved?.Invoke();
        }
    }

    /// <summary>The streams have moved to another device - for tests, and anyone who wants to know.</summary>
    public static event Action? Moved;

    public void Play()
    {
        lock (InitLock)
        {
            _paused = false;
            if (_started || _stream is null)
                return;

            _stream.Start();
            _started = true;
        }
    }

    public void Pause() => _paused = true;

    public void Stop()
    {
        lock (InitLock)
        {
            _paused = true;
            if (!_started || _stream is null)
                return;

            _stream.Stop();
            _started = false;
        }
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
        // The sound first: the device's thread raised above the window's and the background's on its
        // first call, so neither keeps it waiting till the device runs dry - only raised, as PortAudio
        // may have set it higher already. Where the system will not, it stays as it is.
        if (!_prioritised)
        {
            _prioritised = true;
            try
            {
                if (Thread.CurrentThread.Priority < ThreadPriority.Highest)
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException or System.Security.SecurityException)
            {
            }
        }

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
        lock (InitLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Outputs.Remove(this);
            if (_stream is null)
                return;
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
}
