namespace MagPlay.Audio;

/// <summary>
/// A recording read from its start to its end as samples from -1 to 1, its channels in turn - read
/// a piece at a time, so an hour of it never has to be in memory.
/// </summary>
public abstract class AudioDecoder : IDisposable
{
    public int SampleRate { get; protected init; }

    public int Channels { get; protected init; }

    /// <summary>How long it plays, when the file says; null when it cannot be known without reading
    /// it all.</summary>
    public TimeSpan? Duration { get; protected init; }

    /// <summary>What it is, for the window to say: "FLAC, 24-bit".</summary>
    public string Description { get; protected init; } = "";

    /// <summary>Samples into <paramref name="interleaved"/>, a whole number of frames - every channel
    /// of an instant - and how many samples were read; 0 at the end.</summary>
    public abstract int Read(Span<float> interleaved);

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>What a recording's tags say it is.</summary>
public sealed record AudioTags(string? Title, string? Artist, string? Album, string? Year);
