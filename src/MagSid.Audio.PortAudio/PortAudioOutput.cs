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
    private short[] _mono = [];

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

    /// <summary>For tests: the device to open in place of the default, and the most channels to
    /// send it - as a one-eared headset takes.</summary>
    internal static Func<int>? DeviceOverride = null;
    internal static int? ChannelsLimit = null;

    /// <summary>For tests: a device whose callbacks are not counted, as one that never asks for sound.</summary>
    internal static string? DeafDevice = null;

    /// <summary>Channels the device is sent: the source's, or one where the device has only one - a
    /// one-eared headset, or a Bluetooth one in its hands-free mode - the two sides mixed into it.</summary>
    private volatile int _deviceChannels;

    /// <summary>The device the stream plays on.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>Where in the list of devices to try the stream is open: the next one is tried if
    /// this one never asks for sound.</summary>
    private int _candidateAt;

    /// <summary>
    /// A stream on the output device as PortAudio knows it now - the first of <see cref="Candidates"/>
    /// from <paramref name="from"/> on that opens - sent no more channels than it has, and tried
    /// again with more time in hand before the next is tried. One that opens nowhere says which
    /// device it was and why, in PortAudio's words.
    /// </summary>
    private PortAudioSharp.Stream OpenStream(int from = 0)
    {
        int device = DeviceOverride?.Invoke() ?? Pa.DefaultOutputDevice;
        if (device == Pa.NoDevice)
            throw new InvalidOperationException("There is no audio output device.");

        var candidates = Candidates(device);
        PortAudioException? first = null;
        string firstName = "";
        for (int k = from; k < candidates.Count; k++)
        {
            var info = Pa.GetDeviceInfo(candidates[k]);
            int channels = Math.Clamp(Math.Min(_source.WaveFormat.Channels, ChannelsLimit ?? int.MaxValue), 1, info.maxOutputChannels);
            double wanted = _latencyMs / 1000.0;
            foreach (double latency in (ReadOnlySpan<double>)[Math.Max(info.defaultLowOutputLatency, wanted), Math.Max(info.defaultHighOutputLatency, wanted * 2)])
            {
                try
                {
                    _deviceChannels = channels;
                    var stream = OpenOn(candidates[k], channels, latency);
                    DeviceName = info.name;
                    _candidateAt = k;
                    Say(first is null
                        ? $"playing on {info.name}, {channels} channel(s)"
                        : $"{firstName} would not open ({Pa.GetErrorText(first.ErrorCode)}); playing on {info.name}, {channels} channel(s)");
                    return stream;
                }
                catch (PortAudioException ex)
                {
                    if (first is null)
                        (first, firstName) = (ex, info.name);
                }
            }
        }
        if (first is null)
            throw new InvalidOperationException("No other output device would play.");
        var code = first.ErrorCode;
        var failure = new PortAudioException(code, $"{firstName} would not open - {Pa.GetErrorText(code)}", first);
        Say(failure.Message);
        throw failure;
    }

    /// <summary>
    /// The devices to try, in turn: the one asked for, then - on Windows - the sound mapper, which
    /// takes any format and converts it for the default device. On Linux PipeWire's and
    /// PulseAudio's own ALSA devices come first, where they are: ALSA's default reaches them only
    /// through its configuration, and a desktop sound server is what other programs play through.
    /// </summary>
    private static List<int> Candidates(int device)
    {
        var all = new List<(int Index, string Name)>();
        for (int d = 0; d < Pa.DeviceCount; d++)
        {
            var info = Pa.GetDeviceInfo(d);
            if (info.maxOutputChannels > 0)
                all.Add((d, info.name));
        }
        var order = new List<int>();
        void Add(int d)
        {
            if (d >= 0 && !order.Contains(d) && all.Any(a => a.Index == d))
                order.Add(d);
        }
        int Named(string name) => all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } found ? found.Index : -1;
        if (OperatingSystem.IsLinux() && DeviceOverride is null)
        {
            Add(Named("pipewire"));
            Add(Named("pulse"));
        }
        Add(device);
        if (OperatingSystem.IsLinux())
        {
            Add(Named("default"));
            Add(Named("sysdefault"));
        }
        foreach (var (index, name) in all)
            if (name.StartsWith("Microsoft Sound Mapper", StringComparison.Ordinal))
                Add(index);
        return order;
    }

    /// <summary>What happened to the sound, for anyone looking: the trace, and on Linux and macOS
    /// the terminal the app was started from - where a report of no sound can be copied from.</summary>
    private static void Say(string message)
    {
        Trace.WriteLine($"MagPlaySID: {message}");
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Console.Error.WriteLine($"MagPlaySID: {message}");
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Callbacks the stream has made since it was last started.</summary>
    private long _callbacks;

    /// <summary>Counts the stream's starts, so a check for one long gone does nothing.</summary>
    private int _generation;

    /// <summary>How long a started stream may go without asking for sound before the next device is tried.</summary>
    private static readonly TimeSpan SilentDevice = TimeSpan.FromSeconds(1.5);

    /// <summary>The stream started, and checked a moment later for having asked for sound at all. Under <see cref="InitLock"/>.</summary>
    private void StartStream()
    {
        Interlocked.Exchange(ref _callbacks, 0);
        _stream.Start();
        _started = true;
        int generation = ++_generation;
        _ = Task.Delay(SilentDevice).ContinueWith(_ => CheckAsking(generation), TaskScheduler.Default);
    }

    /// <summary>
    /// A stream that opened and started but has never asked for sound - a device a sound server
    /// holds, say, which takes the stream and then plays nothing - closed, and the next device
    /// tried; with none left, the output stops and says why, rather than the tune standing still
    /// with no word.
    /// </summary>
    private void CheckAsking(int generation)
    {
        Exception? stopped = null;
        lock (InitLock)
        {
            if (_disposed || generation != _generation || !_started || _stream is null || Interlocked.Read(ref _callbacks) > 0)
                return;
            string silent = DeviceName;
            _moving = true;
            try
            {
                _stream.Abort();
            }
            catch (PortAudioException)
            {
            }
            _stream.Dispose();
            _started = false;
            try
            {
                _stream = OpenStream(_candidateAt + 1);
                Say($"{silent} took the stream but never asked for sound; trying {DeviceName}");
                _moving = false;
                StartStream();
            }
            catch (Exception ex) when (ex is PortAudioException or InvalidOperationException)
            {
                _stream = null!;
                _moving = false;
                _failure = new IOException($"{silent} took the stream but never asked for any sound, and no other output device would play", ex);
                Say(_failure.Message);
                stopped = _failure;
            }
        }
        if (stopped is not null)
            Stopped?.Invoke(stopped);
    }

    private PortAudioSharp.Stream OpenOn(int device, int channels, double latency)
    {
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = latency,
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
                        output.StartStream();
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

            StartStream();
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
        if (DeafDevice is null || DeafDevice != DeviceName)
            Interlocked.Increment(ref _callbacks);
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
        int channels = _source.WaveFormat.Channels;
        if (_deviceChannels == channels || channels != 2)
        {
            Marshal.Copy(_buffer, 0, output, bytes);
        }
        else
        {
            // A device of one channel: the two sides mixed into it.
            if (_mono.Length < frameCount)
                _mono = new short[frameCount];
            MixToMono(MemoryMarshal.Cast<byte, short>(_buffer.AsSpan(0, bytes)), _mono);
            Marshal.Copy(_mono, 0, output, (int)frameCount);
        }

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

    /// <summary>Stereo samples, left and right in turn, mixed to one channel: the two halved and
    /// added, so a sound on one side only is as loud as on both at half.</summary>
    internal static void MixToMono(ReadOnlySpan<short> stereo, Span<short> mono)
    {
        int frames = Math.Min(stereo.Length / 2, mono.Length);
        for (int i = 0; i < frames; i++)
            mono[i] = (short)((stereo[2 * i] + stereo[(2 * i) + 1]) >> 1);
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
