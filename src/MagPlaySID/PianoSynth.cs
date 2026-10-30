using NAudio.Wave;

namespace MagPlaySid;

/// <summary>
/// A small piano, for the keys along the window's bottom when no tune plays. Each struck key is
/// eight partials - stretched a little sharp as a real string's are - each two strings a hair apart,
/// so they beat as a piano's unisons do; they die away quickly, then ring on quietly, the high
/// notes sooner than the low, after a soft knock of the hammer. Up to sixteen keys sound at once.
/// </summary>
internal sealed class PianoSynth : IWaveProvider
{
    public const int SampleRate = 44100;
    private const int Partials = 8;
    private const int MostStruck = 16;
    private const int TableSize = 4096;
    private static readonly float[] Sine = Enumerable.Range(0, TableSize + 1)
        .Select(i => (float)Math.Sin(2 * Math.PI * i / TableSize)).ToArray();

    private readonly object _lock = new();
    private readonly List<Ringing> _struck = [];
    private readonly Random _random = new();
    /// <summary>The mix, kept: the sound card asks a hundred times a second, and no garbage on its thread.</summary>
    private float[] _mix = [];

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    /// <summary>A key struck: its number on the window's keyboard, C-0 as 0.</summary>
    public void Strike(int key)
    {
        var strike = new Ringing(16.3516 * Math.Pow(2, key / 12.0), _random.Next());
        lock (_lock)
        {
            if (_struck.Count >= MostStruck)
                _struck.RemoveAt(0);
            _struck.Add(strike);
        }
    }

    /// <summary>Keys still sounding.</summary>
    public int Sounding
    {
        get
        {
            lock (_lock)
                return _struck.Count;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / 2;
        if (_mix.Length < samples)
            _mix = new float[samples];
        var mix = _mix;
        Array.Clear(mix, 0, samples);
        lock (_lock)
        {
            foreach (var strike in _struck)
                strike.Add(mix, samples);
            _struck.RemoveAll(s => s.Done);
        }
        for (int i = 0; i < samples; i++)
        {
            // A soft limit, so a fistful of keys swells rather than clips.
            float v = MathF.Tanh(mix[i] * 0.9f);
            short s = (short)(v * 30000);
            buffer[offset + 2 * i] = (byte)s;
            buffer[offset + 2 * i + 1] = (byte)(s >> 8);
        }
        return samples * 2;
    }

    /// <summary>A key struck and still ringing.</summary>
    private sealed class Ringing
    {
        private readonly double[] _phase = new double[Partials * 2];
        private readonly double[] _step = new double[Partials * 2];
        private readonly float[] _level = new float[Partials];
        private readonly float[] _fast = new float[Partials];
        private readonly float[] _slow = new float[Partials];
        private readonly float[] _fastDecay = new float[Partials];
        private readonly float[] _slowDecay = new float[Partials];
        private readonly int _partials;
        private uint _noise;
        private float _knock = 1;
        private float _knockFiltered;
        private int _age;

        public Ringing(double frequency, int seed)
        {
            _noise = (uint)seed | 1;
            // The higher the key, the fewer and quieter its upper partials, and the shorter its ring.
            double octavesUp = Math.Log2(frequency / 55.0);
            double ring = Math.Clamp(2.4 - 0.32 * octavesUp, 0.35, 3.0);
            const double stretch = 0.00035;
            int partials = 0;
            for (int k = 1; k <= Partials; k++)
            {
                double f = k * frequency * Math.Sqrt(1 + stretch * k * k);
                if (f > 15000)
                    break;
                int p = k - 1;
                double beat = 1 + 0.0006 * (1 + 0.2 * k);
                _step[2 * p] = f / SampleRate * TableSize;
                _step[2 * p + 1] = f * beat / SampleRate * TableSize;
                _phase[2 * p] = 0;
                _phase[2 * p + 1] = TableSize * 0.25 * k % TableSize;
                _level[p] = (float)(Math.Pow(k, -1.25) * Math.Exp(-(k - 1) * (0.12 + 0.06 * Math.Max(0, octavesUp))));
                double fastSeconds = ring / (1 + 0.45 * (k - 1));
                _fastDecay[p] = (float)Math.Exp(-1.0 / (fastSeconds * 0.35 * SampleRate));
                _slowDecay[p] = (float)Math.Exp(-1.0 / (fastSeconds * 2.2 * SampleRate));
                _fast[p] = 0.7f;
                _slow[p] = 0.3f;
                partials++;
            }
            _partials = partials;
        }

        public bool Done { get; private set; }

        public void Add(float[] mix, int samples)
        {
            const float gain = 0.16f;
            const int attack = SampleRate * 3 / 1000;
            float knockDecay = MathF.Exp(-1f / (0.006f * SampleRate));
            float loudest = 0;
            for (int i = 0; i < samples; i++)
            {
                float sum = 0;
                for (int p = 0; p < _partials; p++)
                {
                    float envelope = _fast[p] + _slow[p];
                    _fast[p] *= _fastDecay[p];
                    _slow[p] *= _slowDecay[p];
                    float strings = Table(ref _phase[2 * p], _step[2 * p]) + Table(ref _phase[2 * p + 1], _step[2 * p + 1]);
                    sum += strings * 0.5f * _level[p] * envelope;
                }
                if (_partials > 0)
                    loudest = Math.Max(loudest, _fast[0] + _slow[0]);

                // The hammer: a short knock of noise, softened.
                _noise ^= _noise << 13;
                _noise ^= _noise >> 17;
                _noise ^= _noise << 5;
                float noise = (_noise & 0xFFFF) / 32768f - 1f;
                _knockFiltered += 0.18f * (noise * _knock - _knockFiltered);
                _knock *= knockDecay;

                float rise = _age < attack ? _age / (float)attack : 1f;
                mix[i] += (sum + _knockFiltered * 0.25f) * rise * gain;
                _age++;
            }
            Done = loudest < 0.0008f || _age > SampleRate * 7;
        }

        private static float Table(ref double phase, double step)
        {
            int index = (int)phase;
            float fraction = (float)(phase - index);
            float value = Sine[index] + (Sine[index + 1] - Sine[index]) * fraction;
            phase += step;
            if (phase >= TableSize)
                phase -= TableSize;
            return value;
        }
    }
}
