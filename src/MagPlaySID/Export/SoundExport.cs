using System.Runtime.InteropServices;
using System.Text;
using MagPlay.Amiga;
using MagPlay.C64;
using MagSid.Playback;

namespace MagPlaySid.Export;

internal enum SoundFormat
{
    Wav,
    Flac,
    Mp3,
}

/// <summary>
/// A song's sound saved to a file: played on its own C64 - or an Amiga module on its own player, in
/// stereo as wide as it is heard - apart from the one heard, as fast as this computer goes, for the
/// song's length, faded out over a few seconds after it when it loops on, with the voices muted that
/// are muted now - and written as WAV, FLAC or MP3, its title, composer and year in the file's tags.
/// </summary>
internal static class SoundExport
{
    /// <summary>The seconds a song that loops on fades out over, after its length.</summary>
    public const double FadeSeconds = 4;

    public const int Mp3BitRate = 320;

    public static string Extension(SoundFormat format) => format switch
    {
        SoundFormat.Wav => "wav",
        SoundFormat.Flac => "flac",
        _ => "mp3",
    };

    public static string Name(SoundFormat format) => format switch
    {
        SoundFormat.Wav => "WAV",
        SoundFormat.Flac => "FLAC",
        _ => "MP3",
    };

    /// <summary>What a file of the song is called by default: the tune's name, and its song when it
    /// has several.</summary>
    public static string FileName(SidFile file, int song)
    {
        string name = string.Concat(file.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (name.Length == 0)
            name = "Tune";
        return file.Songs > 1 ? $"{name} - song {song}" : name;
    }

    /// <summary>What a file of a module is called by default: its title, or else its file's name.</summary>
    public static string FileName(ITrackerModule module, string path)
    {
        string title = string.IsNullOrWhiteSpace(module.Title) ? Path.GetFileNameWithoutExtension(path) : module.Title;
        string name = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return name.Length == 0 ? "Module" : name;
    }

    /// <summary>A module's samples, 16-bit stereo - left and right in turn - at
    /// <see cref="ModuleRate"/>, as loud as it plays in the window; <paramref name="progress"/> told
    /// how far, 0-1, as it goes.</summary>
    public static short[] Render(ITrackerModule module, TimeSpan length, bool fadeOut, IReadOnlyList<bool> muted, double stereoWidth,
                                 IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var player = module.CreatePlayer(ModuleRate, stereoWidth);
        for (int channel = 0; channel < muted.Count; channel++)
            player.SetMuted(channel, muted[channel]);

        int rate = ModuleRate;
        int heard = (int)Math.Max(rate, length.TotalSeconds * rate);
        int fade = fadeOut ? (int)(FadeSeconds * rate) : 0;
        var samples = new short[(heard + fade) * 2];
        var left = new float[rate / 10];
        var right = new float[left.Length];
        for (int at = 0; at < heard + fade;)
        {
            cancel.ThrowIfCancellationRequested();
            int count = Math.Min(left.Length, heard + fade - at);
            player.Render(left.AsSpan(0, count), right.AsSpan(0, count));
            for (int i = 0; i < count; i++)
            {
                double gain = 1;
                if (at + i >= heard)
                {
                    double remaining = 1 - ((at + i - heard + 1) / (double)fade);
                    gain = remaining * remaining;
                }
                samples[(at + i) * 2] = (short)Math.Clamp(left[i] * ModuleLoudness * gain, short.MinValue, short.MaxValue);
                samples[((at + i) * 2) + 1] = (short)Math.Clamp(right[i] * ModuleLoudness * gain, short.MinValue, short.MaxValue);
            }
            at += count;
            progress?.Report((double)at / (heard + fade));
        }
        return samples;
    }

    /// <summary>A module's rate: the window's, so the file sounds as it played.</summary>
    public const int ModuleRate = 44100;

    /// <summary>What a module's sound, -1 to 1, is multiplied by into 16 bits: as the window plays it.</summary>
    private const float ModuleLoudness = 30000;

    /// <summary>The song's samples, 16-bit mono at the engine's rate; <paramref name="progress"/>
    /// told how far, 0-1, as it goes.</summary>
    public static short[] Render(SidFile file, int song, TimeSpan length, bool fadeOut, IReadOnlyList<bool> muted,
                                 IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var engine = new C64PlaybackEngine(file, song);
        for (int voice = 0; voice < muted.Count; voice++)
            engine.SetVoiceMuted(voice, muted[voice]);

        int rate = C64PlaybackEngine.SampleRate;
        int heard = (int)Math.Max(rate, length.TotalSeconds * rate);
        int fade = fadeOut ? (int)(FadeSeconds * rate) : 0;
        var samples = new short[heard + fade];
        var buffer = new byte[rate / 10 * 2];
        for (int at = 0; at < samples.Length;)
        {
            cancel.ThrowIfCancellationRequested();
            int count = Math.Min(buffer.Length / 2, samples.Length - at);
            engine.Read(buffer, 0, count * 2);
            MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, count * 2)).CopyTo(samples.AsSpan(at));
            at += count;
            progress?.Report((double)at / samples.Length);
        }

        // A fade as the ear hears one: down the square of a falling line.
        for (int i = 0; i < fade; i++)
        {
            double left = 1 - ((i + 1) / (double)fade);
            samples[heard + i] = (short)(samples[heard + i] * left * left);
        }
        return samples;
    }

    public static void Write(Stream output, SoundFormat format, ReadOnlySpan<short> samples, SidFile file, int song) =>
        Write(output, format, samples, C64PlaybackEngine.SampleRate, 1, Tags(file, song));

    /// <summary>A module's stereo samples from <see cref="Render(ITrackerModule, TimeSpan, bool, IReadOnlyList{bool}, double, IProgress{double}?, CancellationToken)"/>,
    /// its title as the file's.</summary>
    public static void Write(Stream output, SoundFormat format, ReadOnlySpan<short> samples, ITrackerModule module, string path)
    {
        var tags = new List<Tag>();
        string title = string.IsNullOrWhiteSpace(module.Title) ? Path.GetFileNameWithoutExtension(path) : module.Title.Trim();
        tags.Add(new Tag("TITLE", "TIT2", "INAM", title));
        tags.Add(new Tag("ENCODER", "TSSE", "ISFT", UpdateCheck.Product));
        Write(output, format, samples, ModuleRate, 2, tags);
    }

    private static void Write(Stream output, SoundFormat format, ReadOnlySpan<short> samples, int rate, int channels, List<Tag> tags)
    {
        switch (format)
        {
            case SoundFormat.Wav:
                WriteWav(output, samples, rate, channels, tags);
                break;
            case SoundFormat.Flac:
                FlacEncoder.Write(output, samples, rate, [.. tags.Select(t => (t.Vorbis, t.Value))], channels);
                break;
            default:
                WriteMp3(output, samples, rate, channels, tags);
                break;
        }
    }

    private sealed record Tag(string Vorbis, string Id3, string Riff, string Value);

    private static List<Tag> Tags(SidFile file, int song)
    {
        var tags = new List<Tag>();
        void Add(string vorbis, string id3, string riff, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                tags.Add(new Tag(vorbis, id3, riff, value.Trim()));
        }
        Add("TITLE", "TIT2", "INAM", file.Songs > 1 ? $"{file.Name} (song {song})" : file.Name);
        Add("ARTIST", "TPE1", "IART", file.Author);
        Add("COPYRIGHT", "TCOP", "ICOP", file.Released);
        string year = new(file.Released.TakeWhile(char.IsDigit).ToArray());
        if (year.Length == 4)
            Add("DATE", "TYER", "ICRD", year);
        Add("ENCODER", "TSSE", "ISFT", UpdateCheck.Product);
        return tags;
    }

    private static void WriteWav(Stream output, ReadOnlySpan<short> samples, int rate, int channels, List<Tag> tags)
    {
        var info = new MemoryStream();
        info.Write("INFO"u8);
        foreach (var tag in tags)
        {
            var text = Encoding.Latin1.GetBytes(tag.Value + "\0");
            info.Write(Encoding.ASCII.GetBytes(tag.Riff));
            info.Write(BitConverter.GetBytes(text.Length));
            info.Write(text);
            if (text.Length % 2 != 0)
                info.WriteByte(0);
        }

        int data = samples.Length * 2;
        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(4 + (8 + 16) + (8 + (int)info.Length) + (8 + data));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * 2 * channels);
        writer.Write((short)(2 * channels));
        writer.Write((short)16);
        writer.Write("LIST"u8);
        writer.Write((int)info.Length);
        writer.Write(info.ToArray());
        writer.Write("data"u8);
        writer.Write(data);
        writer.Flush();
        output.Write(MemoryMarshal.AsBytes(samples));
    }

    private static void WriteMp3(Stream output, ReadOnlySpan<short> samples, int rate, int channels, List<Tag> tags)
    {
        // An ID3v2.3 tag first, its text in UTF-16 with a byte order mark.
        var frames = new MemoryStream();
        foreach (var tag in tags)
        {
            var text = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(tag.Value)).ToArray();
            frames.Write(Encoding.ASCII.GetBytes(tag.Id3));
            int size = text.Length + 1;
            frames.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 0, 0, 1]);
            frames.Write(text);
        }
        int length = (int)frames.Length;
        output.Write("ID3"u8);
        output.Write([3, 0, 0, (byte)((length >> 21) & 0x7F), (byte)((length >> 14) & 0x7F), (byte)((length >> 7) & 0x7F), (byte)(length & 0x7F)]);
        frames.Position = 0;
        frames.CopyTo(output);

        // Its frames, and one of silence more: the filter bank and MDCT hold back the last of the sound.
        Mp3Encoder.EncodeAll(output, samples, rate, Mp3BitRate, channels);
    }
}
