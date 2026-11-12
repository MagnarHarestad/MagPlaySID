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

    private static readonly object DeckLock = new();
    private static Deck? _deck;

    /// <summary>
    /// <paramref name="source"/> played on the one stream kept open for it and every source after
    /// it - opening a device's stream takes the best part of half a second, and stopping one waits
    /// for what it holds to play out: a tune after tune each with a stream of its own held the
    /// window that long at every change. Stopped, it hands the stream over, at once, to whatever
    /// comes next, and silence till then. The stream is opened off the caller's thread: a source
    /// taking the first turn plays once it is, and one that cannot be opened is told by its output's
    /// Stopped. 16-bit mono or stereo; anything else, or a rate the stream is not at, gets a stream
    /// of its own.
    /// </summary>
    public static IAudioOutput Share(IWaveProvider source, int latencyMs)
    {
        var format = source.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels is not (1 or 2))
            return Start(source, latencyMs);
        lock (DeckLock)
        {
            if (_deck is null || _deck.Failed || _deck.WaveFormat.SampleRate != format.SampleRate)
            {
                _deck?.Close();
                _deck = null;
                var deck = new Deck(format.SampleRate);
                deck.OpenSoon(latencyMs);
                _deck = deck;
            }
            return _deck.Take(source);
        }
    }

    /// <summary>One stereo stream played without a stop: the source taking its turn read into it -
    /// a mono one to both sides - and silence between turns.</summary>
    private sealed class Deck(int sampleRate) : IWaveProvider
    {
        private readonly object _lock = new();
        private volatile IAudioOutput? _device;
        private Turn? _current;
        private long _delivered;
        private byte[] _mono = [];

        public WaveFormat WaveFormat { get; } = new(sampleRate, 16, 2);

        /// <summary>The stream stopped by itself - its device gone: the next source opens another.</summary>
        public bool Failed { get; private set; }

        /// <summary>The stream, once it is open.</summary>
        public IAudioOutput? Device => _device;

        public void OpenSoon(int latencyMs)
        {
            // A device that neither opens nor fails - its driver waiting on a sound server that
            // holds it - leaves the tune standing still with no word: after a while, it is told.
            _ = Task.Delay(OpenAtMost).ContinueWith(_ =>
            {
                if (_device is null && !Failed && !_closed)
                    FailCurrent(new TimeoutException($"the sound device has not opened in {OpenAtMost.TotalSeconds:0} seconds - another program may be holding it"));
            }, TaskScheduler.Default);
            OpenNow(latencyMs);
        }

        /// <summary>How long a device may take to open before the tune waiting for it is told.</summary>
        private static readonly TimeSpan OpenAtMost = TimeSpan.FromSeconds(8);

        private void OpenNow(int latencyMs) => ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var device = Start(this, latencyMs);
                device.Stopped += _ =>
                {
                    Failed = true;
                    FailCurrent(null);
                };
                lock (_lock)
                {
                    _device = device;
                    if (_closed)
                    {
                        device.Stop();
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                // No device, or none that will open: the turn that waits for it is told, and the
                // next source tries again.
                Failed = true;
                FailCurrent(ex);
            }
        });

        private bool _closed;

        private void FailCurrent(Exception? ex)
        {
            Turn? current;
            lock (_lock)
                current = _current;
            current?.Fail(ex);
        }

        public void Close()
        {
            IAudioOutput? device;
            lock (_lock)
            {
                _closed = true;
                device = _device;
            }
            device?.Stop();
            device?.Dispose();
        }

        public Turn Take(IWaveProvider source)
        {
            var turn = new Turn(this, source);
            lock (_lock)
                _current = turn;
            if (Failed)
                turn.Fail(null);
            return turn;
        }

        /// <summary>The turn over: once this returns, its source is read no more.</summary>
        public void Release(Turn turn)
        {
            lock (_lock)
            {
                if (_current == turn)
                    _current = null;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 4;
            count = frames * 4;
            lock (_lock)
            {
                var turn = _current;
                int read = 0;
                if (turn is not null)
                {
                    if (turn.StartedAt < 0)
                        turn.StartedAt = _delivered;
                    try
                    {
                        read = turn.Source.WaveFormat.Channels == 2 ? ReadStereo(turn.Source, buffer, offset, count) : ReadMono(turn.Source, buffer, offset, frames);
                    }
                    catch (Exception ex)
                    {
                        // A source that fails ends its own turn, not the stream every turn plays on.
                        _current = null;
                        read = 0;
                        ThreadPool.QueueUserWorkItem(_ => turn.Fail(ex));
                    }
                }
                if (read < count)
                    Array.Clear(buffer, offset + read, count - read);
                _delivered += frames;
            }
            return count;
        }

        private static int ReadStereo(IWaveProvider source, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int got = source.Read(buffer, offset + read, count - read);
                if (got <= 0)
                    break;
                read += got;
            }
            return read - (read % 4);
        }

        private int ReadMono(IWaveProvider source, byte[] buffer, int offset, int frames)
        {
            int bytes = frames * 2;
            if (_mono.Length < bytes)
                _mono = new byte[bytes];
            int read = 0;
            while (read < bytes)
            {
                int got = source.Read(_mono, read, bytes - read);
                if (got <= 0)
                    break;
                read += got;
            }
            int samples = read / 2;
            for (int i = 0; i < samples; i++)
            {
                byte low = _mono[2 * i], high = _mono[(2 * i) + 1];
                int at = offset + (4 * i);
                buffer[at] = low;
                buffer[at + 1] = high;
                buffer[at + 2] = low;
                buffer[at + 3] = high;
            }
            return samples * 4;
        }
    }

    /// <summary>A source's turn on the shared stream, as an output of its own: its samples counted
    /// from where its sound began on the stream, as a stream opened for it would count them.</summary>
    private sealed class Turn(Deck deck, IWaveProvider source) : IAudioOutput
    {
        private bool _over;

        public IWaveProvider Source { get; } = source;

        /// <summary>Where on the stream its first sample went; -1 till then. Set under the deck's lock.</summary>
        public long StartedAt { get; set; } = -1;

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Stop()
        {
            _over = true;
            deck.Release(this);
        }

        public bool IsPlaying => !_over && !deck.Failed;

        public long SamplesPlayed
        {
            get
            {
                long started = StartedAt;
                return started < 0 || deck.Device is not { } device ? 0 : Math.Max(0, device.SamplesPlayed - started);
            }
        }

        private Action<Exception?>? _stopped;
        private bool _failed;
        private Exception? _failure;

        /// <summary>Told when the stream fails it - at once, to one who listens after it already has:
        /// the stream is opened on another thread, and may fail before anyone listens.</summary>
        public event Action<Exception?>? Stopped
        {
            add
            {
                bool failed;
                lock (this)
                {
                    _stopped += value;
                    failed = _failed;
                }
                if (failed)
                    value?.Invoke(_failure);
            }
            remove
            {
                lock (this)
                    _stopped -= value;
            }
        }

        public void Fail(Exception? ex)
        {
            Action<Exception?>? stopped;
            lock (this)
            {
                if (_over || _failed)
                    return;
                (_failed, _failure, _over) = (true, ex, true);
                stopped = _stopped;
            }
            stopped?.Invoke(ex);
        }

        public void Dispose() => Stop();
    }
}
