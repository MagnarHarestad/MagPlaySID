namespace MagPlay.Audio;

/// <summary>
/// The magnitude spectrum of a real signal of a power-of-two length: a complex transform of half
/// the length, the even samples as real parts and the odd as imaginary, then split into the real
/// signal's spectrum. Twiddles and bit reversal worked out once.
/// </summary>
public sealed class RealFft
{
    private readonly int _n, _half;
    private readonly int[] _reverse;
    private readonly float[] _cos, _sin;       // for the half-length transform
    private readonly float[] _splitCos, _splitSin; // for the split
    private readonly float[] _re, _im;

    public RealFft(int length)
    {
        if (length < 4 || (length & (length - 1)) != 0)
            throw new ArgumentException("A power of two.", nameof(length));
        _n = length;
        _half = length / 2;
        _reverse = new int[_half];
        int bits = (int)Math.Log2(_half);
        for (int i = 0; i < _half; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }
        _cos = new float[_half / 2];
        _sin = new float[_half / 2];
        for (int i = 0; i < _half / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / _half);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / _half);
        }
        _splitCos = new float[_half];
        _splitSin = new float[_half];
        for (int k = 0; k < _half; k++)
        {
            _splitCos[k] = (float)Math.Cos(-2 * Math.PI * k / _n);
            _splitSin[k] = (float)Math.Sin(-2 * Math.PI * k / _n);
        }
        _re = new float[_half];
        _im = new float[_half];
    }

    /// <summary><paramref name="signal"/>'s magnitudes, bins 0 to length/2, into <paramref name="magnitude"/>.</summary>
    public void Magnitudes(ReadOnlySpan<float> signal, Span<float> magnitude)
    {
        for (int i = 0; i < _half; i++)
        {
            int r = _reverse[i];
            _re[r] = signal[2 * i];
            _im[r] = signal[(2 * i) + 1];
        }
        for (int size = 2; size <= _half; size <<= 1)
        {
            int halfSize = size >> 1, step = _half / size;
            for (int start = 0; start < _half; start += size)
            {
                for (int j = 0, t = 0; j < halfSize; j++, t += step)
                {
                    int a = start + j, b = a + halfSize;
                    float wr = _cos[t], wi = _sin[t];
                    float xr = (_re[b] * wr) - (_im[b] * wi);
                    float xi = (_re[b] * wi) + (_im[b] * wr);
                    _re[b] = _re[a] - xr;
                    _im[b] = _im[a] - xi;
                    _re[a] += xr;
                    _im[a] += xi;
                }
            }
        }
        // X[k] = (Z[k] + conj Z[N/2-k]) / 2 + W^k (Z[k] - conj Z[N/2-k]) / 2i
        magnitude[0] = Math.Abs(_re[0] + _im[0]);
        magnitude[_half] = Math.Abs(_re[0] - _im[0]);
        for (int k = 1; k < _half; k++)
        {
            float zr = _re[k], zi = _im[k], cr = _re[_half - k], ci = -_im[_half - k];
            float er = (zr + cr) * 0.5f, ei = (zi + ci) * 0.5f;
            float or = (zi - ci) * 0.5f, oi = -(zr - cr) * 0.5f;
            float wr = _splitCos[k], wi = _splitSin[k];
            float xr = er + ((or * wr) - (oi * wi));
            float xi = ei + ((or * wi) + (oi * wr));
            magnitude[k] = MathF.Sqrt((xr * xr) + (xi * xi));
        }
    }
}
