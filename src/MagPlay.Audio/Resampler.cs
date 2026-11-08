namespace MagPlay.Audio;

/// <summary>
/// Sound from one sample rate to another, any two: each sample out made from 32 samples in through
/// a windowed sinc - Kaiser, cut off below the lower rate's half so nothing folds back - read from a
/// table of 512 phases between which it is interpolated. Fed a piece at a time; an equal rate is
/// copied as it is.
/// </summary>
public sealed class Resampler
{
    private const int Side = 16;
    private const int Phases = 512;

    private readonly int _channels;
    private readonly double _step;
    private readonly float[]? _table;
    private float[] _input = new float[8192];
    private int _frames;

    /// <summary>Where the next sample out falls, in frames of the input kept.</summary>
    private double _position;

    public Resampler(int fromRate, int toRate, int channels)
    {
        _channels = channels;
        _step = (double)fromRate / toRate;
        if (fromRate == toRate)
            return;

        // Below the lower of the two rates' halves, by a little, as the window's slope needs.
        double cutoff = Math.Min(1.0, (double)toRate / fromRate) * 0.94;
        _table = new float[(Phases + 1) * 2 * Side];
        for (int phase = 0; phase <= Phases; phase++)
        {
            double fraction = (double)phase / Phases;
            for (int tap = 0; tap < 2 * Side; tap++)
            {
                double x = tap - (Side - 1) - fraction;
                double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x * cutoff) / (Math.PI * x * cutoff);
                double window = Kaiser(x / Side, 8.0);
                _table[(phase * 2 * Side) + tap] = (float)(cutoff * sinc * window);
            }
        }
    }

    private static double Kaiser(double x, double beta) =>
        Math.Abs(x) > 1 ? 0 : Bessel0(beta * Math.Sqrt(1 - (x * x))) / Bessel0(beta);

    private static double Bessel0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 30; k++)
        {
            term *= x / (2 * k);
            sum += term * term;
        }
        return sum;
    }

    /// <summary><paramref name="input"/> taken in, and what it makes written to
    /// <paramref name="output"/>, which grows to hold it; the samples written, every channel's in turn.</summary>
    public int Process(ReadOnlySpan<float> input, ref float[] output)
    {
        if (_table is null)
        {
            if (output.Length < input.Length)
                output = new float[input.Length];
            input.CopyTo(output);
            return input.Length;
        }

        int frames = input.Length / _channels;
        if (_input.Length < (_frames + frames) * _channels)
            Array.Resize(ref _input, Math.Max(_input.Length * 2, (_frames + frames) * _channels));
        input[..(frames * _channels)].CopyTo(_input.AsSpan(_frames * _channels));
        _frames += frames;

        int most = (int)((_frames - _position) / _step) + 2;
        if (output.Length < most * _channels)
            output = new float[most * _channels];

        int written = 0;
        while (_position + Side < _frames)
        {
            int whole = (int)_position;
            double fraction = (_position - whole) * Phases;
            int phase = (int)fraction;
            float blend = (float)(fraction - phase);
            int first = whole - (Side - 1);
            int a = phase * 2 * Side, b = a + (2 * Side);
            for (int c = 0; c < _channels; c++)
            {
                float sum = 0;
                for (int tap = 0; tap < 2 * Side; tap++)
                {
                    int at = first + tap;
                    if (at < 0)
                        continue;
                    float weight = _table[a + tap] + (blend * (_table[b + tap] - _table[a + tap]));
                    sum += weight * _input[(at * _channels) + c];
                }
                output[(written * _channels) + c] = sum;
            }
            written++;
            _position += _step;
        }

        // What no sample out needs any more, let go of.
        int done = Math.Max(0, (int)_position - (Side - 1));
        if (done > 0)
        {
            _input.AsSpan(done * _channels, (_frames - done) * _channels).CopyTo(_input);
            _frames -= done;
            _position -= done;
        }
        return written * _channels;
    }

    /// <summary>The last of the sound out: the input ended with silence after it.</summary>
    public int Flush(ref float[] output) =>
        _table is null ? 0 : Process(new float[(Side + 1) * _channels], ref output);
}
