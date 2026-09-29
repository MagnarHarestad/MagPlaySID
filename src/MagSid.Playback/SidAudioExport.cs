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
    /// <summary>WAV always; MP3 only where Windows has an encoder for it.</summary>
    public enum Format
    {
        Wav,
        Mp3,
    }

    /// <summary>
    /// MP3 encoding goes through Media Foundation, which is part of Windows rather than something
    /// shipped here - so no native library travels with the editor and the single-file build stays
    /// one file. Windows N editions without the Media Feature Pack have no MP3 encoder at all,
    /// which is why this is asked rather than assumed.
    /// </summary>
    public static bool IsMp3Available()
    {
        try
        {
            MediaFoundationApi.Startup();
            return MediaFoundationEncoder
                .GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_MP3)
                .Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

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

        if (!IsMp3Available())
        {
            throw new SidPlaybackException(
                "Windows has no MP3 encoder available on this machine, so only WAV can be written. "
                + "That encoder is part of the Media Feature Pack, which N editions of Windows leave out.");
        }

        // Media Foundation pulls from a provider rather than being written to, so the length is
        // enforced by a wrapper that stops the tune rather than by counting writes.
        var source = new BoundedProvider(new SidFilePlaybackEngine(file, chipModel), totalSamples, progress, cancellation);
        MediaFoundationEncoder.EncodeToMp3(source, path, 192_000);
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
