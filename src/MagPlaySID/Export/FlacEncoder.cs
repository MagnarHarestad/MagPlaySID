using System.Security.Cryptography;
using System.Text;

namespace MagPlaySid.Export;

/// <summary>
/// A FLAC encoder for 16-bit mono or stereo: blocks of 4096 samples, each channel a constant where
/// it is one - silence - or else the best of the fixed predictors of order 0-4, its residual Rice
/// coded in as many partitions as pay; stereo as left and right, or with their difference, whichever
/// is smallest. Lossless: the decoder gets back every sample, and the MD5 of them all is
/// in the stream info to check by.
/// </summary>
internal static class FlacEncoder
{
    private const int BlockSize = 4096;
    private const int MostPartitionOrder = 8;
    private const int MostRiceParameter = 14;

    /// <summary>The whole of <paramref name="samples"/> - left and right in turn for stereo - written
    /// as a FLAC stream, with its tags as Vorbis comments.</summary>
    public static void Write(Stream output, ReadOnlySpan<short> samples, int sampleRate, IReadOnlyList<(string Key, string Value)> tags,
                             int channels = 1)
    {
        if (channels is not (1 or 2))
            throw new ArgumentException("Mono or stereo.", nameof(channels));
        var frames = new MemoryStream();
        int smallest = int.MaxValue, largest = 0;
        var bits = new BitWriter();
        int length = samples.Length / channels;
        var work = new Channels();
        for (int start = 0, number = 0; start < length; start += BlockSize, number++)
        {
            int count = Math.Min(BlockSize, length - start);
            bits.Clear();
            WriteFrame(bits, samples.Slice(start * channels, count * channels), channels, number, sampleRate, work);
            frames.Write(bits.Bytes);
            smallest = Math.Min(smallest, bits.Bytes.Length);
            largest = Math.Max(largest, bits.Bytes.Length);
        }
        if (length == 0)
            smallest = 0;
        var info = new BitWriter();
        int blocks = length < BlockSize ? Math.Max(16, length) : BlockSize;

        output.Write("fLaC"u8);

        // STREAMINFO.
        info.Put(blocks, 16);
        info.Put(blocks, 16);
        info.Put(smallest, 24);
        info.Put(largest, 24);
        info.Put(sampleRate, 20);
        info.Put(channels - 1, 3);
        info.Put(15, 5);
        info.Put(length >> 18, 18);
        info.Put(length & 0x3FFFF, 18);
        WriteBlockHeader(output, last: false, type: 0, info.Bytes.Length + 16);
        output.Write(info.Bytes);
        output.Write(MD5.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples)));

        // VORBIS_COMMENT, little-endian lengths.
        var comment = new MemoryStream();
        void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            comment.Write(BitConverter.GetBytes((uint)bytes.Length));
            comment.Write(bytes);
        }
        Text(UpdateCheck.Product);
        comment.Write(BitConverter.GetBytes((uint)tags.Count));
        foreach (var (key, value) in tags)
            Text($"{key}={value}");
        WriteBlockHeader(output, last: true, type: 4, (int)comment.Length);
        comment.Position = 0;
        comment.CopyTo(output);

        frames.Position = 0;
        frames.CopyTo(output);
    }

    private static void WriteBlockHeader(Stream output, bool last, int type, int length)
    {
        output.WriteByte((byte)((last ? 0x80 : 0) | type));
        output.WriteByte((byte)(length >> 16));
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
    }

    /// <summary>A block's channels as numbers to code, and the stereo forms: mid and side.</summary>
    private sealed class Channels
    {
        public readonly int[] Left = new int[BlockSize], Right = new int[BlockSize], Mid = new int[BlockSize], Side = new int[BlockSize];
        public readonly int[] Residual = new int[BlockSize];
    }

    private static void WriteFrame(BitWriter bits, ReadOnlySpan<short> interleaved, int channels, int number, int sampleRate, Channels work)
    {
        int count = interleaved.Length / channels;
        var left = work.Left.AsSpan(0, count);
        var right = work.Right.AsSpan(0, count);
        var mid = work.Mid.AsSpan(0, count);
        var side = work.Side.AsSpan(0, count);
        int assignment = 0;
        if (channels == 1)
            for (int i = 0; i < count; i++)
                left[i] = interleaved[i];
        else
        {
            for (int i = 0; i < count; i++)
            {
                int l = interleaved[2 * i], r = interleaved[(2 * i) + 1];
                (left[i], right[i], mid[i], side[i]) = (l, r, (l + r) >> 1, l - r);
            }
            // Of left and right, left and side, side and right, and mid and side: the pair whose
            // residuals are smallest.
            long l0 = Cost(left), r0 = Cost(right), m0 = Cost(mid), s0 = Cost(side);
            assignment = new[] { (1, l0 + r0), (8, l0 + s0), (9, s0 + r0), (10, m0 + s0) }.MinBy(c => c.Item2).Item1;
        }

        bool whole = count == BlockSize;
        bits.Put(0b11111111111110, 14);
        bits.Put(0, 1);
        bits.Put(0, 1);
        bits.Put(whole ? 12 : 7, 4);
        bits.Put(sampleRate switch { 44100 => 9, 48000 => 10, 32000 => 8, _ => 0 }, 4);
        bits.Put(assignment, 4);
        bits.Put(4, 3);
        bits.Put(0, 1);
        WriteUtf8Number(bits, number);
        if (!whole)
            bits.Put(count - 1, 16);
        bits.Put(Crc8(bits.Bytes), 8);

        switch (assignment)
        {
            case 0:
                WriteSubframe(bits, left, 16, work.Residual);
                break;
            case 1:
                WriteSubframe(bits, left, 16, work.Residual);
                WriteSubframe(bits, right, 16, work.Residual);
                break;
            case 8:
                WriteSubframe(bits, left, 16, work.Residual);
                WriteSubframe(bits, side, 17, work.Residual);
                break;
            case 9:
                WriteSubframe(bits, side, 17, work.Residual);
                WriteSubframe(bits, right, 16, work.Residual);
                break;
            default:
                WriteSubframe(bits, mid, 16, work.Residual);
                WriteSubframe(bits, side, 17, work.Residual);
                break;
        }
        bits.Align();
        bits.Put(Crc16(bits.Bytes), 16);
    }

    /// <summary>The frame's number as UTF-8 codes characters, up to 31 bits.</summary>
    private static void WriteUtf8Number(BitWriter bits, int number)
    {
        if (number < 0x80)
        {
            bits.Put(number, 8);
            return;
        }
        int continuations = number < 0x800 ? 1 : number < 0x10000 ? 2 : number < 0x200000 ? 3 : number < 0x4000000 ? 4 : 5;
        int lead = (0xFF00 >> (continuations + 1)) & 0xFF;
        bits.Put(lead | (number >> (6 * continuations)), 8);
        for (int i = continuations - 1; i >= 0; i--)
            bits.Put(0x80 | ((number >> (6 * i)) & 0x3F), 8);
    }

    /// <summary>The smallest sum of a fixed predictor's residuals over a channel: how well it codes.</summary>
    private static long Cost(ReadOnlySpan<int> block)
    {
        long best = long.MaxValue;
        for (int order = 0; order <= 4 && order < block.Length; order++)
        {
            long sum = 0;
            for (int i = order; i < block.Length; i++)
                sum += Math.Abs(Residual(block, i, order));
            best = Math.Min(best, sum);
        }
        return best;
    }

    /// <param name="depth">Bits a sample: 16, or 17 for the side channel - the difference of two.</param>
    private static void WriteSubframe(BitWriter bits, ReadOnlySpan<int> block, int depth, int[] residual)
    {
        int mask = (1 << depth) - 1;
        bool constant = true;
        for (int i = 1; i < block.Length && constant; i++)
            constant = block[i] == block[0];
        if (constant)
        {
            bits.Put(0, 8);
            bits.Put(block[0] & mask, depth);
            return;
        }

        // The predictor whose residual is smallest, of those the block is long enough for.
        int best = 0;
        long bestSum = long.MaxValue;
        for (int order = 0; order <= 4 && order < block.Length; order++)
        {
            long sum = 0;
            for (int i = order; i < block.Length; i++)
                sum += Math.Abs(Residual(block, i, order));
            if (sum < bestSum)
                (best, bestSum) = (order, sum);
        }

        Array.Clear(residual);
        for (int i = best; i < block.Length; i++)
            residual[i] = Residual(block, i, best);

        int verbatimBits = depth * block.Length;
        var (partitionOrder, cost) = BestPartitions(residual, block.Length, best);
        if (cost + (depth * best) >= verbatimBits)
        {
            bits.Put(0b00000010, 8);
            foreach (int sample in block)
                bits.Put(sample & mask, depth);
            return;
        }

        bits.Put(0b00010000 | (best << 1), 8);
        for (int i = 0; i < best; i++)
            bits.Put(block[i] & mask, depth);
        bits.Put(0, 2);
        bits.Put(partitionOrder, 4);
        int partitions = 1 << partitionOrder;
        int each = block.Length >> partitionOrder;
        for (int p = 0; p < partitions; p++)
        {
            int from = p == 0 ? best : p * each, to = (p + 1) * each;
            int k = BestParameter(residual, from, to, out _);
            bits.Put(k, 4);
            for (int i = from; i < to; i++)
            {
                uint folded = (uint)((residual[i] << 1) ^ (residual[i] >> 31));
                uint high = folded >> k;
                for (uint q = high; q > 0;)
                {
                    int zeros = (int)Math.Min(q, 32u);
                    bits.Put(0, zeros);
                    q -= (uint)zeros;
                }
                bits.Put(1, 1);
                if (k > 0)
                    bits.Put((int)(folded & ((1u << k) - 1)), k);
            }
        }
    }

    private static int Residual(ReadOnlySpan<int> s, int i, int order) => order switch
    {
        0 => s[i],
        1 => s[i] - s[i - 1],
        2 => s[i] - (2 * s[i - 1]) + s[i - 2],
        3 => s[i] - (3 * s[i - 1]) + (3 * s[i - 2]) - s[i - 3],
        _ => s[i] - (4 * s[i - 1]) + (6 * s[i - 2]) - (4 * s[i - 3]) + s[i - 4],
    };

    /// <summary>How finely to split the residual into partitions, each with its own Rice parameter:
    /// the order that codes it in the fewest bits, and those bits.</summary>
    private static (int Order, long Bits) BestPartitions(int[] residual, int length, int predictor)
    {
        int bestOrder = 0;
        long bestBits = long.MaxValue;
        for (int order = 0; order <= MostPartitionOrder; order++)
        {
            int partitions = 1 << order;
            if (length % partitions != 0 || (length >> order) <= predictor)
                break;
            int each = length >> order;
            long bits = 6;
            for (int p = 0; p < partitions; p++)
            {
                BestParameter(residual, p == 0 ? predictor : p * each, (p + 1) * each, out long partitionBits);
                bits += 4 + partitionBits;
            }
            if (bits < bestBits)
                (bestOrder, bestBits) = (order, bits);
        }
        return (bestOrder, bestBits);
    }

    private static int BestParameter(int[] residual, int from, int to, out long bits)
    {
        int best = 0;
        bits = long.MaxValue;
        for (int k = 0; k <= MostRiceParameter; k++)
        {
            long sum = (long)(to - from) * (k + 1);
            for (int i = from; i < to; i++)
                sum += (uint)((residual[i] << 1) ^ (residual[i] >> 31)) >> k;
            if (sum < bits)
                (best, bits) = (k, sum);
        }
        return best;
    }

    private static int Crc8(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
        }
        return crc;
    }

    private static int Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
        }
        return crc;
    }

    /// <summary>Bits written high first into bytes.</summary>
    private sealed class BitWriter
    {
        private byte[] _bytes = new byte[16384];
        private int _length;
        private ulong _bits;
        private int _count;

        /// <summary>The whole bytes written so far.</summary>
        public ReadOnlySpan<byte> Bytes => _bytes.AsSpan(0, _length);

        public void Clear() => (_length, _bits, _count) = (0, 0, 0);

        public void Put(int value, int bits)
        {
            if (bits == 0)
                return;
            _bits = (_bits << bits) | ((ulong)(uint)value & ((1UL << bits) - 1));
            _count += bits;
            while (_count >= 8)
            {
                _count -= 8;
                if (_length == _bytes.Length)
                    Array.Resize(ref _bytes, _bytes.Length * 2);
                _bytes[_length++] = (byte)(_bits >> _count);
            }
        }

        public void Align()
        {
            if (_count > 0)
                Put(0, 8 - _count);
        }
    }
}
