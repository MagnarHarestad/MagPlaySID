using System.Buffers.Binary;
using System.Text;

namespace MagPlay.Audio;

/// <summary>
/// A WAV file: PCM of 8, 16, 24 or 32 bits, floating point of 32 or 64, in the plain format or the
/// extensible one, and IMA and Microsoft ADPCM - any number of channels. Its tags are the INFO list's.
/// </summary>
public sealed class WavDecoder : AudioDecoder
{
    private const int Pcm = 1, MsAdpcm = 2, Float = 3, ImaAdpcm = 0x11, Extensible = 0xFFFE;

    private readonly Stream _stream;
    private readonly int _format, _bits, _blockAlign;
    private long _left;

    // ADPCM: a block decoded whole, then handed out.
    private readonly int _samplesPerBlock;
    private readonly short[] _coefficients = [];
    private readonly byte[] _block = [];
    private readonly float[] _decoded = [];
    private int _decodedAt, _decodedCount;

    private readonly byte[] _raw = new byte[65536];

    public WavDecoder(Stream stream)
    {
        _stream = stream;
        var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        string riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        reader.ReadUInt32();
        if (riff != "RIFF" || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Not a WAV file.");

        bool haveFormat = false;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            long start = stream.Position;
            if (id == "fmt ")
            {
                _format = reader.ReadUInt16();
                Channels = reader.ReadUInt16();
                SampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                _blockAlign = reader.ReadUInt16();
                _bits = reader.ReadUInt16();
                int extra = size >= 18 ? reader.ReadUInt16() : 0;
                if (_format == Extensible && extra >= 22)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    _format = reader.ReadUInt16();
                }
                else if (_format == MsAdpcm && extra >= 4)
                {
                    _samplesPerBlock = reader.ReadUInt16();
                    int count = reader.ReadUInt16();
                    _coefficients = new short[count * 2];
                    for (int i = 0; i < _coefficients.Length; i++)
                        _coefficients[i] = reader.ReadInt16();
                }
                else if (_format == ImaAdpcm && extra >= 2)
                    _samplesPerBlock = reader.ReadUInt16();
                haveFormat = true;
            }
            else if (id == "data")
            {
                if (!haveFormat)
                    throw new InvalidDataException("The WAV file's data comes before its format.");
                // A size the writer never went back to fill in, or one past the end: to the end.
                _left = size == 0 || size == 0xFFFFFFFF || start + size > stream.Length ? stream.Length - start : size;
                break;
            }
            stream.Position = start + size + (size & 1);
        }
        if (!haveFormat || Channels < 1 || SampleRate < 1)
            throw new InvalidDataException("The WAV file has no format, or no sound.");

        string kind;
        switch (_format)
        {
            case Pcm when _bits is 8 or 16 or 24 or 32:
                kind = $"WAV, {_bits}-bit";
                Duration = TimeSpan.FromSeconds((double)_left / _blockAlign / SampleRate);
                break;
            case Float when _bits is 32 or 64:
                kind = $"WAV, {_bits}-bit floating point";
                Duration = TimeSpan.FromSeconds((double)_left / _blockAlign / SampleRate);
                break;
            case ImaAdpcm or MsAdpcm when _blockAlign > 4 * Channels && Channels <= 2:
                if (_samplesPerBlock == 0)
                    _samplesPerBlock = _format == ImaAdpcm ? ((_blockAlign - (4 * Channels)) * 2 / Channels) + 1 : ((_blockAlign - (7 * Channels)) * 2 / Channels) + 2;
                if (_format == MsAdpcm && _coefficients.Length == 0)
                    _coefficients = [256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232];
                _block = new byte[_blockAlign];
                _decoded = new float[_samplesPerBlock * Channels];
                kind = _format == ImaAdpcm ? "WAV, IMA ADPCM" : "WAV, ADPCM";
                Duration = TimeSpan.FromSeconds((double)(_left / _blockAlign) * _samplesPerBlock / SampleRate);
                break;
            default:
                throw new InvalidDataException($"This WAV file's sound is in a form not played here (format {_format}, {_bits} bits).");
        }
        Description = kind;
    }

    public override int Read(Span<float> interleaved)
    {
        if (_format is ImaAdpcm or MsAdpcm)
            return ReadAdpcm(interleaved);

        int frameBytes = _blockAlign;
        int frames = (int)Math.Min(interleaved.Length / Channels, Math.Min(_raw.Length / frameBytes, _left / frameBytes));
        if (frames <= 0)
            return 0;
        int bytes = ReadFully(_raw.AsSpan(0, frames * frameBytes));
        frames = bytes / frameBytes;
        _left -= bytes;
        int count = frames * Channels;
        var raw = _raw.AsSpan(0, frames * frameBytes);
        int each = _bits / 8;
        for (int i = 0; i < count; i++)
        {
            var at = raw.Slice(i * each, each);
            interleaved[i] = (_format, _bits) switch
            {
                (Pcm, 8) => (at[0] - 128) / 128f,
                (Pcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(at) / 32768f,
                (Pcm, 24) => ((at[0] | (at[1] << 8) | (at[2] << 16)) << 8 >> 8) / 8388608f,
                (Pcm, _) => BinaryPrimitives.ReadInt32LittleEndian(at) / 2147483648f,
                (_, 32) => BinaryPrimitives.ReadSingleLittleEndian(at),
                _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(at),
            };
        }
        return count;
    }

    private int ReadFully(Span<byte> into)
    {
        int done = 0;
        while (done < into.Length)
        {
            int got = _stream.Read(into[done..]);
            if (got == 0)
                break;
            done += got;
        }
        return done;
    }

    private int ReadAdpcm(Span<float> interleaved)
    {
        int written = 0;
        while (written < interleaved.Length)
        {
            if (_decodedAt == _decodedCount)
            {
                if (_left < 4 * Channels)
                    break;
                int length = ReadFully(_block.AsSpan(0, (int)Math.Min(_blockAlign, _left)));
                _left -= length;
                _decodedCount = (_format == ImaAdpcm ? DecodeIma(_block.AsSpan(0, length)) : DecodeMs(_block.AsSpan(0, length))) * Channels;
                _decodedAt = 0;
                if (_decodedCount == 0)
                    break;
            }
            int take = Math.Min(_decodedCount - _decodedAt, interleaved.Length - written);
            take -= take % Channels;
            if (take == 0)
                break;
            _decoded.AsSpan(_decodedAt, take).CopyTo(interleaved[written..]);
            _decodedAt += take;
            written += take;
        }
        return written;
    }

    private static readonly int[] ImaSteps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894,
        6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    private static readonly int[] ImaIndex = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    /// <summary>An IMA ADPCM block: each channel's first sample and step in a header, then four bytes
    /// - eight samples - of each channel in turn, the low nibble first.</summary>
    private int DecodeIma(ReadOnlySpan<byte> block)
    {
        int channels = Channels;
        if (block.Length < 4 * channels)
            return 0;
        Span<int> predictor = stackalloc int[channels], index = stackalloc int[channels];
        for (int c = 0; c < channels; c++)
        {
            predictor[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(4 * c)..]);
            index[c] = Math.Clamp((int)block[(4 * c) + 2], 0, 88);
            _decoded[c] = predictor[c] / 32768f;
        }
        int frames = Math.Min(_samplesPerBlock, ((block.Length - (4 * channels)) * 2 / channels) + 1);
        int at = 4 * channels;
        for (int group = 0; 1 + (group * 8) < frames; group++)
        {
            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < 8; i++)
                {
                    int frame = 1 + (group * 8) + i;
                    int b = block[at + (i / 2)];
                    int nibble = (i & 1) == 0 ? b & 15 : b >> 4;
                    int step = ImaSteps[index[c]];
                    int diff = step >> 3;
                    if ((nibble & 4) != 0) diff += step;
                    if ((nibble & 2) != 0) diff += step >> 1;
                    if ((nibble & 1) != 0) diff += step >> 2;
                    predictor[c] = Math.Clamp(predictor[c] + ((nibble & 8) != 0 ? -diff : diff), -32768, 32767);
                    index[c] = Math.Clamp(index[c] + ImaIndex[nibble], 0, 88);
                    if (frame < frames)
                        _decoded[(frame * channels) + c] = predictor[c] / 32768f;
                }
                at += 4;
            }
        }
        return frames;
    }

    private static readonly int[] MsAdaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    /// <summary>A Microsoft ADPCM block: each channel's predictor, step and two first samples in a
    /// header - the second first - then nibbles, the high one first, the channels in turn.</summary>
    private int DecodeMs(ReadOnlySpan<byte> block)
    {
        int channels = Channels;
        if (block.Length < 7 * channels)
            return 0;
        Span<int> c1 = stackalloc int[channels], c2 = stackalloc int[channels], delta = stackalloc int[channels];
        Span<int> s1 = stackalloc int[channels], s2 = stackalloc int[channels];
        int at = 0;
        for (int c = 0; c < channels; c++)
        {
            int predictor = Math.Min(block[at++], (_coefficients.Length / 2) - 1);
            c1[c] = _coefficients[predictor * 2];
            c2[c] = _coefficients[(predictor * 2) + 1];
        }
        for (int c = 0; c < channels; c++, at += 2)
            delta[c] = BinaryPrimitives.ReadInt16LittleEndian(block[at..]);
        for (int c = 0; c < channels; c++, at += 2)
            s1[c] = BinaryPrimitives.ReadInt16LittleEndian(block[at..]);
        for (int c = 0; c < channels; c++, at += 2)
            s2[c] = BinaryPrimitives.ReadInt16LittleEndian(block[at..]);
        for (int c = 0; c < channels; c++)
        {
            _decoded[c] = s2[c] / 32768f;
            _decoded[channels + c] = s1[c] / 32768f;
        }

        int frames = Math.Min(_samplesPerBlock, 2 + ((block.Length - at) * 2 / channels));
        int nibbleAt = 0;
        for (int frame = 2; frame < frames; frame++)
        {
            for (int c = 0; c < channels; c++, nibbleAt++)
            {
                int b = block[at + (nibbleAt >> 1)];
                int nibble = (nibbleAt & 1) == 0 ? b >> 4 : b & 15;
                int signed = nibble >= 8 ? nibble - 16 : nibble;
                int predicted = ((s1[c] * c1[c]) + (s2[c] * c2[c])) >> 8;
                int sample = Math.Clamp(predicted + (signed * delta[c]), -32768, 32767);
                s2[c] = s1[c];
                s1[c] = sample;
                delta[c] = Math.Max(16, (MsAdaptation[nibble] * delta[c]) >> 8);
                _decoded[(frame * channels) + c] = sample / 32768f;
            }
        }
        return frames;
    }

    /// <summary>A WAV file's INFO list: its name, artist, album and date.</summary>
    public static AudioTags ReadTags(Stream stream)
    {
        string? title = null, artist = null, album = null, year = null;
        var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 12;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            long start = stream.Position;
            if (id == "LIST" && size >= 4 && Encoding.ASCII.GetString(reader.ReadBytes(4)) == "INFO")
            {
                while (stream.Position + 8 <= start + size)
                {
                    string key = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    int length = (int)reader.ReadUInt32();
                    long at = stream.Position;
                    string value = Encoding.Latin1.GetString(reader.ReadBytes(Math.Min(length, 4096))).TrimEnd('\0', ' ');
                    switch (key)
                    {
                        case "INAM": title = value; break;
                        case "IART": artist = value; break;
                        case "IPRD": album = value; break;
                        case "ICRD": year = value; break;
                    }
                    stream.Position = at + length + (length & 1);
                }
            }
            if (id == "data" && (size == 0 || size == 0xFFFFFFFF))
                break;
            stream.Position = start + size + (size & 1);
        }
        return new AudioTags(Empty(title), Empty(artist), Empty(album), Empty(year));
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public override void Dispose()
    {
        _stream.Dispose();
        base.Dispose();
    }
}
