using NVorbis;

namespace MagPlay.Audio;

/// <summary>
/// An Ogg Vorbis recording, decoded by NVorbis (MIT) in managed code - the same on every system - its
/// samples read a piece at a time, and a jump made by its pages' own positions.
/// </summary>
public sealed class OggDecoder : AudioDecoder
{
    private readonly VorbisReader _reader;
    private float[] _buffer = new float[8192];

    public OggDecoder(Stream stream)
    {
        try
        {
            _reader = new VorbisReader(stream, closeOnDispose: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // An Ogg file of another codec - Opus, FLAC - or a broken one.
            throw new InvalidDataException($"The Ogg file holds no Vorbis sound: {ex.Message}", ex);
        }
        SampleRate = _reader.SampleRate;
        Channels = _reader.Channels;
        if (SampleRate <= 0 || Channels <= 0)
            throw new InvalidDataException("The Ogg file holds no Vorbis sound.");
        Duration = _reader.TotalTime > TimeSpan.Zero ? _reader.TotalTime : null;
        int kbps = _reader.NominalBitrate > 0 ? _reader.NominalBitrate / 1000 : 0;
        Description = kbps > 0 ? $"Ogg Vorbis, {kbps} kbps" : "Ogg Vorbis";
        var tags = _reader.Tags;
        Tags = new AudioTags(Empty(tags.Title), Empty(tags.Artist), Empty(tags.Album), Empty(tags.Dates.FirstOrDefault()?.ToString()));
    }

    /// <summary>Its Vorbis comments: title, artist, album and date.</summary>
    public AudioTags Tags { get; }

    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public override int Read(Span<float> interleaved)
    {
        int want = interleaved.Length - (interleaved.Length % Channels);
        if (_buffer.Length < want)
            _buffer = new float[want];
        int got;
        try
        {
            got = _reader.ReadSamples(_buffer, 0, want);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or EndOfStreamException)
        {
            // A page damaged past reading: the recording ends there.
            return 0;
        }
        got -= got % Channels;
        _buffer.AsSpan(0, got).CopyTo(interleaved);
        return got;
    }

    /// <summary>Straight to the frame wanted, by the Ogg pages' granule positions.</summary>
    public override void Skip(long frames)
    {
        if (frames <= 0)
            return;
        try
        {
            _reader.SamplePosition = Math.Min(_reader.SamplePosition + frames, Math.Max(0, _reader.TotalSamples - 1));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            base.Skip(frames);
        }
    }

    public override void Dispose()
    {
        _reader.Dispose();
        base.Dispose();
    }
}
