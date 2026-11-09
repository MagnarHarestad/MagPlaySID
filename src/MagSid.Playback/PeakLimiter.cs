using System.Numerics;
using System.Runtime.InteropServices;

namespace MagSid.Playback;

/// <summary>
/// A gain, and a look-ahead limiter that keeps the true peak under a ceiling - the wave between the
/// samples too, as a converter draws it - so a tune can be brought up to a loudness without
/// clipping. The gain comes down smoothly over <see cref="Ahead"/> before a peak and goes back up
/// over a release, and the sound comes out <see cref="Delay"/> frames late.
/// </summary>
public sealed class PeakLimiter
{
    /// <summary>Frames either side the wave between two samples is drawn from.</summary>
    private const int Half = 12;

    private const int Phases = 4;

    /// <summary>For each quarter between two samples, the weights of the samples around it -
    /// a Lanczos kernel over 24 samples, as true-peak meters look four times between samples.</summary>
    private static readonly float[][] Between = MakeBetween();

    /// <summary>Weights padded to whole vectors, read a vector at a time.</summary>
    private static readonly int Padded = (2 * Half + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    /// <summary>The most the wave between samples can be, as a share of the largest sample around it.</summary>
    private static readonly float Overshoot = Between.Max(w => w.Sum(Math.Abs));

    private readonly int _channels;
    private readonly int _ahead;
    private readonly float _ceiling;

    /// <summary>The peak aimed for: a little under the ceiling, as meters draw the wave between
    /// samples a little differently - ffmpeg's read up to 0.15 dB higher without this.</summary>
    private readonly float _aim;
    private const double MarginDb = 0.15;
    private readonly float _release;
    private float _gain = 1f;

    /// <summary>The gained sound of each channel, as it waits to come out: a ring written twice,
    /// at a frame and a ring's length on, so the samples around any frame lie side by side.</summary>
    private readonly float[][] _history;
    private readonly int _size, _mask;
    private long _frame;

    /// <summary>Below this no wave between samples can reach the ceiling; the last frame above it.</summary>
    private readonly float _quiet;
    private long _loudAt = long.MinValue / 2;

    /// <summary>The gains peaks need over the look-ahead, smallest first: a sliding minimum.</summary>
    private readonly long[] _minAt;
    private readonly float[] _minGain;
    private int _minHead, _minCount;

    /// <summary>The held gain after its release, and the last <see cref="Ahead"/> of them, whose
    /// mean is the gain applied - a ramp that reaches each peak's gain as the peak comes out.</summary>
    private float _held = 1f;
    private readonly float[] _box;
    private int _boxAt;
    private double _boxSum;

    /// <summary>The ride: the gain brought down by itself when the limiter keeps taking more than
    /// <see cref="MaxWorkDb"/> off - a song mixed far louder than its kind - and back up slowly once
    /// it no longer needs to be. Worked out a block of frames at a time.</summary>
    private const int RideBlock = 256;
    private const double WorkSeconds = 1.5, RideDownSeconds = 1.0, RecoverDbPerSecond = 0.3;
    private readonly double _blockSeconds;
    private double _blockSum;
    private int _blockCount;
    private double _workDb, _rideDb;
    private float _ride = 1f, _rideStep;

    /// <param name="channels">1 or 2, interleaved; the channels are limited together.</param>
    /// <param name="ceilingDb">The highest true peak let out, in dB below full scale.</param>
    /// <param name="aheadSeconds">How long before a peak the gain starts down.</param>
    /// <param name="releaseSeconds">How long the gain takes to come most of the way back up.</param>
    public PeakLimiter(int channels, int sampleRate, double ceilingDb = -0.3, double aheadSeconds = 0.005, double releaseSeconds = 0.15)
    {
        _channels = channels;
        _ahead = Math.Max(1, (int)Math.Round(aheadSeconds * sampleRate));
        _ceiling = (float)Math.Pow(10, ceilingDb / 20);
        _aim = (float)Math.Pow(10, (ceilingDb - MarginDb) / 20);
        _release = (float)(1 - Math.Exp(-1 / (releaseSeconds * sampleRate)));
        // A frame comes out with a gain no more than the peaks on either side of it need: those of
        // the wave from the frame before it, and to the frame after.
        Delay = _ahead - 1 + Half;
        int size = 1;
        while (size < Delay + 1)
            size <<= 1;
        _size = size;
        _mask = size - 1;
        _history = new float[channels][];
        for (int c = 0; c < channels; c++)
            _history[c] = new float[(2 * size) + Padded];
        _quiet = _aim / Overshoot;
        _minAt = new long[_ahead + 2];
        _minGain = new float[_ahead + 2];
        _box = new float[_ahead];
        Array.Fill(_box, 1f);
        _boxSum = _ahead;
        _blockSeconds = (double)RideBlock / sampleRate;
    }

    /// <summary>Frames the sound comes out late.</summary>
    public int Delay { get; }

    /// <summary>Frames the gain takes to come down before a peak.</summary>
    public int Ahead => _ahead;

    /// <summary>What the sound is raised by before it is limited, in dB.</summary>
    public double GainDb
    {
        get => 20 * Math.Log10(_gain);
        set => _gain = (float)Math.Pow(10, value / 20);
    }

    /// <summary>The most the limiter is to take off, over a second or two, before the gain rides
    /// down: in dB; infinity, never.</summary>
    public double MaxWorkDb { get; set; } = double.PositiveInfinity;

    /// <summary>How far the gain has ridden down, in dB: 0 or below.</summary>
    public double RideDb => _rideDb;

    /// <summary>Limits <paramref name="frames"/> in place: interleaved samples, full scale ±1.</summary>
    public void Process(Span<float> frames)
    {
        int channels = _channels, count = frames.Length / channels;
        float gain = _gain, ceiling = _ceiling;
        for (int f = 0; f < count; f++)
        {
            long n = _frame++;
            int at = (int)(n & _mask);
            for (int c = 0; c < channels; c++)
            {
                float x = frames[(f * channels) + c] * gain * _ride;
                _history[c][at] = x;
                _history[c][at + _size] = x;
                if (Math.Abs(x) > _quiet)
                    _loudAt = n;
            }

            // The peak of the frame Half back, and of the wave from it to the next - looked for
            // only when a sample around it is loud enough for the wave to reach the ceiling.
            long k = n - Half;
            float need = 1f;
            if (n - _loudAt < 2 * Half)
            {
                float peak = 0;
                int from = (int)((k - Half + 1) & _mask);
                for (int c = 0; c < channels; c++)
                {
                    var around = MemoryMarshal.Cast<float, Vector<float>>(_history[c].AsSpan(from, Padded));
                    peak = Math.Max(peak, Math.Abs(_history[c][from + Half - 1]));
                    for (int p = 0; p < Phases - 1; p++)
                    {
                        var weights = MemoryMarshal.Cast<float, Vector<float>>(Between[p]);
                        var sum = Vector<float>.Zero;
                        for (int i = 0; i < weights.Length; i++)
                            sum += around[i] * weights[i];
                        peak = Math.Max(peak, Math.Abs(Vector.Sum(sum)));
                    }
                }
                if (peak > _aim)
                    need = _aim / peak;
            }

            // The smallest gain needed over the look-ahead.
            int length = _minAt.Length;
            while (_minCount > 0 && _minGain[Wrap(_minHead + _minCount - 1, length)] >= need)
                _minCount--;
            int tail = Wrap(_minHead + _minCount, length);
            _minAt[tail] = k;
            _minGain[tail] = need;
            _minCount++;
            while (_minAt[_minHead] < k - _ahead)
            {
                _minHead = Wrap(_minHead + 1, length);
                _minCount--;
            }
            float least = _minGain[_minHead];

            _held = least < _held ? least : _held + ((least - _held) * _release);
            _boxSum += _held - _box[_boxAt];
            _box[_boxAt] = _held;
            _boxAt = _boxAt + 1 == _ahead ? 0 : _boxAt + 1;
            float applied = (float)(_boxSum / _ahead);
            _ride += _rideStep;
            _blockSum += applied;
            if (++_blockCount == RideBlock)
                Ride();

            int outAt = (int)((n - Delay) & _mask);
            for (int c = 0; c < channels; c++)
                frames[(f * channels) + c] = n < Delay ? 0 : Math.Clamp(_history[c][outAt] * applied, -ceiling, ceiling);
        }
    }

    /// <summary>How hard the limiter has been working, smoothed over a second or two, and the ride
    /// it calls for, glided to over the next block.</summary>
    private void Ride()
    {
        double mean = _blockSum / _blockCount;
        _blockSum = 0;
        _blockCount = 0;
        _workDb += (20 * Math.Log10(Math.Max(mean, 1e-6)) - _workDb) * (_blockSeconds / WorkSeconds);
        if (double.IsFinite(MaxWorkDb))
        {
            double excess = -MaxWorkDb - _workDb;
            if (excess > 0)
                _rideDb -= excess * _blockSeconds / RideDownSeconds;
            else if (_workDb > -MaxWorkDb / 2)
                _rideDb = Math.Min(0, _rideDb + (RecoverDbPerSecond * _blockSeconds));
        }
        _rideStep = ((float)Math.Pow(10, _rideDb / 20) - _ride) / RideBlock;
    }

    private static int Wrap(int index, int length) => index >= length ? index - length : index;

    private static float[][] MakeBetween()
    {
        var phases = new float[Phases - 1][];
        for (int p = 1; p < Phases; p++)
        {
            // The wave at k + t from the samples k-11 to k+12.
            double t = (double)p / Phases, total = 0;
            var weights = new double[2 * Half];
            for (int i = 0; i < weights.Length; i++)
            {
                double u = t - (i - Half + 1);
                weights[i] = Sinc(u) * Sinc(u / Half);
                total += weights[i];
            }
            phases[p - 1] = new float[(2 * Half + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count];
            for (int i = 0; i < weights.Length; i++)
                phases[p - 1][i] = (float)(weights[i] / total);
        }
        return phases;
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
