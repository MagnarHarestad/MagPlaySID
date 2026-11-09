using System.Text;
using NLayer;

namespace MagPlay.Audio;

/// <summary>An MP3 file - MPEG-1, 2 and 2.5 Layer III, and Layers I and II - decoded by NLayer,
/// without the silence its encoder put before and after the music when its LAME tag says how much.
/// Its tags are its ID3v2 tag's, or else the ID3v1 one at its end.</summary>
public sealed class Mp3Decoder : AudioDecoder
{
    /// <summary>The samples a Layer III decoder's filter bank holds back: LAME counts them in.</summary>
    private const int DecoderDelay = 529;

    private readonly MpegFile _file;
    private float[] _buffer = new float[16384];

    /// <summary>Frames still to drop at the start; frames still to give, or -1 to the end.</summary>
    private long _skip, _left = -1;

    public Mp3Decoder(Stream stream)
    {
        var gapless = Gapless(stream);
        stream.Position = 0;
        _file = new MpegFile(stream);
        SampleRate = _file.SampleRate;
        Channels = _file.Channels;
        if (SampleRate <= 0 || Channels <= 0)
            throw new InvalidDataException("The MP3 file has no frames.");
        if (gapless is { } g)
        {
            _skip = g.Delay + DecoderDelay;
            if (g.Samples > 0)
                _left = g.Samples;
        }
        // NLayer's Duration reads every frame of the file: a long mix cold off the disk took half a
        // second of the window's. The first frame says as much: its Xing or VBRI count of frames, or
        // for a constant bit rate, the file's size.
        Duration = _left > 0 ? TimeSpan.FromSeconds((double)_left / SampleRate)
            : Estimate(stream) is { } seconds ? TimeSpan.FromSeconds(seconds)
            : _file.Duration > TimeSpan.Zero ? _file.Duration : null;
        Description = "MP3";
    }

    private static readonly int[] Mpeg1Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Rates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] SampleRates = [44100, 48000, 32000];

    /// <summary>How long a Layer III file plays, in seconds, from its first frame: the frames its
    /// Xing, Info or VBRI header counts, or else - a constant bit rate - the bytes after it at that
    /// rate; null when there is no Layer III frame to read it from.</summary>
    internal static double? Estimate(Stream stream)
    {
        try
        {
            long start = 0;
            var head = new byte[10];
            stream.Position = 0;
            if (stream.Read(head) == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                start = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F)) + ((head[5] & 0x10) != 0 ? 10 : 0);
            stream.Position = start;
            var window = new byte[16384];
            int got = stream.Read(window);
            for (int at = 0; at + 4 < got; at++)
            {
                if (window[at] != 0xFF || (window[at + 1] & 0xE0) != 0xE0)
                    continue;
                int version = (window[at + 1] >> 3) & 3, layer = (window[at + 1] >> 1) & 3;
                int rateIndex = window[at + 2] >> 4, srIndex = (window[at + 2] >> 2) & 3, padding = (window[at + 2] >> 1) & 1;
                if (version == 1 || layer != 1 || rateIndex is 0 or 15 || srIndex == 3)
                    continue;
                bool mpeg1 = version == 3;
                int sampleRate = SampleRates[srIndex] >> (mpeg1 ? 0 : version == 2 ? 1 : 2);
                int bitrate = (mpeg1 ? Mpeg1Rates : Mpeg2Rates)[rateIndex] * 1000;
                int perFrame = mpeg1 ? 1152 : 576;
                int length = ((mpeg1 ? 144 : 72) * bitrate / sampleRate) + padding;
                // A second frame where the first says: a real header, not a stray 0xFF in a tag.
                if (at + length + 2 < got && (window[at + length] != 0xFF || (window[at + length + 1] & 0xE0) != 0xE0))
                    continue;

                bool mono = (window[at + 3] >> 6) == 3;
                int side = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
                int tag = at + 4 + side;
                if (tag + 12 <= got && Encoding.ASCII.GetString(window, tag, 4) is "Xing" or "Info" && (window[tag + 7] & 1) != 0)
                {
                    long frames = ((long)window[tag + 8] << 24) | ((long)window[tag + 9] << 16) | ((long)window[tag + 10] << 8) | window[tag + 11];
                    if (frames > 0)
                        return (double)frames * perFrame / sampleRate;
                }
                int vbri = at + 4 + 32;
                if (vbri + 18 <= got && Encoding.ASCII.GetString(window, vbri, 4) == "VBRI")
                {
                    long frames = ((long)window[vbri + 14] << 24) | ((long)window[vbri + 15] << 16) | ((long)window[vbri + 16] << 8) | window[vbri + 17];
                    if (frames > 0)
                        return (double)frames * perFrame / sampleRate;
                }

                // A constant rate: what follows the first frame, less an ID3v1 tag at the end.
                long end = stream.Length;
                if (end >= 128)
                {
                    stream.Position = end - 128;
                    var v1 = new byte[3];
                    if (stream.Read(v1) == 3 && v1[0] == 'T' && v1[1] == 'A' && v1[2] == 'G')
                        end -= 128;
                }
                long bytes = end - (start + at);
                return bytes > 0 ? bytes * 8.0 / bitrate : null;
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    public override int Read(Span<float> interleaved)
    {
        int written = 0;
        while (written < interleaved.Length && _left != 0)
        {
            int want = interleaved.Length - written;
            want -= want % Channels;
            if (_left > 0)
                want = (int)Math.Min(want, _left * Channels);
            if (want == 0)
                break;
            if (_buffer.Length < want)
                _buffer = new float[want];
            int got = _file.ReadSamples(_buffer, 0, want);
            got -= got % Channels;
            if (got <= 0)
                break;
            int from = 0;
            if (_skip > 0)
            {
                int dropped = (int)Math.Min(_skip, got / Channels);
                _skip -= dropped;
                from = dropped * Channels;
            }
            int count = got - from;
            _buffer.AsSpan(from, count).CopyTo(interleaved[written..]);
            written += count;
            if (_left > 0)
                _left -= count / Channels;
        }
        return written;
    }

    /// <summary>What the first frame's Xing or Info header and LAME tag say: the samples the encoder
    /// put before the music, and how many the music is; null when there is no LAME tag.</summary>
    private static (int Delay, long Samples)? Gapless(Stream stream)
    {
        try
        {
            stream.Position = 0;
            var head = new byte[10];
            if (stream.Read(head) == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                stream.Position = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F)) + ((head[5] & 0x10) != 0 ? 10 : 0);
            else
                stream.Position = 0;

            // The first frame header, within a few kilobytes.
            var window = new byte[8192];
            int got = stream.Read(window);
            for (int at = 0; at + 4 < got; at++)
            {
                if (window[at] != 0xFF || (window[at + 1] & 0xE0) != 0xE0)
                    continue;
                int version = (window[at + 1] >> 3) & 3, layer = (window[at + 1] >> 1) & 3;
                if (version == 1 || layer != 1)
                    continue;
                bool mono = (window[at + 3] >> 6) == 3;
                bool mpeg1 = version == 3;
                int side = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
                int tag = at + 4 + side;
                if (tag + 120 + 24 > got)
                    return null;
                string id = Encoding.ASCII.GetString(window, tag, 4);
                if (id is not ("Xing" or "Info"))
                    return null;
                int flags = window[tag + 7];
                int p = tag + 8;
                long frames = 0;
                if ((flags & 1) != 0)
                {
                    frames = window[p] << 24 | window[p + 1] << 16 | window[p + 2] << 8 | window[p + 3];
                    p += 4;
                }
                if ((flags & 2) != 0)
                    p += 4;
                if ((flags & 4) != 0)
                    p += 100;
                if ((flags & 8) != 0)
                    p += 4;
                if (p + 24 > got || Encoding.ASCII.GetString(window, p, 4) is not ("LAME" or "Lavf" or "Lavc"))
                    return null;
                int delay = (window[p + 21] << 4) | (window[p + 22] >> 4);
                int padding = ((window[p + 22] & 15) << 8) | window[p + 23];
                int perFrame = mpeg1 ? 1152 : 576;
                long samples = frames > 0 ? (frames * perFrame) - delay - padding : 0;
                return (delay, Math.Max(0, samples));
            }
        }
        catch (IOException)
        {
        }
        return null;
    }

    /// <summary>An MP3's title, artist, album and year: ID3v2 first, then ID3v1.</summary>
    public static AudioTags ReadTags(Stream stream)
    {
        string? title = null, artist = null, album = null, year = null;
        var head = new byte[10];
        stream.Position = 0;
        if (stream.Read(head) == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3' && head[3] is >= 2 and <= 4)
        {
            int version = head[3];
            int size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
            var tag = new byte[Math.Min(size, 1 << 22)];
            int got = stream.Read(tag);
            int at = 0;
            // An extended header, skipped.
            if ((head[5] & 0x40) != 0 && got >= 4)
                at = version == 4 ? Synchsafe(tag, 0) : 4 + (tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);
            int idLength = version == 2 ? 3 : 4, headerLength = version == 2 ? 6 : 10;
            while (at + headerLength <= got && tag[at] != 0)
            {
                string id = Encoding.ASCII.GetString(tag, at, idLength);
                int length = version switch
                {
                    2 => tag[at + 3] << 16 | tag[at + 4] << 8 | tag[at + 5],
                    4 => Synchsafe(tag, at + 4),
                    _ => tag[at + 4] << 24 | tag[at + 5] << 16 | tag[at + 6] << 8 | tag[at + 7],
                };
                int body = at + headerLength;
                if (length <= 0 || body + length > got)
                    break;
                string? text = id[0] == 'T' ? Text(tag.AsSpan(body, length)) : null;
                switch (id)
                {
                    case "TIT2" or "TT2": title ??= text; break;
                    case "TPE1" or "TP1": artist ??= text; break;
                    case "TALB" or "TAL": album ??= text; break;
                    case "TYER" or "TYE" or "TDRC": year ??= text; break;
                }
                at = body + length;
            }
        }
        if ((title is null || artist is null) && stream.Length >= 128)
        {
            var v1 = new byte[128];
            stream.Position = stream.Length - 128;
            if (stream.Read(v1) == 128 && v1[0] == 'T' && v1[1] == 'A' && v1[2] == 'G')
            {
                string Field(int from, int length) => Encoding.Latin1.GetString(v1, from, length).TrimEnd('\0', ' ');
                title ??= Empty(Field(3, 30));
                artist ??= Empty(Field(33, 30));
                album ??= Empty(Field(63, 30));
                year ??= Empty(Field(93, 4));
            }
        }
        return new AudioTags(title, artist, album, year);
    }

    private static int Synchsafe(byte[] b, int at) => (b[at] & 0x7F) << 21 | (b[at + 1] & 0x7F) << 14 | (b[at + 2] & 0x7F) << 7 | (b[at + 3] & 0x7F);

    /// <summary>An ID3 text frame's text: its encoding byte, then Latin-1, UTF-16 with or without
    /// a byte order mark, or UTF-8 - the first of several values.</summary>
    private static string? Text(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
            return null;
        var body = frame[1..];
        bool bigEndianMark = body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF;
        bool littleEndianMark = body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE;
        string text = frame[0] switch
        {
            1 when bigEndianMark => Encoding.BigEndianUnicode.GetString(body[2..]),
            1 => Encoding.Unicode.GetString(littleEndianMark ? body[2..] : body),
            2 => Encoding.BigEndianUnicode.GetString(body),
            3 => Encoding.UTF8.GetString(body),
            _ => Encoding.Latin1.GetString(body),
        };
        int end = text.IndexOf('\0');
        return Empty(end >= 0 ? text[..end] : text);
    }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public override void Dispose()
    {
        _file.Dispose();
        base.Dispose();
    }
}
