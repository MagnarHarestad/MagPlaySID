using MagSid.Sid;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace MagSid.Playback;

/// <summary>
/// Renders a tune to a file by playing it, faster than real time and without a sound card.
///
/// The same engine the player window uses, read in a loop instead of by the audio device - so what
/// lands in the file is what comes out of the speakers, down to the reSID filter's own state, rather
/// than a second rendering path that could drift from it.
/// </summary>
public static class SidAudioExport
{
    /// <summary>WAV always; the compressed formats only where Windows has an encoder for them.</summary>
    public enum Format
    {
        Wav,
        Mp3,
        Flac,
    }

    /// <summary>
    /// The encoders go through Media Foundation, which is part of Windows rather than something
    /// shipped here - so no native library travels with the editor and the single-file build stays
    /// one file. What Windows actually has varies: N editions without the Media Feature Pack have
    /// no MP3 encoder, and FLAC arrived only in Windows 10. So this is asked rather than assumed.
    /// </summary>
    public static bool IsAvailable(Format format)
    {
        if (format == Format.Wav)
            return true;

        // Media Foundation is Windows'; on a Mac or Linux only WAV is written for now.
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            MediaFoundationApi.Startup();
            return MediaFoundationEncoder.GetOutputMediaTypes(SubtypeOf(format)).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The formats this machine can actually write, WAV first.</summary>
    public static IReadOnlyList<Format> AvailableFormats() =>
        [.. Enum.GetValues<Format>().Where(IsAvailable)];

    /// <summary>Which format a filename asks for, by its extension. WAV for anything else, since
    /// that is the one always available.</summary>
    public static Format FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => Format.Mp3,
        ".flac" => Format.Flac,
        _ => Format.Wav,
    };

    public static string ExtensionOf(Format format) => format switch
    {
        Format.Mp3 => ".mp3",
        Format.Flac => ".flac",
        _ => ".wav",
    };

    /// <summary>
    /// Picks the encoder's output type, matching bit depth before anything else.
    ///
    /// NAudio's own SelectMediaType orders only by how close the bitrate is, which is right for a
    /// lossy format and wrong for FLAC: FLAC advertises 8, 16 and 24-bit types at one sample rate
    /// all carrying the same nominal bitrate, so that ordering can land on 8-bit, and the sink
    /// writer then refuses the 16-bit input with an error that names the media type rather than the
    /// mismatch. Matching the depth first is what makes FLAC work at all.
    ///
    /// Not every type carries a bit depth - MP3's do not, and asking throws - so a type without one
    /// simply does not lose on that count.
    /// </summary>
    private static MediaType? SelectOutputType(Format format, WaveFormat input)
    {
        // Lossless, so a bitrate is not a quality setting: ask high and take the best offered.
        int desiredBitRate = format == Format.Flac ? 1_000_000 : 192_000;

        return MediaFoundationEncoder.GetOutputMediaTypes(SubtypeOf(format))
            .Where(type => type.SampleRate == input.SampleRate && type.ChannelCount == input.Channels)
            .OrderBy(type => BitsPerSampleOf(type) is { } bits && bits != input.BitsPerSample ? 1 : 0)
            .ThenBy(type => Math.Abs(desiredBitRate - (type.AverageBytesPerSecond * 8)))
            .FirstOrDefault();
    }

    private static int? BitsPerSampleOf(MediaType type)
    {
        try
        {
            return type.BitsPerSample;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Guid SubtypeOf(Format format) => format switch
    {
        Format.Mp3 => AudioSubtypes.MFAudioFormat_MP3,
        Format.Flac => AudioSubtypes.MFAudioFormat_FLAC,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "WAV is not encoded."),
    };

    /// <summary>
    /// Reads a length a person typed: "02:35:01" as minutes, seconds and hundredths, "3:30"
    /// without them, or plain seconds as "210".
    ///
    /// Hundredths rather than thousandths, because two digits can only mean one thing - "02:35:01"
    /// is unambiguous where "3:30:5" would sit between two answers a hundred-fold apart. A
    /// hundredth is 10ms, still finer than the 20ms frame the player runs on, so the precision is
    /// real rather than decorative.
    ///
    /// Null for anything that is not a length, zero and negatives included: a render of no length
    /// is not a smaller export, it is an empty file, and silently making one would look like the
    /// export had failed for some other reason.
    /// </summary>
    public static TimeSpan? ParseLength(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        string[] parts = trimmed.Split(':');
        if (parts.Length > 3)
            return null;

        if (!int.TryParse(parts[0], out int first) || first < 0)
            return null;

        if (parts.Length == 1)
            return first > 0 ? TimeSpan.FromSeconds(first) : null;

        if (!int.TryParse(parts[1], out int seconds) || seconds is < 0 or > 59)
            return null;

        int hundredths = 0;
        if (parts.Length == 3
            && (!int.TryParse(parts[2], out hundredths) || hundredths is < 0 or > 99))
        {
            return null;
        }

        var length = new TimeSpan(0, 0, first, seconds, hundredths * 10);
        return length > TimeSpan.Zero ? length : null;
    }

    /// <summary>The spelling <see cref="ParseLength"/> reads back: mm:ss:cc.</summary>
    public static string FormatLength(TimeSpan length) =>
        $"{(int)length.TotalMinutes:00}:{length.Seconds:00}:{length.Milliseconds / 10:00}";

    /// <param name="duration">How much to render. A tune loops forever, so the length is the
    /// caller's decision - normally what SidSongLength found. Taken as a duration rather than a
    /// frame count so a typed millisecond reaches the file: a frame is 20ms, and rounding to one
    /// would make the precision on screen a fiction.</param>
    /// <param name="progress">Fraction rendered, 0 to 1, for a caller showing progress.</param>
    public static void Render(
        SidFile file, string path, Format format, TimeSpan duration,
        ChipModel? chipModel = null, Action<double>? progress = null,
        CancellationToken cancellation = default)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "A render needs a length above zero.");

        long totalSamples = (long)Math.Round(duration.TotalSeconds * SidFilePlaybackEngine.SampleRate);
        if (totalSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "That length is shorter than one sample.");

        if (format == Format.Wav)
        {
            var engine = new SidFilePlaybackEngine(file, chipModel);
            using var writer = new WaveFileWriter(path, engine.WaveFormat);
            Pump(engine, totalSamples, writer.Write, progress, cancellation);
            return;
        }

        if (!IsAvailable(format))
        {
            throw new SidPlaybackException(
                $"Windows has no {format.ToString().ToUpperInvariant()} encoder on this machine, so "
                + "that format cannot be written here. WAV always works. MP3 comes with the Media "
                + "Feature Pack, which N editions of Windows leave out, and FLAC needs Windows 10 "
                + "or later.");
        }

        // Media Foundation pulls from a provider rather than being written to, so the length is
        // enforced by a wrapper that stops the tune rather than by counting writes.
        var source = new BoundedProvider(new SidFilePlaybackEngine(file, chipModel), totalSamples, progress, cancellation);

        var mediaType = SelectOutputType(format, source.WaveFormat)
            ?? throw new SidPlaybackException(
                $"Windows' {format.ToString().ToUpperInvariant()} encoder will not take "
                + $"{source.WaveFormat.BitsPerSample}-bit mono at {source.WaveFormat.SampleRate}Hz, "
                + "which is what this player produces.");

        using var encoder = new MediaFoundationEncoder(mediaType);
        encoder.Encode(path, source);
    }

    private static void Pump(
        IWaveProvider source, long totalSamples, Action<byte[], int, int> write,
        Action<double>? progress, CancellationToken cancellation)
    {
        var buffer = new byte[SidFilePlaybackEngine.SampleRate * 2 / 10];   // a tenth of a second
        long written = 0;

        while (written < totalSamples)
        {
            cancellation.ThrowIfCancellationRequested();

            int wanted = (int)Math.Min(buffer.Length, (totalSamples - written) * 2);
            int read = source.Read(buffer, 0, wanted);
            if (read <= 0)
                break;

            write(buffer, 0, read);
            written += read / 2;
            progress?.Invoke(Math.Min(1.0, written / (double)totalSamples));
        }
    }

    /// <summary>A tune that stops after a set number of samples. The engine itself never ends - it
    /// would keep playing the loop for as long as anyone read from it.</summary>
    private sealed class BoundedProvider(
        IWaveProvider source, long totalSamples, Action<double>? progress, CancellationToken cancellation)
        : IWaveProvider
    {
        private long _written;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();

            long remaining = (totalSamples - _written) * 2;
            if (remaining <= 0)
                return 0;

            int read = source.Read(buffer, offset, (int)Math.Min(count, remaining));
            _written += read / 2;
            progress?.Invoke(Math.Min(1.0, _written / (double)totalSamples));
            return read;
        }
    }
}
