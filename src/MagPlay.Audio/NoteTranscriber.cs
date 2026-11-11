namespace MagPlay.Audio;

/// <summary>A note heard in a recording: its key, MIDI's numbering (60 middle C), when it starts and
/// ends in seconds, and how strongly it sounds, 0-1.</summary>
public readonly record struct HeardNote(int Pitch, double Start, double End, float Strength);

/// <summary>
/// The notes of a recording, many at once, heard from its sound alone. Each frame - 11.6 ms apart -
/// the spectrum of the last 186 ms is whitened, and the fundamental whose harmonics sum the most
/// found; its harmonics are taken away - each no more than the smooth run of its neighbours, so a
/// harmonic another note shares is left to it - and the next found, until one more would add less
/// than it costs (after Klapuri, "Multiple fundamental frequency estimation by summing harmonic
/// amplitudes", 2006). A key sounding over frames is a note; it starts at the onset - a sudden rise
/// in the sound, from a short window - nearest where it is first heard, and a note struck again on
/// itself is split where its own harmonics rise again.
/// </summary>
public sealed class NoteTranscriber
{
    public const int Rate = 22050;
    public const int Hop = 256;
    private const int Window = 4096;
    private const int Fft = 8192;
    private const int ShortWindow = 1024;

    /// <summary>The window keys from <see cref="SplitKey"/> up are heard in: 93 ms, quick enough for
    /// a fast run, where the bass needs the long one to tell its semitones apart.</summary>
    private const int MediumWindow = 2048;
    private const int Bins = (Fft / 2) + 1;

    /// <summary>The keys listened for: A0 to C7. Above, a melody's fundamental is rare and its
    /// harmonics are another key's.</summary>
    public const int LowestPitch = 21, HighestPitch = 96;
    private const int Keys = HighestPitch - LowestPitch + 1;

    private const int StepsPerSemitone = 5;
    private const int MostVoices = 8;
    private const int MostHarmonics = 20;
    private const double TopFrequency = 6000;

    /// <summary>Seconds a frame is.</summary>
    public const double FrameSeconds = (double)Hop / Rate;

    // Tuning, found on pieces of known notes.
    internal static float CancelDepth = 0.75f;
    internal static float StopBelow = 0.12f;
    internal static double PolyphonyPower = 0.55;
    internal static double EndShift = -0.075;
    internal static double OnsetShift = -0.023;
    // No key heard in the medium window: it lost more notes in chords than it found in fast runs.
    internal static int SplitKey = HighestPitch + 1;
    internal static double MediumEndShift = -0.035;

    private readonly float[] _window = MakeHann(Window);
    private readonly float[] _mediumWindow = MakeHann(MediumWindow);
    private readonly float[] _shortWindow = MakeHann(ShortWindow);
    private readonly RealFft _fft = new(Fft);
    private readonly RealFft _shortFft = new(ShortWindow);
    private readonly float[] _signal = new float[Fft];
    private readonly float[] _shortSignal = new float[ShortWindow];
    // The long window's spectrum [0] and the medium's [1]: as they are, whitened, and what is left of
    // them as fundamentals are found.
    private readonly float[][] _magnitudes = [new float[Bins], new float[Bins]];
    private readonly float[][] _whites = [new float[Bins], new float[Bins]];
    private readonly float[][] _residuals = [new float[Bins], new float[Bins]];
    private float[] _magnitude => _magnitudes[0];

    /// <summary>Which spectrum each candidate is heard in.</summary>
    private int[] _spectrumOf = [];
    private readonly float[] _shortMagnitude = new float[(ShortWindow / 2) + 1];
    private readonly float[] _shortLogBefore = new float[(ShortWindow / 2) + 1];

    // The candidates: a fundamental each fifth of a semitone, and the bins each of its harmonics may be in.
    private readonly double[] _candidates;
    private readonly int[][] _bandLow, _bandHigh;
    private readonly float[][] _weights;
    private readonly float[] _partials = new float[MostHarmonics];
    private readonly int[] _partialAt = new int[MostHarmonics];

    // Whitening: the ERB bands' centres, and for each bin the band below it and how far towards the next.
    private readonly int[] _bandCentres;
    private readonly int[] _binBand = new int[Bins];
    private readonly float[] _binTowards = new float[Bins];
    private readonly float[] _bandGain;

    // Each key's first three harmonics' bins, for its own loudness - in the long window and the short.
    private readonly int[][] _keyBins = new int[Keys][];
    private readonly int[][] _keyShortBins = new int[Keys][];

    public NoteTranscriber()
    {
        int count = ((HighestPitch - LowestPitch) * StepsPerSemitone) + 1;
        _candidates = new double[count];
        _bandLow = new int[count][];
        _bandHigh = new int[count][];
        _weights = new float[count][];
        double binHz = (double)Rate / Fft;
        for (int i = 0; i < count; i++)
        {
            double f0 = 440 * Math.Pow(2, ((LowestPitch + ((double)i / StepsPerSemitone)) - 69) / 12);
            _candidates[i] = f0;
            int harmonics = Math.Min(MostHarmonics, (int)(TopFrequency / f0));
            _bandLow[i] = new int[harmonics];
            _bandHigh[i] = new int[harmonics];
            _weights[i] = new float[harmonics];
            for (int m = 1; m <= harmonics; m++)
            {
                // Half a candidate step either side - and above, as far as a string's partials run
                // sharp: a piano's are 2% high by the twentieth.
                double spread = Math.Pow(2, 0.5 / StepsPerSemitone / 12);
                _bandLow[i][m - 1] = Math.Max(1, (int)Math.Floor(m * f0 / spread / binHz));
                _bandHigh[i][m - 1] = Math.Min(Fft / 2, (int)Math.Ceiling(m * f0 * spread * (1 + (0.00005 * m * m)) / binHz));
                _weights[i][m - 1] = (float)((f0 + 52) / ((m * f0) + 320));
            }
        }

        var centres = new List<int>();
        for (int b = 0; ; b++)
        {
            double hz = 229 * (Math.Pow(10, (b + 1) / 21.4) - 1);
            if (hz > TopFrequency * 1.2)
                break;
            centres.Add(Math.Max(1, (int)Math.Round(hz / binHz)));
        }
        _bandCentres = [.. centres.Distinct()];
        _spectrumOf = new int[count];
        // Which window each candidate is heard in is read once, as made.
        for (int i = 0; i < count; i++)
            _spectrumOf[i] = LowestPitch + ((double)i / StepsPerSemitone) >= SplitKey - 0.5 ? 1 : 0;
        _bandGain = new float[_bandCentres.Length];
        for (int k = 0, b = 0; k < Bins; k++)
        {
            while (b + 1 < _bandCentres.Length && _bandCentres[b + 1] <= k)
                b++;
            _binBand[k] = b;
            _binTowards[k] = k <= _bandCentres[0] || b + 1 >= _bandCentres.Length ? 0
                : (float)(k - _bandCentres[b]) / (_bandCentres[b + 1] - _bandCentres[b]);
        }

        for (int key = 0; key < Keys; key++)
        {
            double f0 = 440 * Math.Pow(2, (LowestPitch + key - 69) / 12.0);
            _keyBins[key] = Enumerable.Range(1, 3).Select(m => (int)Math.Round(m * f0 / binHz)).Where(b => b + 1 < Bins).ToArray();
            double shortHz = (double)Rate / ShortWindow;
            _keyShortBins[key] = Enumerable.Range(1, 4).Select(m => (int)Math.Round(m * f0 / shortHz)).Where(b => b >= 1 && b + 1 <= ShortWindow / 2).Distinct().ToArray();
        }
    }

    private static float[] MakeHann(int length)
    {
        var w = new float[length];
        for (int i = 0; i < length; i++)
            w[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / length)));
        return w;
    }

    /// <summary>
    /// The whole recording transcribed, read from <paramref name="decoder"/>: <paramref name="progress"/>
    /// is told every few seconds of it the notes so far - in order of their start - and how far it
    /// has heard, in seconds.
    /// </summary>
    public static List<HeardNote> Transcribe(AudioDecoder decoder, Action<IReadOnlyList<HeardNote>, double>? progress = null,
                                             CancellationToken cancel = default)
    {
        var transcriber = new NoteTranscriber();
        return transcriber.Run(decoder, progress, cancel);
    }

    /// <summary>What a frame heard: each key's salience - 0 where not found - and the short window's
    /// onset strength, and each key's loudness for the splitting of struck-again notes.</summary>
    private sealed class Frame
    {
        public readonly float[] Keys = new float[NoteTranscriber.Keys];
        public readonly float[] KeyEnergy = new float[NoteTranscriber.Keys];

        /// <summary>Each key's first harmonics in the short window: quick to rise when it is struck.</summary>
        public readonly float[] KeyAttack = new float[NoteTranscriber.Keys];

        /// <summary>Each key's fundamental alone in the short window: a note struck an octave above
        /// raises the key's harmonics, not this.</summary>
        public readonly float[] RootAttack = new float[NoteTranscriber.Keys];
        public float Onset;
        public float Loudness;
    }

    /// <summary>Threads the frames are heard on, side by side: half the cores, at most four - the
    /// rest kept for the sound, the window and the app starting up, as a recording opened with the
    /// app is heard while both start.</summary>
    internal static int Workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Frames a worker hears at a time: three seconds.</summary>
    private const int BlockFrames = 256;

    /// <summary>
    /// The recording read and resampled here, in order; its frames heard in blocks on worker threads
    /// side by side - each frame stands on its own samples, but for the short window's onset, which
    /// is the rise over the frame before, so a block hears the frame before it first and puts it
    /// aside - and made into notes here, in order, as the blocks come back. The notes are the same
    /// as one thread's, frame for frame, in a fraction of the time.
    /// </summary>
    private List<HeardNote> Run(AudioDecoder decoder, Action<IReadOnlyList<HeardNote>, double>? progress, CancellationToken cancel)
    {
        var resampler = new Resampler(decoder.SampleRate, Rate, 1);
        int channels = decoder.Channels;
        var read = new float[8192 * channels];
        var mono = new float[8192];
        var resampled = new float[16384];

        // The samples not yet given to a block, from the absolute sample `kept` on.
        var store = new List<float>(1 << 16);
        long kept = 0, taken = 0;
        long scheduled = 0;
        var frames = new List<Frame>();
        var notes = new List<HeardNote>();
        var tracker = new Tracker();
        double reported = 0;
        var sinceReport = System.Diagnostics.Stopwatch.StartNew();

        // Not disposed: the workers may still be finishing a block as this returns; finished adding, they end.
        var work = new System.Collections.Concurrent.BlockingCollection<(float[] Samples, int Count, TaskCompletionSource<Frame[]> Done)>();
        var threads = new List<Thread>();
        for (int w = 0; w < Workers; w++)
        {
            var thread = new Thread(() =>
            {
                var ear = this;
                lock (threads)
                {
                    // Each thread its own buffers: the first takes this one's, the rest new ones.
                    if (threads.IndexOf(Thread.CurrentThread) > 0)
                        ear = new NoteTranscriber();
                }
                foreach (var (samples, count, done) in work.GetConsumingEnumerable())
                {
                    try
                    {
                        // The frame before the block first, for the onset's rise; then the block.
                        Analyse(ear, samples.AsSpan(0, Window), new Frame());
                        var heard = new Frame[count];
                        for (int f = 0; f < count; f++)
                        {
                            heard[f] = new Frame();
                            Analyse(ear, samples.AsSpan((f + 1) * Hop, Window), heard[f]);
                        }
                        System.Buffers.ArrayPool<float>.Shared.Return(samples);
                        done.SetResult(heard);
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Hearing notes" };
            lock (threads)
                threads.Add(thread);
        }
        foreach (var thread in threads)
            thread.Start();

        var inFlight = new Queue<Task<Frame[]>>();

        // The oldest block back: its frames made into notes, in order.
        void TakeBack()
        {
            var heard = inFlight.Dequeue().GetAwaiter().GetResult();
            foreach (var frame in heard)
            {
                frames.Add(frame);
                tracker.Add(frames, notes);
            }
        }

        // Every whole block heard so far given to a worker - and at the end, what is left.
        void Schedule(bool end)
        {
            long ready = taken / Hop;
            while (ready - scheduled >= BlockFrames || (end && scheduled < ready))
            {
                int count = (int)Math.Min(BlockFrames, ready - scheduled);
                // From the start of the frame before's window to the end of the block's last.
                long from = (scheduled * Hop) - Window;
                // Three seconds of samples a block: from a pool, not made new - an array this big is
                // only let go of by a full collection, and one a block made one every few blocks,
                // each a pause of every thread, the sound's among them.
                int length = (count * Hop) + Window;
                var samples = System.Buffers.ArrayPool<float>.Shared.Rent(length);
                Array.Clear(samples, 0, length);
                for (long at = Math.Max(from, kept); at < from + length; at++)
                    samples[at - from] = store[(int)(at - kept)];
                var done = new TaskCompletionSource<Frame[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                work.Add((samples, count, done), cancel);
                inFlight.Enqueue(done.Task);
                scheduled += count;

                // What no block will need again let go.
                long keepFrom = (scheduled * Hop) - Window;
                if (keepFrom > kept)
                {
                    store.RemoveRange(0, (int)(keepFrom - kept));
                    kept = keepFrom;
                }
                while (inFlight.Count > 0 && (inFlight.Peek().IsCompleted || inFlight.Count > Workers * 2))
                    TakeBack();
            }
        }

        void Take(ReadOnlySpan<float> samples)
        {
            foreach (float sample in samples)
                store.Add(sample);
            taken += samples.Length;
            Schedule(end: false);
        }

        try
        {
            int got;
            while ((got = decoder.Read(read)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                int frameCount = got / channels;
                if (mono.Length < frameCount)
                    mono = new float[frameCount];
                for (int i = 0; i < frameCount; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += read[(i * channels) + c];
                    mono[i] = sum / channels;
                }
                int made = resampler.Process(mono.AsSpan(0, frameCount), ref resampled);
                Take(resampled.AsSpan(0, made));

                double heard = frames.Count * FrameSeconds;
                // The notes so far shown now and then: every four seconds heard, but not more than
                // twice a second - each a copy of them all, and the window's to take in.
                if (progress is not null && heard - reported >= 4 && sinceReport.ElapsedMilliseconds >= 500)
                {
                    reported = heard;
                    sinceReport.Restart();
                    progress(Snapshot(notes, tracker.Pending(frames)), heard);
                }
            }
            int last = resampler.Flush(ref resampled);
            Take(resampled.AsSpan(0, last));
            // Half a window of silence, so the last notes are heard to their end.
            Take(new float[Window / 2]);
            Schedule(end: true);
            while (inFlight.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                TakeBack();
            }
        }
        finally
        {
            work.CompleteAdding();
        }
        tracker.Finish(frames, notes);
        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        progress?.Invoke(notes, frames.Count * FrameSeconds);
        return notes;
    }

    private static void Analyse(NoteTranscriber ear, ReadOnlySpan<float> samples, Frame frame) => ear.Analyse(samples, frame);

    private static List<HeardNote> Snapshot(List<HeardNote> notes, IEnumerable<HeardNote> pending)
    {
        var all = new List<HeardNote>(notes);
        all.AddRange(pending);
        all.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
        return all;
    }

    // ------------------------------------------------------------------ a frame

    private void Analyse(ReadOnlySpan<float> samples, Frame frame)
    {
        // The long window, its newest samples last, zero-padded to the transform's length.
        int offset = Window - samples.Length;
        double energy = 0;
        Array.Clear(_signal);
        for (int i = offset; i < Window; i++)
        {
            float value = samples[i - offset] * _window[i];
            _signal[i] = value;
            energy += value * value;
        }
        frame.Loudness = (float)Math.Sqrt(energy / Window);
        _fft.Magnitudes(_signal, _magnitudes[0]);

        // The medium window at the same end, as loud for a steady tone: its window sums to half as much.
        bool medium = SplitKey <= HighestPitch;
        Array.Clear(_signal);
        int mediumFrom = Window - MediumWindow;
        for (int i = 0; i < MediumWindow; i++)
        {
            int at = mediumFrom + i;
            _signal[at] = at >= offset ? samples[at - offset] * _mediumWindow[i] * 2 : 0;
        }
        if (medium)
            _fft.Magnitudes(_signal, _magnitudes[1]);

        // The short window at the same end: the onset strength, a rise in log magnitude over the
        // frame before's, anywhere in the spectrum.
        int shortFrom = samples.Length - ShortWindow;
        for (int i = 0; i < ShortWindow; i++)
            _shortSignal[i] = shortFrom + i >= 0 ? samples[shortFrom + i] * _shortWindow[i] : 0;
        _shortFft.Magnitudes(_shortSignal, _shortMagnitude);
        float onset = 0, previous = 0;
        for (int k = 1; k <= ShortWindow / 2; k++)
        {
            float log = MathF.Log(1 + (100 * _shortMagnitude[k]));
            // Against the frame before's at this bin and its neighbours - vibrato is not an onset.
            float before = Math.Max(_shortLogBefore[k], Math.Max(previous, k < ShortWindow / 2 ? _shortLogBefore[k + 1] : 0));
            onset += Math.Max(0, log - before);
            previous = _shortLogBefore[k];
            _shortLogBefore[k] = log;
        }
        frame.Onset = onset;
        for (int key = 0; key < Keys; key++)
        {
            float sum = 0;
            foreach (int bin in _keyShortBins[key])
                sum += Math.Max(_shortMagnitude[bin], Math.Max(_shortMagnitude[bin - 1], _shortMagnitude[bin + 1]));
            frame.KeyAttack[key] = sum;
            if (_keyShortBins[key].Length > 0)
            {
                int root = _keyShortBins[key][0];
                frame.RootAttack[key] = Math.Max(_shortMagnitude[root], Math.Max(_shortMagnitude[root - 1], _shortMagnitude[root + 1]));
            }
        }

        if (frame.Loudness < 1e-4f)
            return;

        Whiten(_magnitudes[0], _whites[0]);
        if (medium)
            Whiten(_magnitudes[1], _whites[1]);
        EstimateFundamentals(frame);

        // Each key's own loudness: its first harmonics in the long window.
        for (int key = 0; key < Keys; key++)
        {
            float sum = 0;
            foreach (int bin in _keyBins[key])
                sum += Math.Max(_magnitude[bin], Math.Max(_magnitude[bin - 1], _magnitude[bin + 1]));
            frame.KeyEnergy[key] = sum;
        }
    }

    /// <summary>The spectrum's broad shape taken out, so a quiet high partial counts beside a loud
    /// low one: each ERB band's level raised to the power -0.67, interpolated between band centres.</summary>
    private void Whiten(float[] _magnitude, float[] _white)
    {
        int bands = _bandCentres.Length;
        for (int b = 0; b < bands; b++)
        {
            int low = b == 0 ? 1 : _bandCentres[b - 1], centre = _bandCentres[b], high = b + 1 < bands ? _bandCentres[b + 1] : Math.Min(Fft / 2, (2 * centre) - low);
            double sum = 0, weights = 0;
            for (int k = low; k <= high && k <= Fft / 2; k++)
            {
                double w = k <= centre ? (double)(k - low + 1) / (centre - low + 1) : (double)(high - k + 1) / (high - centre + 1);
                sum += w * _magnitude[k] * _magnitude[k];
                weights += w;
            }
            double sigma = Math.Sqrt(sum / Math.Max(1, weights));
            _bandGain[b] = (float)Math.Pow(Math.Max(sigma, 1e-9), 0.33 - 1);
        }
        for (int k = 0; k < Bins; k++)
        {
            int b = _binBand[k];
            float g = b + 1 < bands ? _bandGain[b] + (_binTowards[k] * (_bandGain[b + 1] - _bandGain[b])) : _bandGain[b];
            _white[k] = g * _magnitude[k];
        }
    }

    /// <summary>One fundamental after another: the most salient; its partials taken from the
    /// spectrum, each no more than the smooth run of its own partials says it is; again - until the
    /// sum of saliences over the count to the 0.7th stops growing.</summary>
    private void EstimateFundamentals(Frame frame)
    {
        bool medium = SplitKey <= HighestPitch;
        _whites[0].AsSpan().CopyTo(_residuals[0]);
        _whites[1].AsSpan().CopyTo(_residuals[1]);
        double total = 0, bestScore = 0;
        float first = 0;
        for (int voice = 1; voice <= MostVoices; voice++)
        {
            if (!medium)
                BuildMaxima(_residuals[0]);
            int best = -1;
            float bestSalience = 0;
            for (int i = 0; i < _candidates.Length; i++)
            {
                float s = Salience(i, medium);
                if (s > bestSalience)
                    (best, bestSalience) = (i, s);
            }
            if (best < 0)
                break;
            if (voice == 1)
                first = bestSalience;

            double score = (total + bestSalience) / Math.Pow(voice, PolyphonyPower);
            if (score <= bestScore || bestSalience < first * StopBelow)
                break;
            total += bestSalience;
            bestScore = score;

            int key = (int)Math.Round((double)best / StepsPerSemitone);
            frame.Keys[key] = Math.Max(frame.Keys[key], bestSalience);
            Cancel(best, _residuals[0], 4);
            if (medium)
                Cancel(best, _residuals[1], 8);
        }
    }


    /// <summary>A candidate's salience in what is left of the spectrum: its partials' peaks, weighted.</summary>
    private float Salience(int i, bool medium)
    {
        float s = 0;
        var low = _bandLow[i];
        var high = _bandHigh[i];
        var weights = _weights[i];
        var residual = _residuals[_spectrumOf[i]];
        for (int m = 0; m < low.Length; m++)
        {
            float peak;
            if (!medium)
                peak = MaxIn(low[m], high[m]);
            else
            {
                peak = 0;
                for (int k = low[m]; k <= high[m]; k++)
                    if (residual[k] > peak)
                        peak = residual[k];
            }
            s += weights[m] * peak;
        }
        return s;
    }

    // The residual's maxima over every power-of-two run of bins: the loudest bin in any band is
    // then two looks, not a scan of the band.
    private const int Levels = 9;

    /// <summary>The bins a harmonic is looked for in: up to the top frequency, and its band's reach.</summary>
    private const int ListenedBins = (int)(TopFrequency * 1.1 / Rate * Fft) + 1;
    private readonly float[][] _maxima = Enumerable.Range(0, Levels).Select(_ => new float[Bins]).ToArray();
    private static readonly int[] FloorLog2 = Enumerable.Range(0, Bins + 1).Select(n => n < 1 ? 0 : (int)Math.Log2(n)).ToArray();

    private void BuildMaxima(float[] residual)
    {
        residual.AsSpan(0, ListenedBins + 256).CopyTo(_maxima[0]);
        for (int level = 1; level < Levels; level++)
        {
            int half = 1 << (level - 1), length = 1 << level;
            var below = _maxima[level - 1];
            var here = _maxima[level];
            for (int k = 0; k + length <= ListenedBins; k++)
                here[k] = Math.Max(below[k], below[k + half]);
        }
    }

    /// <summary>The loudest of bins <paramref name="low"/> to <paramref name="high"/> of the residual.</summary>
    private float MaxIn(int low, int high)
    {
        int level = FloorLog2[high - low + 1];
        var maxima = _maxima[level];
        return Math.Max(maxima[low], maxima[high - (1 << level) + 1]);
    }

    /// <summary>A found fundamental's partials taken from the residual: each partial's peak, and its
    /// amplitude no more than the mean of its neighbours' - a partial standing above the run of them
    /// is another note's as well, and that part is left.</summary>
    /// <param name="lobe">Bins either side of a partial's peak its window's main lobe reaches.</param>
    private void Cancel(int candidate, float[] _residual, int lobe)
    {
        var lows = _bandLow[candidate];
        var highs = _bandHigh[candidate];
        int count = lows.Length;
        for (int m = 0; m < count; m++)
        {
            int peakAt = lows[m];
            for (int k = lows[m]; k <= highs[m]; k++)
                if (_residual[k] > _residual[peakAt])
                    peakAt = k;
            _partialAt[m] = peakAt;
            _partials[m] = _residual[peakAt];
        }
        for (int m = 0; m < count; m++)
        {
            // The smooth run: the mean over about an octave of partials about this one.
            int from = Math.Max(0, m - 1), to = Math.Min(count - 1, m + 1);
            float mean = 0;
            for (int j = from; j <= to; j++)
                mean += _partials[j];
            mean /= to - from + 1;
            float amount = Math.Min(_partials[m], mean * 1.2f) * CancelDepth;
            if (_partials[m] <= 0)
                continue;
            // The partial's whole peak, scaled down by as much: the window's main lobe is four bins.
            float scale = amount / _partials[m];
            int at = _partialAt[m];
            for (int k = Math.Max(1, at - lobe); k <= Math.Min(Fft / 2, at + lobe); k++)
            {
                // A Hann window's main lobe, roughly: full at the peak, to a little at its edge.
                float x = (float)Math.Abs(k - at) / (lobe + 1);
                float shape = 0.5f + (0.5f * MathF.Cos(MathF.PI * x));
                _residual[k] = Math.Max(0, _residual[k] - (_residual[k] * scale * shape));
            }
        }
    }

    // ------------------------------------------------------------------ notes from frames

    /// <summary>
    /// Keys over frames made into notes, a little behind the newest frame. A key is sounding where
    /// it was found in most of the seven frames about; a note of it starts at a strike - an onset
    /// where the key's own harmonics rise in the short window - or, for a note that swells in with
    /// none, where it is first heard, less the long window's lag. A strike while it sounds starts it
    /// again; it ends when it has not sounded for a few frames.
    /// </summary>
    private sealed class Tracker
    {
        /// <summary>Frames behind the newest that are settled: what is looked at after a frame.</summary>
        private const int Lag = 24;

        /// <summary>Frames a key may be silent within a note.</summary>
        internal static int Gap = 3;

        /// <summary>The fewest frames a note lasts: 70 ms.</summary>
        internal static int Shortest = 6;

        /// <summary>How much a key's harmonics rise in the short window for an onset to strike it.</summary>
        internal static float StrikeRise = 1.5f;

        /// <summary>Of seven frames about, how many a key is found in to be sounding.</summary>
        internal static int Smooth = 4;

        /// <summary>Frames the long window hears a note after it starts: a note first heard looks back
        /// this far for its strike.</summary>
        internal static int LookBack = 24;

        /// <summary>Frames a note that has stopped sounding is kept: heard again before then with no
        /// strike, it is the same note, lost for a moment under others.</summary>
        internal static int Resume = 80;

        /// <summary>Whether a strike that only raises a key's upper harmonics - as a note struck an
        /// octave, a twelfth or two octaves above does - leaves a note of it sounding on.</summary>
        internal static bool Explain = true;

        /// <summary>The fewest frames a note lasts that was not struck - first heard with no strike
        /// of its own, as a chord's partials can make a key heard for a moment: 139 ms.</summary>
        internal static int Unstruck = 12;

        // A note no longer heard, kept a while in case it comes back: its start and its last frame.
        private readonly int[] _restStart = new int[Keys];
        private readonly double[] _restStartSeconds = new double[Keys];
        private readonly int[] _restLast = new int[Keys];
        private readonly float[] _restPeak = new float[Keys];
        private readonly bool[] _restOwn = new bool[Keys];
        private readonly bool[] _own = new bool[Keys];

        private readonly int[] _start = new int[Keys];
        private readonly double[] _startSeconds = new double[Keys];
        private readonly int[] _lastHeard = new int[Keys];
        private readonly float[] _peak = new float[Keys];
        private int _settled;

        public Tracker()
        {
            Array.Fill(_start, -1);
            Array.Fill(_restStart, -1);
        }

        public void Add(List<Frame> frames, List<HeardNote> notes)
        {
            while (_settled < frames.Count - Lag)
                Settle(frames, notes, _settled++);
        }

        public void Finish(List<Frame> frames, List<HeardNote> notes)
        {
            while (_settled < frames.Count)
                Settle(frames, notes, _settled++);
            for (int key = 0; key < Keys; key++)
            {
                if (_start[key] >= 0)
                    End(notes, key, _lastHeard[key] + 1);
                LetGo(notes, key);
            }
        }

        /// <summary>A resting note given up on: it was its end.</summary>
        private void LetGo(List<HeardNote> notes, int key)
        {
            if (_restStart[key] < 0)
                return;
            Add(notes, key, _restStart[key], _restStartSeconds[key], _restLast[key] + 1, _restPeak[key], _restOwn[key]);
            _restStart[key] = -1;
        }

        /// <summary>The notes sounding now, as far as heard - shown before they end.</summary>
        public IEnumerable<HeardNote> Pending(List<Frame> frames)
        {
            for (int key = 0; key < Keys; key++)
                if (_start[key] >= 0 && _lastHeard[key] - _start[key] + 1 >= (_own[key] ? Shortest : Math.Max(Shortest, Unstruck)))
                    yield return new HeardNote(LowestPitch + key, _startSeconds[key], FrameTime(_lastHeard[key] + 1) + (LowestPitch + key >= SplitKey ? MediumEndShift : EndShift), Strength(_peak[key]));
        }

        private static bool Sounding(List<Frame> frames, int key, int t)
        {
            int found = 0;
            for (int i = Math.Max(0, t - 3); i <= Math.Min(frames.Count - 1, t + 3); i++)
                if (frames[i].Keys[key] > 0)
                    found++;
            return found >= Smooth && frames[Math.Clamp(t, 0, frames.Count - 1)].Keys[key] > 0
                || found >= Smooth + 2;
        }

        private void Settle(List<Frame> frames, List<HeardNote> notes, int t)
        {
            bool onset = IsOnset(frames, t);
            for (int key = 0; key < Keys; key++)
            {
                bool sounding = Sounding(frames, key, t);
                if (_start[key] >= 0)
                {
                    if (onset && t - _start[key] >= Shortest && Struck(frames, key, t) && SoundsSoon(frames, key, t) && !Explained(frames, key, t))
                    {
                        End(notes, key, t);
                        Begin(frames, key, t, StrikeTime(frames, t), own: true);
                    }
                    else if (!sounding && t - _lastHeard[key] > Gap)
                    {
                        End(notes, key, _lastHeard[key] + 1, resting: true);
                        continue;
                    }
                }
                else if (sounding)
                {
                    // Back within a moment and not struck: the note that rested goes on.
                    if (_restStart[key] >= 0 && !(onset && Struck(frames, key, t)) && !StruckSince(frames, key, _restLast[key], t))
                    {
                        (_start[key], _startSeconds[key], _peak[key], _own[key]) = (_restStart[key], _restStartSeconds[key], _restPeak[key], _restOwn[key]);
                        _restStart[key] = -1;
                    }
                    else
                    {
                        LetGo(notes, key);
                        double seconds = StartSeconds(frames, key, t, out bool own);
                        Begin(frames, key, t, seconds, own);
                    }
                }
                if (_restStart[key] >= 0 && t - _restLast[key] > Resume)
                    LetGo(notes, key);
                if (sounding && _start[key] >= 0)
                {
                    _lastHeard[key] = t;
                    _peak[key] = Math.Max(_peak[key], frames[t].KeyEnergy[key]);
                }
            }
        }

        private void Begin(List<Frame> frames, int key, int t, double seconds, bool own)
        {
            _own[key] = own;
            _start[key] = t;
            _startSeconds[key] = seconds;
            _lastHeard[key] = t;
            _peak[key] = frames[t].KeyEnergy[key];
        }

        /// <summary>Whether the key is heard within the long window's lag after frame t.</summary>
        private static bool SoundsSoon(List<Frame> frames, int key, int t)
        {
            for (int i = t; i < Math.Min(frames.Count, t + LookBack); i++)
                if (frames[i].Keys[key] > 0)
                    return true;
            return false;
        }

        /// <summary>Whether an onset at frame t strikes the key: its harmonics in the short window
        /// rise that much over the quietest of the frames just before.</summary>
        private static bool Struck(List<Frame> frames, int key, int t)
        {
            float before = float.MaxValue;
            for (int i = Math.Max(0, t - 4); i < t; i++)
                before = Math.Min(before, frames[i].KeyAttack[key]);
            if (before == float.MaxValue)
                before = 0;
            float after = 0;
            for (int i = t; i < Math.Min(frames.Count, t + 3); i++)
                after = Math.Max(after, frames[i].KeyAttack[key]);
            return after > (before * StrikeRise) + 1e-3f;
        }

        /// <summary>Whether a strike of a sounding key is a note struck above it, whose partials are the
        /// key's harmonics: one an octave, a twelfth or two octaves up is struck, and the key's own
        /// fundamental does not rise.</summary>
        private static bool Explained(List<Frame> frames, int key, int t)
        {
            if (!Explain)
                return false;
            bool above = false;
            foreach (int up in (ReadOnlySpan<int>)[12, 19, 24])
                if (key + up < Keys && Struck(frames, key + up, t) && !Sounding(frames, key + up, t - 4))
                    above = true;
            if (!above)
                return false;
            float before = float.MaxValue;
            for (int i = Math.Max(0, t - 4); i < t; i++)
                before = Math.Min(before, frames[i].RootAttack[key]);
            if (before == float.MaxValue)
                before = 0;
            float after = 0;
            for (int i = t; i < Math.Min(frames.Count, t + 3); i++)
                after = Math.Max(after, frames[i].RootAttack[key]);
            return after <= (before * StrikeRise) + 1e-3f;
        }

        /// <summary>A peak of the onset strength, standing out from those about it.</summary>
        private static bool IsOnset(List<Frame> frames, int t)
        {
            if (t < 1 || t + 1 >= frames.Count)
                return false;
            float o = frames[t].Onset;
            if (o < frames[t - 1].Onset || o < frames[t + 1].Onset)
                return false;
            float mean = 0;
            int n = 0;
            for (int i = Math.Max(0, t - 10); i <= Math.Min(frames.Count - 1, t + 3); i++, n++)
                mean += frames[i].Onset;
            mean /= n;
            return o > mean * 1.5f && o > 2;
        }

        /// <summary>A note ends: struck again, it is done; fallen silent, it rests a while first.</summary>
        private void End(List<HeardNote> notes, int key, int end, bool resting = false)
        {
            int start = _start[key];
            _start[key] = -1;
            if (resting)
            {
                LetGo(notes, key);
                (_restStart[key], _restStartSeconds[key], _restLast[key], _restPeak[key], _restOwn[key]) = (start, _startSeconds[key], end - 1, _peak[key], _own[key]);
                return;
            }
            Add(notes, key, start, _startSeconds[key], end, _peak[key], _own[key]);
        }

        private static void Add(List<HeardNote> notes, int key, int start, double startSeconds, int end, float peak, bool own)
        {
            if (end - start < (own ? Shortest : Math.Max(Shortest, Unstruck)))
                return;
            // The long window still hears a note for a while after it ends.
            double endSeconds = Math.Max(startSeconds + 0.03, FrameTime(end) + (LowestPitch + key >= SplitKey ? MediumEndShift : EndShift));
            notes.Add(new HeardNote(LowestPitch + key, startSeconds, endSeconds, Strength(peak)));
        }

        /// <summary>Whether the key was struck at an onset after frame from, up to t.</summary>
        private static bool StruckSince(List<Frame> frames, int key, int from, int t)
        {
            for (int i = from + 1; i <= t; i++)
                if (IsOnset(frames, i) && Struck(frames, key, i))
                    return true;
            return false;
        }

        /// <summary>When a note first heard at frame t starts: at the latest onset that strikes it
        /// within the long window's lag before; else the strongest onset there; else the lag back.</summary>
        private static double StartSeconds(List<Frame> frames, int key, int t, out bool own)
        {
            int from = Math.Max(1, t - LookBack), to = Math.Min(frames.Count - 2, t + 1);
            for (int i = to; i >= from; i--)
                if (IsOnset(frames, i) && Struck(frames, key, i))
                {
                    own = !Explained(frames, key, i);
                    return StrikeTime(frames, i);
                }
            own = false;
            int best = -1;
            float bestOnset = 0;
            for (int i = from; i <= to; i++)
                if (IsOnset(frames, i) && frames[i].Onset > bestOnset)
                    (best, bestOnset) = (i, frames[i].Onset);
            return best >= 0 ? StrikeTime(frames, best) : Math.Max(0, FrameTime(t) - (Window / 4.0 / Rate));
        }

        private static double StrikeTime(List<Frame> frames, int t) => Math.Max(0, FrameTime(t) + OnsetShift);

        private static double FrameTime(int t) => (t + 1) * FrameSeconds;

        private static float Strength(float peak) => (float)Math.Clamp(Math.Log10(1 + peak) / 3, 0, 1);
    }
}
