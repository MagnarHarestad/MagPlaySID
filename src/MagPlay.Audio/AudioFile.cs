namespace MagPlay.Audio;

/// <summary>Recordings: which files are one, opened for their sound, and their tags.</summary>
public static class AudioFile
{
    /// <summary>The names a recording goes by.</summary>
    public static bool IsAudioName(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac" or ".mp3" or ".ogg" or ".oga" or ".m4a" or ".aac";

    /// <summary>The start of an Ogg file: its first page.</summary>
    private static bool IsOgg(ReadOnlySpan<byte> head) => head.Length >= 4 && head[..4].SequenceEqual("OggS"u8);

    /// <summary>An MP4 file - an .m4a: its file-type box first.</summary>
    private static bool IsMp4(ReadOnlySpan<byte> head) => head.Length >= 8 && head[4..8].SequenceEqual("ftyp"u8);

    /// <summary>An ADTS frame's header - an .aac: the sync, then layer 00, as an MP3 frame never has.</summary>
    private static bool IsAdts(ReadOnlySpan<byte> head) => head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0;

    /// <summary>Whether a file's first bytes are a recording's - WAV, FLAC, Ogg, MP4 or ADTS AAC, or
    /// MP3 with or without an ID3 tag - not a .sid or a module.</summary>
    public static bool Looks(ReadOnlySpan<byte> head)
    {
        if (IsOgg(head) || IsMp4(head) || IsAdts(head))
            return true;
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WAVE"u8))
            return true;
        if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8))
            return true;
        if (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8))
            return true;
        // An MPEG audio frame's sync: eleven ones, then not a reserved version or layer.
        return head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x18) != 0x08 && (head[1] & 0x06) != 0;
    }

    /// <summary>The recording at <paramref name="path"/>, opened at its start.</summary>
    public static AudioDecoder Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            Span<byte> head = stackalloc byte[12];
            int got = stream.Read(head);
            stream.Position = 0;
            head = head[..got];
            if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8))
                return new WavDecoder(stream);
            if (head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8))
                return new FlacDecoder(stream);
            if (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8) && IsFlacAfterId3(stream))
                return new FlacDecoder(stream);
            if (IsOgg(head))
                return new OggDecoder(stream);
            if (IsMp4(head))
                return AacDecoder.Open(stream, mp4: true);
            if (IsAdts(head))
                return AacDecoder.Open(stream, mp4: false);
            return new Mp3Decoder(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsFlacAfterId3(Stream stream)
    {
        var head = new byte[10];
        stream.Position = 0;
        bool flac = false;
        if (stream.Read(head) == 10)
        {
            long size = (head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F);
            stream.Position = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
            var marker = new byte[4];
            flac = stream.Read(marker) == 4 && marker.AsSpan().SequenceEqual("fLaC"u8);
        }
        stream.Position = 0;
        return flac;
    }

    /// <summary>A recording's tags; all null when it has none, or cannot be read.</summary>
    public static AudioTags ReadTags(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            Span<byte> head = stackalloc byte[12];
            int got = stream.Read(head);
            stream.Position = 0;
            head = head[..got];
            if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8))
                return WavDecoder.ReadTags(stream);
            if ((head.Length >= 4 && head[..4].SequenceEqual("fLaC"u8)) || (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8) && IsFlacAfterId3(stream)))
                return new FlacDecoder(new NonClosing(stream)).Tags;
            if (IsOgg(head))
            {
                using var ogg = new OggDecoder(new NonClosing(stream));
                return ogg.Tags;
            }
            if (IsMp4(head))
            {
                using var m4a = AacDecoder.Open(new NonClosing(stream), mp4: true);
                return m4a.Tags;
            }
            if (IsAdts(head))
                return new AudioTags(null, null, null, null);
            return Mp3Decoder.ReadTags(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return new AudioTags(null, null, null, null);
        }
    }

    /// <summary>A stream its reader may close without closing it.</summary>
    private sealed class NonClosing(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
