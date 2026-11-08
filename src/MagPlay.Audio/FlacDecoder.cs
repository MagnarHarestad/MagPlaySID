using System.Text;

namespace MagPlay.Audio;

/// <summary>
/// A FLAC stream decoded: every frame's subframes - constant, verbatim, fixed and linear predictors,
/// their residual Rice coded, with escapes and wasted bits - and stereo as left and right, or with
/// their difference. 4 to 32 bits a sample, any channels. Its tags are its Vorbis comments.
/// </summary>
public sealed class FlacDecoder : AudioDecoder
{
    private readonly BitReader _bits;
    private readonly int _depth;
    private readonly int _maxBlock;
    private readonly long _total;
    private long _decodedFrames;

    private readonly long[][] _channels;
    private readonly long[] _residual;
    private float[] _out = [];
    private int _outAt, _outCount;

    public AudioTags Tags { get; }

    public FlacDecoder(Stream stream)
    {
        _bits = new BitReader(stream);
        SkipId3(stream);
        if (_bits.Bits(32) != 0x664C6143)
            throw new InvalidDataException("Not a FLAC file.");

        string? title = null, artist = null, album = null, year = null;
        bool last;
        int sampleRate = 0, channels = 0;
        do
        {
            last = _bits.Bits(1) == 1;
            int type = (int)_bits.Bits(7);
            int length = (int)_bits.Bits(24);
            if (type == 0)
            {
                _bits.Bits(16);
                _maxBlock = (int)_bits.Bits(16);
                _bits.Bits(24);
                _bits.Bits(24);
                sampleRate = (int)_bits.Bits(20);
                channels = (int)_bits.Bits(3) + 1;
                _depth = (int)_bits.Bits(5) + 1;
                _total = (long)((_bits.Bits(4) << 32) | _bits.Bits(32));
                _bits.Skip(16 * 8);
            }
            else if (type == 4)
            {
                var block = _bits.Bytes(length);
                foreach (var (key, value) in Comments(block))
                {
                    switch (key)
                    {
                        case "TITLE": title ??= value; break;
                        case "ARTIST": artist ??= value; break;
                        case "ALBUM": album ??= value; break;
                        case "DATE" or "YEAR": year ??= value; break;
                    }
                }
            }
            else
                _bits.Skip(8L * length);
        }
        while (!last);

        if (sampleRate == 0 || channels == 0)
            throw new InvalidDataException("The FLAC file has no stream information.");
        SampleRate = sampleRate;
        Channels = channels;
        if (_total > 0)
            Duration = TimeSpan.FromSeconds((double)_total / sampleRate);
        Description = $"FLAC, {_depth}-bit";
        Tags = new AudioTags(title, artist, album, year);
        int most = Math.Max(_maxBlock, 4096);
        _channels = new long[channels][];
        for (int c = 0; c < channels; c++)
            _channels[c] = new long[most];
        _residual = new long[most];
    }

    private void SkipId3(Stream stream)
    {
        // An ID3v2 tag some writers put before the stream.
        if (_bits.Peek(24) != 0x494433)
            return;
        _bits.Bits(24);
        _bits.Bits(16);
        int flags = (int)_bits.Bits(8);
        long size = 0;
        for (int i = 0; i < 4; i++)
            size = (size << 7) | (_bits.Bits(8) & 0x7F);
        _bits.Skip(8 * (size + ((flags & 0x10) != 0 ? 10 : 0)));
    }

    private static IEnumerable<(string Key, string Value)> Comments(byte[] block)
    {
        var list = new List<(string, string)>();
        try
        {
            int at = 0;
            int Length()
            {
                int value = BitConverter.ToInt32(block, at);
                at += 4;
                return value;
            }
            // The vendor string, skipped: read before adding, as at += Length() would add to the at
            // from before the length was read.
            int vendor = Length();
            at += vendor;
            int count = Length();
            for (int i = 0; i < count && at < block.Length; i++)
            {
                int length = Length();
                string comment = Encoding.UTF8.GetString(block, at, length);
                at += length;
                int equals = comment.IndexOf('=');
                if (equals > 0 && !string.IsNullOrWhiteSpace(comment[(equals + 1)..]))
                    list.Add((comment[..equals].ToUpperInvariant(), comment[(equals + 1)..].Trim()));
            }
        }
        catch (ArgumentException)
        {
        }
        return list;
    }

    public override int Read(Span<float> interleaved)
    {
        int written = 0;
        while (written < interleaved.Length)
        {
            if (_outAt == _outCount && !NextFrame())
                break;
            int take = Math.Min(_outCount - _outAt, interleaved.Length - written);
            take -= take % Channels;
            if (take == 0)
                break;
            _out.AsSpan(_outAt, take).CopyTo(interleaved[written..]);
            _outAt += take;
            written += take;
        }
        return written;
    }

    /// <summary>The next frame decoded into the output; false at the end.</summary>
    private bool NextFrame()
    {
        while (true)
        {
            if (_total > 0 && _decodedFrames >= _total)
                return false;
            if (!_bits.FindSync())
                return false;
            try
            {
                if (DecodeFrame())
                    return true;
            }
            catch (InvalidDataException)
            {
                // A damaged frame: on to the next one that starts as a frame does.
            }
            catch (IndexOutOfRangeException)
            {
            }
        }
    }

    private static readonly int[] Rates = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
    private static readonly int[] Depths = [0, 8, 12, 0, 16, 20, 24, 32];

    private bool DecodeFrame()
    {
        _bits.Bits(15);
        _bits.Bits(1);
        int sizeCode = (int)_bits.Bits(4);
        int rateCode = (int)_bits.Bits(4);
        int assignment = (int)_bits.Bits(4);
        int depthCode = (int)_bits.Bits(3);
        _bits.Bits(1);
        ReadUtf8Number();
        int size = sizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (sizeCode - 2),
            6 => (int)_bits.Bits(8) + 1,
            7 => (int)_bits.Bits(16) + 1,
            >= 8 => 256 << (sizeCode - 8),
            _ => throw new InvalidDataException("A reserved block size."),
        };
        if (rateCode == 12)
            _bits.Bits(8);
        else if (rateCode is 13 or 14)
            _bits.Bits(16);
        else if (rateCode == 15)
            throw new InvalidDataException("Not a frame.");
        _bits.Bits(8);

        int depth = depthCode == 0 ? _depth : Depths[depthCode];
        int channels = assignment < 8 ? assignment + 1 : 2;
        if (depth == 0 || channels != Channels || assignment > 10 || size > _channels[0].Length)
            throw new InvalidDataException("A frame that does not fit the stream.");

        for (int c = 0; c < channels; c++)
        {
            // The side channel has a bit more: it is a difference.
            bool side = (assignment == 8 && c == 1) || (assignment == 9 && c == 0) || (assignment == 10 && c == 1);
            DecodeSubframe(_channels[c], size, depth + (side ? 1 : 0));
        }
        _bits.Align();
        _bits.Bits(16);

        var a = _channels[0];
        if (assignment >= 8)
        {
            var b = _channels[1];
            for (int i = 0; i < size; i++)
            {
                switch (assignment)
                {
                    case 8:
                        b[i] = a[i] - b[i];
                        break;
                    case 9:
                        a[i] += b[i];
                        break;
                    default:
                        long mid = (a[i] << 1) | (b[i] & 1);
                        a[i] = (mid + b[i]) >> 1;
                        b[i] = (mid - b[i]) >> 1;
                        break;
                }
            }
        }

        int count = (int)(_total > 0 ? Math.Min(size, _total - _decodedFrames) : size);
        _decodedFrames += size;
        if (_out.Length < count * channels)
            _out = new float[count * channels];
        float scale = 1f / (1L << (depth - 1));
        for (int c = 0; c < channels; c++)
        {
            var samples = _channels[c];
            for (int i = 0; i < count; i++)
                _out[(i * channels) + c] = samples[i] * scale;
        }
        (_outAt, _outCount) = (0, count * channels);
        return count > 0;
    }

    private void ReadUtf8Number()
    {
        uint first = _bits.Bits(8);
        int more = 0;
        for (uint mask = 0x80; (first & mask) != 0 && mask > 1; mask >>= 1)
            more++;
        if (more == 1 || more > 7)
            throw new InvalidDataException("Not a frame number.");
        for (int i = 1; i < more; i++)
            if ((_bits.Bits(8) & 0xC0) != 0x80)
                throw new InvalidDataException("Not a frame number.");
    }

    private void DecodeSubframe(long[] samples, int size, int depth)
    {
        if (_bits.Bits(1) != 0)
            throw new InvalidDataException("A subframe's padding is not zero.");
        int type = (int)_bits.Bits(6);
        int wasted = 0;
        if (_bits.Bits(1) == 1)
        {
            wasted = 1;
            while (_bits.Bits(1) == 0)
                wasted++;
            depth -= wasted;
        }

        if (type == 0)
        {
            long value = _bits.Signed(depth);
            Array.Fill(samples, value, 0, size);
        }
        else if (type == 1)
        {
            for (int i = 0; i < size; i++)
                samples[i] = _bits.Signed(depth);
        }
        else if (type is >= 8 and <= 12)
        {
            int order = type - 8;
            for (int i = 0; i < order; i++)
                samples[i] = _bits.Signed(depth);
            DecodeResidual(size, order);
            for (int i = order; i < size; i++)
            {
                long predicted = order switch
                {
                    0 => 0,
                    1 => samples[i - 1],
                    2 => (2 * samples[i - 1]) - samples[i - 2],
                    3 => (3 * samples[i - 1]) - (3 * samples[i - 2]) + samples[i - 3],
                    _ => (4 * samples[i - 1]) - (6 * samples[i - 2]) + (4 * samples[i - 3]) - samples[i - 4],
                };
                samples[i] = predicted + _residual[i];
            }
        }
        else if (type >= 32)
        {
            int order = (type & 31) + 1;
            for (int i = 0; i < order; i++)
                samples[i] = _bits.Signed(depth);
            int precision = (int)_bits.Bits(4) + 1;
            if (precision == 16)
                throw new InvalidDataException("A reserved coefficient precision.");
            int shift = (int)_bits.Signed(5);
            Span<long> coefficients = stackalloc long[order];
            for (int i = 0; i < order; i++)
                coefficients[i] = _bits.Signed(precision);
            DecodeResidual(size, order);
            for (int i = order; i < size; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++)
                    sum += coefficients[j] * samples[i - 1 - j];
                samples[i] = (shift >= 0 ? sum >> shift : sum << -shift) + _residual[i];
            }
        }
        else
            throw new InvalidDataException("A reserved subframe type.");

        if (wasted > 0)
            for (int i = 0; i < size; i++)
                samples[i] <<= wasted;
    }

    private void DecodeResidual(int size, int order)
    {
        int method = (int)_bits.Bits(2);
        if (method > 1)
            throw new InvalidDataException("A reserved residual coding.");
        int parameterBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 15 : 31;
        int partitionOrder = (int)_bits.Bits(4);
        int partitions = 1 << partitionOrder;
        int each = size >> partitionOrder;
        if (each << partitionOrder != size || each < order)
            throw new InvalidDataException("Partitions that do not fit the block.");
        int at = order;
        for (int p = 0; p < partitions; p++)
        {
            int count = p == 0 ? each - order : each;
            int k = (int)_bits.Bits(parameterBits);
            if (k == escape)
            {
                int raw = (int)_bits.Bits(5);
                for (int i = 0; i < count; i++)
                    _residual[at++] = raw == 0 ? 0 : _bits.Signed(raw);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ulong folded = (_bits.Unary() << k) | (k > 0 ? _bits.Bits(k) : 0);
                    _residual[at++] = (long)(folded >> 1) ^ -(long)(folded & 1);
                }
            }
        }
    }

    public override void Dispose()
    {
        _bits.Dispose();
        base.Dispose();
    }

    /// <summary>Bits read high first from a stream, through a buffer.</summary>
    private sealed class BitReader(Stream stream) : IDisposable
    {
        private readonly byte[] _buffer = new byte[65536];
        private int _length, _at;
        private ulong _cache;
        private int _count;

        private bool Fill()
        {
            while (_count <= 56)
            {
                if (_at == _length)
                {
                    _length = stream.Read(_buffer, 0, _buffer.Length);
                    _at = 0;
                    if (_length <= 0)
                    {
                        _length = 0;
                        return _count > 0;
                    }
                }
                _cache |= (ulong)_buffer[_at++] << (56 - _count);
                _count += 8;
            }
            return true;
        }

        public uint Bits(int bits)
        {
            if (bits == 0)
                return 0;
            if (_count < bits)
            {
                Fill();
                if (_count < bits)
                    throw new InvalidDataException("The stream ends in a frame.");
            }
            uint value = (uint)(_cache >> (64 - bits));
            _cache <<= bits;
            _count -= bits;
            return value;
        }

        public uint Peek(int bits)
        {
            if (_count < bits)
                Fill();
            return _count < bits ? 0 : (uint)(_cache >> (64 - bits));
        }

        public long Signed(int bits)
        {
            if (bits == 0)
                return 0;
            ulong value = bits > 32 ? ((ulong)Bits(bits - 32) << 32) | Bits(32) : Bits(bits);
            int shift = 64 - bits;
            return (long)(value << shift) >> shift;
        }

        public ulong Unary()
        {
            ulong zeros = 0;
            while (true)
            {
                if (_count == 0 && !Fill())
                    throw new InvalidDataException("The stream ends in a frame.");
                if (_cache == 0)
                {
                    zeros += (ulong)_count;
                    _cache = 0;
                    _count = 0;
                    continue;
                }
                int leading = System.Numerics.BitOperations.LeadingZeroCount(_cache);
                if (leading >= _count)
                {
                    zeros += (ulong)_count;
                    _cache = 0;
                    _count = 0;
                    continue;
                }
                zeros += (ulong)leading;
                // A shift of 64 is no shift in C#: the stop bit as the cache's last empties it.
                _cache = leading == 63 ? 0 : _cache << (leading + 1);
                _count -= leading + 1;
                return zeros;
            }
        }

        public void Skip(long bits)
        {
            while (bits > 0)
            {
                int take = (int)Math.Min(bits, 32);
                Bits(take);
                bits -= take;
            }
        }

        public byte[] Bytes(int count)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
                bytes[i] = (byte)Bits(8);
            return bytes;
        }

        public void Align() => Bits(_count % 8);

        /// <summary>On to the next frame's sync code, at a byte; false at the end.</summary>
        public bool FindSync()
        {
            Align();
            while (true)
            {
                if (_count < 16)
                    Fill();
                if (_count < 16)
                    return false;
                if ((_cache >> 50) == 0x3FFE)
                    return true;
                Bits(8);
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
