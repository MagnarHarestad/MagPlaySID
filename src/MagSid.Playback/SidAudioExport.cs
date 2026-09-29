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

    /// <param name="frames">How many 50Hz frames to render. A tune loops forever, so the length is
    /// the caller's decision - normally what SidSongLength found.</param>
    /// <param name="progress">Fraction rendered, 0 to 1, for a caller showing progress.</param>
    public static void Render(
        SidFile file, string path, Format format, int frames,
        ChipModel? chipModel = null, Action<double>? progress = null,
        CancellationToken cancellation = default)
    {
        if (frames <= 0)
            throw new ArgumentOutOfRangeException(nameof(frames), "A render needs at least one frame.");

        long totalSamples = (long)Math.Round(frames / 50.0 * SidFilePlaybackEngine.SampleRate);

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
