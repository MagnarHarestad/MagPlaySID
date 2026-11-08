namespace MagPlaySid.Export;

/// <summary>A Huffman code table of Layer III: the largest x and y it codes directly, the bits of
/// an escape beyond them and the most those carry, and each pair's code and its length.</summary>
internal sealed record Huffman(int XLen, int YLen, int LinBits, int LinMax, ushort[] Codes, byte[] Lengths);

/// <summary>
/// An MP3 encoder - MPEG-1 Layer III, mono or stereo, at a constant bitrate - in fixed point: a C# translation
/// of Shine (github.com/toots/shine; LGPL 2), the fixed-point encoder for chips without floating
/// point. Like Shine it has no psychoacoustic model: the polyphase filter bank,
/// the MDCT with its alias reduction, a step size found for the bits each granule has, long blocks
/// only and no scalefactors, the Huffman tables chosen for the fewest bits. Changes: the smallest
/// table that holds a region is tried first, not table 13, and a granule's bits are written straight
/// out a byte at a time; the sound goes in a little quieter, so it comes out as loud as it went in;
/// and bits a frame has beyond what its granules may hold are written as ancillary data, so it
/// encodes at 320 kbit/s too.
/// </summary>
internal sealed class Mp3Encoder
{
    /// <summary>The samples a channel each <see cref="Encode"/> takes: one frame, two granules.</summary>
    public const int SamplesPerFrame = 2 * Granule;

    private const int Granule = 576;
    private const int Mono = 3;
    private const int Stereo = 0;

    private const double Pi = 3.14159265358979;
    private const double Pi36 = 0.087266462599717;
    private const double Pi64 = 0.049087385212;

    /// <summary>The step sizes the quantiser may try, from -127: past 45 the global gain would not
    /// fit its eight bits.</summary>
    private const int MostStep = 45;

    /// <summary>What a sample is multiplied by going in: 2^16, the sample as a fraction in fixed
    /// point, over 1.1248 - Shine's filter bank and MDCT, as it scales them, come out of a decoder that
    /// much louder (measured with tones from 100 Hz to 12 kHz, as from Shine itself), and a tune's
    /// loudest would clip.</summary>
    private const int InputScale = 58265;

    private static readonly int[] SampleRates = [44100, 48000, 32000];
    private static readonly int[] BitRates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    /// <summary>Table B.8: the long blocks' scalefactor bands at 44.1, 48 and 32 kHz.</summary>
    private static readonly int[][] Bands =
    [
        [0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 52, 62, 74, 90, 110, 134, 162, 196, 238, 288, 342, 418, 576],
        [0, 4, 8, 12, 16, 20, 24, 30, 36, 42, 50, 60, 72, 88, 106, 128, 156, 190, 230, 276, 330, 384, 576],
        [0, 4, 8, 12, 16, 20, 24, 30, 36, 44, 54, 66, 82, 102, 126, 156, 194, 240, 296, 364, 448, 550, 576],
    ];

    /// <summary>How the big values' bands split into three regions, by how many bands they span.</summary>
    private static readonly (int Region0, int Region1)[] Subdivisions =
    [
        (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 1), (1, 1), (1, 1), (1, 2), (2, 2), (2, 3), (2, 3),
        (3, 4), (3, 4), (3, 4), (4, 5), (4, 5), (4, 6), (5, 6), (5, 6), (5, 7), (6, 7), (6, 7),
    ];

    /// <summary>Table B.9: the alias reduction's butterflies.</summary>
    private static readonly double[] AliasCoefficients = [-0.6, -0.535, -0.33, -0.185, -0.095, -0.041, -0.0142, -0.0037];

    private static readonly int[] AliasSines = Array.ConvertAll(AliasCoefficients, c => (int)(c / Math.Sqrt(1.0 + (c * c)) * 0x7fffffff));
    private static readonly int[] AliasCosines = Array.ConvertAll(AliasCoefficients, c => (int)(1.0 / Math.Sqrt(1.0 + (c * c)) * 0x7fffffff));

    private static readonly int[] Window = Array.ConvertAll(Mp3Tables.Window, w => (int)(w * 0x7fffffff));
    private static readonly int[] Filter = MakeFilter();
    private static readonly int[] Cosines = MakeCosines();
    private static readonly double[] Steps = new double[128 + MostStep + 1];
    private static readonly int[] StepsFixed = new int[128 + MostStep + 1];
    private static readonly int[] ThreeQuarters = new int[10000];

    static Mp3Encoder()
    {
        for (int i = 0; i < Steps.Length; i++)
        {
            Steps[i] = Math.Pow(2.0, (127 - i) / 4.0);
            StepsFixed[i] = Steps[i] * 2 > 0x7fffffff ? 0x7fffffff : (int)((Steps[i] * 2) + 0.5);
        }
        for (int i = 0; i < ThreeQuarters.Length; i++)
            ThreeQuarters[i] = (int)(Math.Sqrt(Math.Sqrt(i) * i) - 0.0946 + 0.5);
    }

    private readonly Stream _output;
    private readonly int _channels;

    /// <summary>The header and side information: 17 bytes of it for mono, 32 for stereo.</summary>
    private readonly int _sideInfoBits;
    private readonly int[] _bands;
    private readonly int _rateIndex;
    private readonly int _bitRateIndex;
    private readonly int _wholeSlots;
    private readonly double _fractionSlots;
    private double _slotLag;
    private int _padding;
    private int _meanBits;
    private int _reservoir;

    /// <summary>Bits of the frame its granules cannot hold - at 320 kbit/s, a mono frame has more than
    /// two granules may say they have - written after them as ancillary data, which decoders skip.</summary>
    private int _ancillary;

    // Each channel's own: its filter bank's history, and the frame's spectrum and its quantising.
    private readonly int[][] _history;
    private readonly int[] _offset;
    private readonly int[] _windowed = new int[64];

    /// <summary>A channel's filter bank output, [granule 0-2][18 slots][32 bands]: the last granule
    /// of the frame before, then this frame's two.</summary>
    private readonly int[][] _subbands;
    private readonly int[] _mdctIn = new int[36];
    private readonly int[][] _spectrum;
    private readonly int[] _magnitudes = new int[Granule];
    private int _loudest;
    private readonly int[][] _quantised;

    /// <summary>[granule, channel].</summary>
    private readonly GranuleInfo[,] _granules;
    private readonly short[] _channelSamples = new short[SamplesPerFrame];

    private ulong _bits;
    private int _bitCount;
    private readonly byte[] _frame = new byte[2048];
    private int _frameLength;

    private sealed class GranuleInfo
    {
        public int Part23Length;
        public int BigValues;
        public int Count1;
        public int GlobalGain;
        public readonly int[] TableSelect = new int[3];
        public int Region0Count;
        public int Region1Count;
        public int Count1Table;
        public int Address1;
        public int Address2;
        public int Address3;
        public int Step;
    }

    /// <param name="bitRate">In kilobits a second: one of MPEG-1's, 32-320.</param>
    /// <param name="channels">1 mono, 2 stereo - each channel coded on its own.</param>
    public Mp3Encoder(Stream output, int sampleRate, int bitRate, int channels = 1)
    {
        if (channels is not (1 or 2))
            throw new ArgumentException("MP3 is mono or stereo.", nameof(channels));
        _output = output;
        _channels = channels;
        _sideInfoBits = 8 * (4 + (channels == 1 ? 17 : 32));
        _history = new int[channels][];
        _offset = new int[channels];
        _subbands = new int[channels][];
        _spectrum = new int[channels][];
        _quantised = new int[channels][];
        _granules = new GranuleInfo[2, channels];
        for (int ch = 0; ch < channels; ch++)
        {
            _history[ch] = new int[512];
            _subbands[ch] = new int[3 * 18 * 32];
            _spectrum[ch] = new int[2 * Granule];
            _quantised[ch] = new int[2 * Granule];
            _granules[0, ch] = new GranuleInfo();
            _granules[1, ch] = new GranuleInfo();
        }
        _rateIndex = Array.IndexOf(SampleRates, sampleRate);
        _bitRateIndex = Array.IndexOf(BitRates, bitRate);
        if (_rateIndex < 0 || _bitRateIndex <= 0)
            throw new ArgumentException($"MP3 does not take {sampleRate} Hz at {bitRate} kbit/s.");
        _bands = Bands[_rateIndex];

        double slots = (double)SamplesPerFrame / sampleRate * (1000.0 * bitRate / 8);
        _wholeSlots = (int)slots;
        _fractionSlots = slots - _wholeSlots;
        _slotLag = -_fractionSlots;
    }

    /// <summary>One frame: <see cref="SamplesPerFrame"/> samples a channel - left and right in
    /// turn for stereo - written out as soon as encoded.</summary>
    public void Encode(ReadOnlySpan<short> samples)
    {
        if (samples.Length != SamplesPerFrame * _channels)
            throw new ArgumentException($"A frame is {SamplesPerFrame} samples a channel.", nameof(samples));

        if (_fractionSlots != 0)
        {
            _padding = _slotLag <= _fractionSlots - 1.0 ? 1 : 0;
            _slotLag += _padding - _fractionSlots;
        }
        int frameBits = 8 * (_wholeSlots + _padding);
        _meanBits = (frameBits - _sideInfoBits) / (2 * _channels);

        for (int ch = 0; ch < _channels; ch++)
        {
            for (int i = 0; i < SamplesPerFrame; i++)
                _channelSamples[i] = samples[(i * _channels) + ch];
            Transform(ch, _channelSamples);
        }
        Quantise();
        WriteFrame();
        _output.Write(_frame, 0, _frameLength);
        _frameLength = 0;
    }

    // ------------------------------------------------------------------ fixed point

    private static int Mul(int a, int b) => (int)(((long)a * b) >> 32);

    private static int MulRound(int a, int b) => (int)((((long)a * b) + 0x80000000L) >> 32);

    // ------------------------------------------------------------------ filter bank and MDCT

    private static int[] MakeFilter()
    {
        var filter = new int[32 * 64];
        for (int i = 0; i < 32; i++)
            for (int j = 0; j < 64; j++)
            {
                double f = 1e9 * Math.Cos((2 * i + 1) * (16 - j) * Pi64);
                f = f >= 0 ? Math.Truncate(f + 0.5) : Math.Truncate(f - 0.5);
                filter[(i * 64) + j] = (int)(f * (0x7fffffff * 1e-9));
            }
        return filter;
    }

    private static int[] MakeCosines()
    {
        var cosines = new int[18 * 36];
        for (int m = 0; m < 18; m++)
            for (int k = 0; k < 36; k++)
                cosines[(m * 36) + k] = (int)(Math.Sin(Pi36 * (k + 0.5)) * Math.Cos(Pi / 72 * (2 * k + 19) * (2 * m + 1)) * 0x7fffffff);
        return cosines;
    }

    /// <summary>32 samples into the window, filtered into 32 subbands at <paramref name="to"/>.</summary>
    private void FilterSubbands(int ch, ReadOnlySpan<short> samples, ref int at, int to)
    {
        var history = _history[ch];
        int offset = _offset[ch];
        for (int i = 31; i >= 0; i--)
            history[i + offset] = samples[at++] * InputScale;

        for (int i = 0; i < 64; i++)
        {
            int sum = 0;
            for (int j = 0; j < 8; j++)
                sum += Mul(history[(offset + i + (j << 6)) & 511], Window[i + (j << 6)]);
            _windowed[i] = sum;
        }
        _offset[ch] = (offset + 480) & 511;
        var subbands = _subbands[ch];

        for (int i = 0; i < 32; i++)
        {
            int sum = 0;
            int row = i * 64;
            for (int j = 0; j < 64; j++)
                sum += Mul(Filter[row + j], _windowed[j]);
            subbands[to + i] = sum;
        }
    }

    private static int Slot(int granule, int slot) => ((granule * 18) + slot) * 32;

    private void Transform(int ch, ReadOnlySpan<short> samples)
    {
        int at = 0;
        var subbands = _subbands[ch];
        for (int gr = 0; gr < 2; gr++)
        {
            for (int k = 0; k < 18; k += 2)
            {
                FilterSubbands(ch, samples, ref at, Slot(gr + 1, k));
                FilterSubbands(ch, samples, ref at, Slot(gr + 1, k + 1));
                // The filter bank inverts every odd band in every odd slot.
                int odd = Slot(gr + 1, k + 1);
                for (int band = 1; band < 32; band += 2)
                    subbands[odd + band] = -subbands[odd + band];
            }

            var spectrum = _spectrum[ch].AsSpan(gr * Granule, Granule);
            for (int band = 0; band < 32; band++)
            {
                for (int k = 0; k < 18; k++)
                {
                    _mdctIn[k] = subbands[Slot(gr, k) + band];
                    _mdctIn[k + 18] = subbands[Slot(gr + 1, k) + band];
                }
                for (int k = 0; k < 18; k++)
                {
                    int sum = 0;
                    int row = k * 36;
                    for (int j = 0; j < 36; j++)
                        sum += Mul(_mdctIn[j], Cosines[row + j]);
                    spectrum[(band * 18) + k] = sum;
                }

                if (band != 0)
                    for (int i = 0; i < 8; i++)
                    {
                        int upper = (band * 18) + i, lower = ((band - 1) * 18) + 17 - i;
                        long a = spectrum[upper], b = spectrum[lower];
                        spectrum[upper] = (int)(((a * AliasCosines[i]) - (b * AliasSines[i])) >> 31);
                        spectrum[lower] = (int)(((a * AliasSines[i]) + (b * AliasCosines[i])) >> 31);
                    }
            }
        }
        // The frame's last granule is the next one's first half.
        Array.Copy(subbands, Slot(2, 0), subbands, 0, 18 * 32);
    }

    // ------------------------------------------------------------------ quantising

    private void Quantise()
    {
        for (int gr = 0; gr < 2; gr++)
        for (int ch = 0; ch < _channels; ch++)
        {
            var spectrum = _spectrum[ch].AsSpan(gr * Granule, Granule);
            var quantised = _quantised[ch].AsSpan(gr * Granule, Granule);
            _loudest = 0;
            for (int i = 0; i < Granule; i++)
            {
                long magnitude = Math.Abs((long)spectrum[i]);
                _magnitudes[i] = (int)Math.Min(magnitude, int.MaxValue);
                _loudest = Math.Max(_loudest, _magnitudes[i]);
            }

            var info = _granules[gr, ch];
            int most = Math.Min(4095, _meanBits);
            info.Part23Length = 0;
            info.BigValues = info.Count1 = info.Region0Count = info.Region1Count = info.Count1Table = 0;
            info.Address1 = info.Address2 = info.Address3 = 0;
            info.TableSelect.AsSpan().Clear();

            if (_loudest != 0)
            {
                info.Step = SearchStep(most, quantised, info);
                info.Part23Length = InnerLoop(quantised, most, info);
            }
            _reservoir += _meanBits - info.Part23Length;
            info.GlobalGain = info.Step + 210;
        }

        // No reservoir carried between frames: what a frame does not use is stuffing in it.
        int stuffing = Math.Max(0, _reservoir);
        _reservoir -= stuffing;
        foreach (var info in _granules)
        {
            int extra = Math.Min(4095 - info.Part23Length, stuffing);
            info.Part23Length += extra;
            stuffing -= extra;
        }
        _ancillary = stuffing;
    }

    /// <summary>The step size that quantises the granule into <paramref name="most"/> bits.</summary>
    private int InnerLoop(Span<int> quantised, int most, GranuleInfo info)
    {
        int bits;
        if (most < 0)
            info.Step--;
        do
        {
            while (info.Step < MostStep && Quantise(quantised, ++info.Step) > 8192)
            {
            }
            bits = CountBits(quantised, info);
        }
        while (bits > most && info.Step < MostStep);
        return bits;
    }

    /// <summary>A first step size, by successive halving.</summary>
    private int SearchStep(int desired, Span<int> quantised, GranuleInfo info)
    {
        int next = -120, count = 120;
        do
        {
            int half = count / 2;
            int bits = Quantise(quantised, next + half) > 8192 ? 100000 : CountBits(quantised, info);
            if (bits < desired)
                count = half;
            else
            {
                next += half;
                count -= half;
            }
        }
        while (count > 1);
        return next;
    }

    private int CountBits(Span<int> quantised, GranuleInfo info)
    {
        RunLengths(quantised, info);
        int bits = Count1Bits(quantised, info);
        Subdivide(info);
        info.TableSelect[0] = info.Address1 > 0 ? ChooseTable(quantised, 0, info.Address1) : 0;
        info.TableSelect[1] = info.Address2 > info.Address1 ? ChooseTable(quantised, info.Address1, info.Address2) : 0;
        info.TableSelect[2] = (info.BigValues << 1) > info.Address2 ? ChooseTable(quantised, info.Address2, info.BigValues << 1) : 0;
        bits += CountRegion(quantised, 0, info.Address1, info.TableSelect[0]);
        bits += CountRegion(quantised, info.Address1, info.Address2, info.TableSelect[1]);
        bits += CountRegion(quantised, info.Address2, info.Address3, info.TableSelect[2]);
        return bits;
    }

    /// <summary>The granule quantised at <paramref name="step"/>; the largest value.</summary>
    private int Quantise(Span<int> quantised, int step)
    {
        int scale = StepsFixed[step + 127];
        if (MulRound(_loudest, scale) > 165140)
            return 16384;

        int max = 0;
        var spectrum = _magnitudes;
        for (int i = 0; i < Granule; i++)
        {
            int ln = MulRound(spectrum[i], scale);
            int value;
            if (ln < 10000)
                value = ThreeQuarters[ln];
            else
            {
                double x = spectrum[i] * Steps[step + 127] * 4.656612875e-10;
                value = (int)Math.Sqrt(Math.Sqrt(x) * x);
            }
            quantised[i] = value;
            if (max < value)
                max = value;
        }
        return max;
    }

    /// <summary>The granule split into big values, quadruples of 0s and 1s, and zeros.</summary>
    private static void RunLengths(Span<int> quantised, GranuleInfo info)
    {
        int i = Granule;
        while (i > 1 && quantised[i - 1] == 0 && quantised[i - 2] == 0)
            i -= 2;
        info.Count1 = 0;
        while (i > 3 && quantised[i - 1] <= 1 && quantised[i - 2] <= 1 && quantised[i - 3] <= 1 && quantised[i - 4] <= 1)
        {
            info.Count1++;
            i -= 4;
        }
        info.BigValues = i >> 1;
    }

    private static int Count1Bits(Span<int> quantised, GranuleInfo info)
    {
        int sum0 = 0, sum1 = 0;
        var a = Mp3Tables.Tables[32].Lengths;
        var b = Mp3Tables.Tables[33].Lengths;
        for (int i = info.BigValues << 1, k = 0; k < info.Count1; i += 4, k++)
        {
            int v = quantised[i], w = quantised[i + 1], x = quantised[i + 2], y = quantised[i + 3];
            int p = v + (w << 1) + (x << 2) + (y << 3);
            int signs = (v != 0 ? 1 : 0) + (w != 0 ? 1 : 0) + (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0);
            sum0 += signs + a[p];
            sum1 += signs + b[p];
        }
        info.Count1Table = sum0 < sum1 ? 0 : 1;
        return Math.Min(sum0, sum1);
    }

    private void Subdivide(GranuleInfo info)
    {
        if (info.BigValues == 0)
        {
            info.Region0Count = info.Region1Count = 0;
            info.Address1 = info.Address2 = info.Address3 = 0;
            return;
        }
        int region = 2 * info.BigValues;
        int spanned = 0;
        while (_bands[spanned] < region)
            spanned++;

        int count = Subdivisions[spanned].Region0;
        while (count > 0 && _bands[count + 1] > region)
            count--;
        info.Region0Count = count;
        info.Address1 = _bands[count + 1];

        int from = info.Region0Count + 1;
        count = Subdivisions[spanned].Region1;
        while (count > 0 && _bands[from + count + 1] > region)
            count--;
        info.Region1Count = count;
        info.Address2 = _bands[from + count + 1];
        info.Address3 = region;
    }

    /// <summary>The Huffman table coding <paramref name="begin"/>-<paramref name="end"/> in the
    /// fewest bits, of those that can hold its largest value.</summary>
    private static int ChooseTable(Span<int> quantised, int begin, int end)
    {
        int max = 0;
        for (int i = begin; i < end; i++)
            max = Math.Max(max, quantised[i]);
        if (max == 0)
            return 0;

        var tables = Mp3Tables.Tables;
        if (max < 15)
        {
            int choice = 0;
            for (int i = 1; i < 16; i++)
                if (tables[i].XLen > max)
                {
                    choice = i;
                    break;
                }
            int sum = CountRegion(quantised, begin, end, choice);
            int[] alternatives = choice switch
            {
                2 => [3],
                5 => [6],
                7 => [8, 9],
                10 => [11, 12],
                13 => [15],
                _ => [],
            };
            foreach (int other in alternatives)
            {
                int bits = CountRegion(quantised, begin, end, other);
                if (bits <= sum)
                    (choice, sum) = (other, bits);
            }
            return choice;
        }

        max -= 15;
        int first = 0, second = 0;
        for (int i = 15; i < 24; i++)
            if (tables[i].LinMax >= max)
            {
                first = i;
                break;
            }
        for (int i = 24; i < 32; i++)
            if (tables[i].LinMax >= max)
            {
                second = i;
                break;
            }
        return CountRegion(quantised, begin, end, second) < CountRegion(quantised, begin, end, first) ? second : first;
    }

    private static int CountRegion(Span<int> quantised, int start, int end, int table)
    {
        if (table == 0)
            return 0;
        var h = Mp3Tables.Tables[table];
        int sum = 0;
        for (int i = start; i < end; i += 2)
        {
            int x = quantised[i], y = quantised[i + 1];
            if (table > 15)
            {
                if (x > 14)
                {
                    x = 15;
                    sum += h.LinBits;
                }
                if (y > 14)
                {
                    y = 15;
                    sum += h.LinBits;
                }
            }
            sum += h.Lengths[(x * h.YLen) + y];
            if (x != 0)
                sum++;
            if (y != 0)
                sum++;
        }
        return sum;
    }

    // ------------------------------------------------------------------ the frame

    private void Put(uint value, int bits)
    {
        if (bits == 0)
            return;
        _bits = (_bits << bits) | (value & (uint)((1UL << bits) - 1));
        _bitCount += bits;
        while (_bitCount >= 8)
        {
            _bitCount -= 8;
            _frame[_frameLength++] = (byte)(_bits >> _bitCount);
        }
    }

    private void WriteFrame()
    {
        for (int ch = 0; ch < _channels; ch++)
            for (int i = 0; i < 2 * Granule; i++)
                if (_spectrum[ch][i] < 0 && _quantised[ch][i] > 0)
                    _quantised[ch][i] = -_quantised[ch][i];

        // The header: sync, MPEG-1, Layer III, no CRC; mono or stereo, not copyright, original.
        Put(0x7ff, 11);
        Put(3, 2);
        Put(1, 2);
        Put(1, 1);
        Put((uint)_bitRateIndex, 4);
        Put((uint)_rateIndex, 2);
        Put((uint)_padding, 1);
        Put(0, 1);
        Put((uint)(_channels == 1 ? Mono : Stereo), 2);
        Put(0, 2);
        Put(0, 1);
        Put(1, 1);
        Put(0, 2);

        // Side information: main data begins here, no private bits, no scalefactors shared.
        Put(0, 9);
        Put(0, _channels == 1 ? 5 : 3);
        Put(0, 4 * _channels);
        foreach (var info in _granules)
        {
            Put((uint)info.Part23Length, 12);
            Put((uint)info.BigValues, 9);
            Put((uint)info.GlobalGain, 8);
            Put(0, 4);
            Put(0, 1);
            foreach (int table in info.TableSelect)
                Put((uint)table, 5);
            Put((uint)info.Region0Count, 4);
            Put((uint)info.Region1Count, 3);
            Put(0, 1);
            Put(0, 1);
            Put((uint)info.Count1Table, 1);
        }

        for (int gr = 0; gr < 2; gr++)
            for (int ch = 0; ch < _channels; ch++)
                WriteGranule(_quantised[ch].AsSpan(gr * Granule, Granule), _granules[gr, ch]);
        for (; _ancillary > 0; _ancillary -= Math.Min(_ancillary, 32))
            Put(0, Math.Min(_ancillary, 32));
    }

    private void WriteGranule(Span<int> quantised, GranuleInfo info)
    {
        int written = 0;
        int bigValues = info.BigValues << 1;
        int region1 = _bands[info.Region0Count + 1];
        int region2 = _bands[info.Region0Count + 1 + info.Region1Count + 1];

        for (int i = 0; i < bigValues; i += 2)
        {
            int table = info.TableSelect[(i >= region1 ? 1 : 0) + (i >= region2 ? 1 : 0)];
            if (table != 0)
                written += WritePair(table, quantised[i], quantised[i + 1]);
        }

        var count1 = Mp3Tables.Tables[32 + info.Count1Table];
        int end = bigValues + (info.Count1 << 2);
        for (int i = bigValues; i < end; i += 4)
            written += WriteQuadruple(count1, quantised[i], quantised[i + 1], quantised[i + 2], quantised[i + 3]);

        // Stuffing, as ones: the count1 table reads them as quadruples of zeros.
        int left = info.Part23Length - written;
        for (; left >= 32; left -= 32)
            Put(uint.MaxValue, 32);
        if (left > 0)
            Put((uint)((1UL << left) - 1), left);
    }

    private int WriteQuadruple(Huffman h, int v, int w, int x, int y)
    {
        uint code = 0;
        int bits = 0;
        int p = Math.Abs(v) + (Math.Abs(w) << 1) + (Math.Abs(x) << 2) + (Math.Abs(y) << 3);
        Put(h.Codes[p], h.Lengths[p]);
        foreach (int value in (ReadOnlySpan<int>)[v, w, x, y])
            if (value != 0)
            {
                code = (code << 1) | (value < 0 ? 1u : 0u);
                bits++;
            }
        Put(code, bits);
        return h.Lengths[p] + bits;
    }

    private int WritePair(int table, int x, int y)
    {
        var h = Mp3Tables.Tables[table];
        uint signX = x < 0 ? 1u : 0u, signY = y < 0 ? 1u : 0u;
        x = Math.Abs(x);
        y = Math.Abs(y);

        if (table > 15)
        {
            int linX = 0, linY = 0;
            if (x > 14)
            {
                linX = x - 15;
                x = 15;
            }
            if (y > 14)
            {
                linY = y - 15;
                y = 15;
            }
            int index = (x * h.YLen) + y;
            uint extra = 0;
            int extraBits = 0;
            if (x > 14)
            {
                extra = (uint)linX;
                extraBits += h.LinBits;
            }
            if (x != 0)
            {
                extra = (extra << 1) | signX;
                extraBits++;
            }
            if (y > 14)
            {
                extra = (extra << h.LinBits) | (uint)linY;
                extraBits += h.LinBits;
            }
            if (y != 0)
            {
                extra = (extra << 1) | signY;
                extraBits++;
            }
            Put(h.Codes[index], h.Lengths[index]);
            Put(extra, extraBits);
            return h.Lengths[index] + extraBits;
        }
        else
        {
            int index = (x * h.YLen) + y;
            uint code = h.Codes[index];
            int bits = h.Lengths[index];
            if (x != 0)
            {
                code = (code << 1) | signX;
                bits++;
            }
            if (y != 0)
            {
                code = (code << 1) | signY;
                bits++;
            }
            Put(code, bits);
            return bits;
        }
    }
}
