using System.Text;
using SharpJaad.AAC;
using SharpJaad.ADTS;
using SharpJaad.MP4;
using SharpJaad.MP4.API;
using Decoder = SharpJaad.AAC.Decoder;

namespace MagPlay.Audio;

/// <summary>
/// An AAC recording - an .m4a, its frames in an MP4 file, or an .aac, a stream of ADTS frames -
/// decoded by SharpJaad (MIT) in managed code, AAC-LC and HE-AAC alike, the same on every system.
/// An .m4a starts where its edit list says, past the encoder's priming, as other players start it.
/// </summary>
public sealed class AacDecoder : AudioDecoder
{
    private readonly Stream _stream;
    private readonly Decoder _decoder;
    private readonly SampleBuffer _sample = new();
    private readonly AudioTrack? _track;
    private readonly ADTSDemultiplexer? _adts;

    /// <summary>Decoded and not yet taken, from <see cref="_at"/>.</summary>
    private float[] _decoded = [];
    private int _at, _count;

    /// <summary>Frames still to drop at the start: the encoder's priming, or the rest of a jump.</summary>
    private long _drop;

    /// <summary>Frames each AAC frame decodes to: 1024, or 2048 with HE-AAC's doubled rate.</summary>
    private readonly int _perFrame;

    private bool _ended;

    /// <summary>The recording in <paramref name="stream"/>: an MP4 file (.m4a) when
    /// <paramref name="mp4"/>, otherwise ADTS frames (.aac). The library's own errors - a box it
    /// cannot read, a profile it cannot decode - come as the app's other formats' do.</summary>
    public static AacDecoder Open(Stream stream, bool mp4)
    {
        try
        {
            return new AacDecoder(stream, mp4);
        }
        catch (Exception ex) when (ex is not (IOException or InvalidDataException or OutOfMemoryException))
        {
            throw new InvalidDataException($"The AAC file cannot be read: {ex.Message}", ex);
        }
    }

    private AacDecoder(Stream stream, bool mp4)
    {
        _stream = stream;
        _sample.SetBigEndian(false);
        string profile = "AAC";
        if (mp4)
        {
            var movie = new MP4Container(stream).GetMovie();
            _track = movie.GetTracks().OfType<AudioTrack>().FirstOrDefault(t => t.GetCodec()?.ToString()?.Contains("AAC", StringComparison.OrdinalIgnoreCase) == true)
                ?? throw new InvalidDataException("The MP4 file holds no AAC sound.");
            _decoder = new Decoder(_track.GetDecoderSpecificInfo());
            Tags = ReadMp4Tags(movie);
            double seconds = movie.GetDuration();
            if (seconds > 0)
                Duration = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            _adts = new ADTSDemultiplexer(stream);
            _decoder = new Decoder(_adts.GetDecoderSpecificInfo());
            Tags = new AudioTags(null, null, null, null);
        }

        // The first frame decoded now: HE-AAC doubles the rate the header gives.
        if (!DecodeNext())
            throw new InvalidDataException("The AAC file has no frames.");
        SampleRate = _sample.SampleRate;
        Channels = Math.Max(1, _sample.Channels);
        _perFrame = _count / Channels;
        if (_perFrame > 1024)
            profile = "HE-AAC";
        Description = $"{profile}, {SampleRate / 1000.0:0.#} kHz";

        // An .m4a starts where its edit list does: past the priming the encoder put before the music.
        // An .aac has no index: its length is its frames, counted by their headers alone.
        if (mp4)
            _drop = EditListStart(stream, SampleRate);
        else if (CountAdtsFrames(stream) is > 0 and var frames)
            Duration = TimeSpan.FromSeconds(frames * (double)_perFrame / SampleRate);
    }

    /// <summary>The ADTS frames in the stream, each header giving the next one's place; the stream
    /// left where it was.</summary>
    private static long CountAdtsFrames(Stream stream)
    {
        if (!stream.CanSeek)
            return 0;
        long keep = stream.Position, at = 0, frames = 0;
        try
        {
            Span<byte> header = stackalloc byte[7];
            while (at + 7 <= stream.Length)
            {
                stream.Position = at;
                if (stream.Read(header) < 7 || header[0] != 0xFF || (header[1] & 0xF6) != 0xF0)
                    break;
                int length = ((header[3] & 0x03) << 11) | (header[4] << 3) | (header[5] >> 5);
                if (length < 7)
                    break;
                frames++;
                at += length;
            }
            return frames;
        }
        catch (IOException)
        {
            return 0;
        }
        finally
        {
            stream.Position = keep;
        }
    }

    public AudioTags Tags { get; }

    public override int Read(Span<float> interleaved)
    {
        int written = 0;
        while (written < interleaved.Length)
        {
            if (_at >= _count && !DecodeNext())
                break;
            if (_drop > 0)
            {
                int dropped = (int)Math.Min(_drop * Channels, _count - _at);
                _at += dropped;
                _drop -= dropped / Channels;
                continue;
            }
            int n = Math.Min(interleaved.Length - written, _count - _at);
            n -= n % Channels;
            if (n <= 0)
                break;
            _decoded.AsSpan(_at, n).CopyTo(interleaved[written..]);
            _at += n;
            written += n;
        }
        return written;
    }

    /// <summary>On <paramref name="frames"/> frames: an .m4a by its sample table, an .aac frame by
    /// frame without decoding - then the rest of the way decoded and dropped.</summary>
    public override void Skip(long frames)
    {
        if (frames <= 0)
            return;
        long left = frames + _drop;
        _drop = 0;
        // What is decoded already counts first.
        long buffered = (_count - _at) / Channels;
        if (left <= buffered)
        {
            _at += (int)left * Channels;
            return;
        }
        left -= buffered;
        _at = _count = 0;
        // Two frames short, decoded and dropped: an AAC frame overlaps the one before it, and its
        // window shape follows that one's - the first heard is then as it would have been.
        long whole = Math.Max(0, (left / _perFrame) - 2);
        if (_track is not null && whole > 0)
        {
            double now = _track.GetNextTimeStamp();
            _track.Seek(now + (whole * _perFrame / (double)SampleRate));
            double landed = _track.GetNextTimeStamp();
            left -= (long)Math.Round((landed - now) * SampleRate);
        }
        else if (_adts is not null)
        {
            for (long i = 0; i < whole; i++)
            {
                if (!NextAdtsFrame(out _))
                {
                    _ended = true;
                    return;
                }
                left -= _perFrame;
            }
        }
        _drop = Math.Max(0, left);
    }

    private bool NextAdtsFrame(out byte[] frame)
    {
        frame = [];
        try
        {
            frame = _adts!.ReadNextFrame();
            return frame is { Length: > 0 };
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or AACException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The next AAC frame decoded into <see cref="_decoded"/>; false at the end.</summary>
    private bool DecodeNext()
    {
        while (!_ended)
        {
            byte[] frame;
            if (_track is not null)
            {
                if (!_track.HasMoreFrames())
                {
                    _ended = true;
                    return false;
                }
                try
                {
                    frame = _track.ReadNextFrame().GetData();
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NullReferenceException)
                {
                    // The sample table points past the file, or into nothing: the end.
                    _ended = true;
                    return false;
                }
            }
            else if (!NextAdtsFrame(out frame))
            {
                _ended = true;
                return false;
            }
            try
            {
                _decoder.DecodeFrame(frame, _sample);
            }
            catch (Exception ex) when (ex is AACException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or NullReferenceException)
            {
                // A damaged frame: passed over, as a player would.
                continue;
            }
            var data = _sample.Data;
            int samples = data.Length / 2;
            if (_decoded.Length < samples)
                _decoded = new float[samples];
            for (int i = 0; i < samples; i++)
                _decoded[i] = (short)(data[2 * i] | (data[(2 * i) + 1] << 8)) / 32768f;
            _at = 0;
            _count = samples - (samples % Math.Max(1, _sample.Channels));
            if (_count > 0)
                return true;
        }
        return false;
    }

    /// <summary>Where the first edit of an MP4's first track starts its media, in frames at
    /// <paramref name="rate"/>: the encoder's priming; 0 when it has no edit list.</summary>
    private static long EditListStart(Stream stream, int rate)
    {
        try
        {
            long keep = stream.Position;
            var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            long timescale = 0, start = -1;
            void Walk(long from, long to, int depth)
            {
                long at = from;
                while (at + 8 <= to && depth < 8)
                {
                    stream.Position = at;
                    long size = BigEndian(reader.ReadUInt32());
                    string type = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    long header = 8;
                    if (size == 1)
                    {
                        size = (long)BigEndian64(reader.ReadUInt64());
                        header = 16;
                    }
                    else if (size == 0)
                        size = to - at;
                    if (size < header)
                        return;
                    if (type is "moov" or "trak" or "edts" or "mdia")
                        Walk(at + header, at + size, depth + 1);
                    else if (type == "mdhd" && timescale == 0)
                    {
                        int version = reader.ReadByte();
                        reader.ReadBytes(3);
                        reader.ReadBytes(version == 1 ? 16 : 8);
                        timescale = BigEndian(reader.ReadUInt32());
                    }
                    else if (type == "elst" && start < 0)
                    {
                        int version = reader.ReadByte();
                        reader.ReadBytes(3);
                        long entries = BigEndian(reader.ReadUInt32());
                        if (entries > 0)
                        {
                            reader.ReadBytes(version == 1 ? 8 : 4);
                            start = version == 1 ? (long)BigEndian64(reader.ReadUInt64()) : (int)BigEndian(reader.ReadUInt32());
                        }
                    }
                    if (type == "trak" && start >= 0)
                        return;
                    at += size;
                }
            }
            Walk(0, stream.Length, 0);
            stream.Position = keep;
            if (start <= 0 || timescale <= 0)
                return 0;
            return (long)Math.Round(start * (double)rate / timescale);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return 0;
        }
    }

    private static uint BigEndian(uint value) =>
        (value >> 24) | ((value >> 8) & 0xFF00) | ((value << 8) & 0xFF0000) | (value << 24);

    private static ulong BigEndian64(ulong value) =>
        ((ulong)BigEndian((uint)value) << 32) | BigEndian((uint)(value >> 32));

    /// <summary>An .m4a's iTunes tags: title, artist, album and year.</summary>
    private static AudioTags ReadMp4Tags(Movie movie)
    {
        try
        {
            if (!movie.ContainsMetaData())
                return new AudioTags(null, null, null, null);
            var meta = movie.GetMetaData();
            string? Get(MetaData.Field<string> field) => meta.Get<string>(field) is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
            return new AudioTags(Get(MetaData.Field<string>.TITLE), Get(MetaData.Field<string>.ARTIST), Get(MetaData.Field<string>.ALBUM),
                                 meta.Get<DateTime>(MetaData.Field<DateTime>.RELEASE_DATE) is { Year: > 1 } date ? date.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }
        catch (Exception)
        {
            return new AudioTags(null, null, null, null);
        }
    }

    public override void Dispose()
    {
        _stream.Dispose();
        base.Dispose();
    }
}
