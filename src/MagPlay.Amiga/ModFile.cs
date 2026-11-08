using System.Text;

namespace MagPlay.Amiga;

/// <summary>A sample of a module: 8-bit signed sound, played as Paula plays it.</summary>
/// <param name="Name">Its name - often a line of the composer's message instead.</param>
/// <param name="Data">Its bytes, signed.</param>
/// <param name="FineTune">-8 to 7, in eighths of a semitone.</param>
/// <param name="Volume">0 to 64.</param>
/// <param name="LoopStart">Where its loop starts, in bytes.</param>
/// <param name="LoopLength">How long its loop is, in bytes; 0 for a sample played once.</param>
public sealed record ModSample(string Name, sbyte[] Data, int FineTune, int Volume, int LoopStart, int LoopLength)
{
    public bool Loops => LoopLength > 2;
}

/// <summary>One channel's cell of a pattern's row: the sample, the note as Paula's period, and the
/// effect with its parameter.</summary>
public readonly record struct ModCell(int Sample, int Period, int Effect, int Parameter)
{
    public bool IsEmpty => Sample == 0 && Period == 0 && Effect == 0 && Parameter == 0;
}

/// <summary>
/// A tracker module from the Amiga: ProTracker's and NoiseTracker's 31 samples ("M.K.", "M!K!",
/// "FLT4", "4CHN"), the 6 and 8 channel ones of FastTracker and StarTrekker ("6CHN", "8CHN",
/// "FLT8", "CD81", "OKTA", "xxCH"), and Soundtracker's 15 samples with no tag. Patterns of 64 rows,
/// played in the order its list gives.
/// </summary>
public sealed class ModFile
{
    public const int Rows = 64;

    public string Title { get; private init; } = "";

    /// <summary>What the file says it is: its four-letter tag, or "Soundtracker" for 15 samples.</summary>
    public string Format { get; private init; } = "";

    public int Channels { get; private init; }

    /// <summary>Its samples, numbered from 1 as its patterns name them: [0] is sample 1.</summary>
    public IReadOnlyList<ModSample> Samples { get; private init; } = [];

    /// <summary>The patterns in the order played.</summary>
    public IReadOnlyList<int> Order { get; private init; } = [];

    /// <summary>The order position the song goes back to at its end - by the file's own byte, which
    /// ProTracker leaves at 127 and ignores.</summary>
    public int Restart { get; private init; }

    /// <summary>Each pattern's cells, [row * Channels + channel].</summary>
    public IReadOnlyList<ModCell[]> Patterns { get; private init; } = [];

    public ModCell Cell(int pattern, int row, int channel) => Patterns[pattern][row * Channels + channel];

    /// <summary>Whether <paramref name="bytes"/> look like a module: a tag known at 1080, or the shape
    /// of a 15-sample one - sensible sample sizes, an order list within its patterns.</summary>
    public static bool Looks(byte[] bytes) => bytes.Length >= 1084 && ChannelsOf(Tag(bytes)) > 0 || LooksOld(bytes);

    public static ModFile Parse(byte[] bytes)
    {
        int channels = bytes.Length >= 1084 ? ChannelsOf(Tag(bytes)) : 0;
        bool old = channels == 0;
        if (old && !LooksOld(bytes))
            throw new InvalidDataException("Not a module: no tag ProTracker knows at byte 1080, nor the shape of a 15-sample Soundtracker one.");
        if (old)
            channels = 4;

        int sampleCount = old ? 15 : 31;
        int at = 20;
        var headers = new List<(string Name, int Length, int FineTune, int Volume, int LoopStart, int LoopLength)>();
        for (int s = 0; s < sampleCount; s++, at += 30)
        {
            string name = Text(bytes, at, 22);
            int length = Word(bytes, at + 22) * 2;
            int fine = bytes[at + 24] & 0x0F;
            int volume = Math.Min(64, (int)bytes[at + 25]);
            int loopStart = Word(bytes, at + 26) * (old ? 1 : 2);
            int loopLength = Word(bytes, at + 28) * 2;
            headers.Add((name, length, fine > 7 ? fine - 16 : fine, volume, loopStart, loopLength));
        }

        int songLength = Math.Clamp((int)bytes[at], 1, 128);
        int restart = bytes[at + 1];
        var order = new int[128];
        for (int i = 0; i < 128; i++)
            order[i] = bytes[at + 2 + i] & 0x7F;
        // Every pattern the list names, played or not, is in the file.
        int patterns = order.Max() + 1;
        at += 2 + 128 + (old ? 0 : 4);

        int patternBytes = Rows * channels * 4;
        var cells = new List<ModCell[]>();
        for (int p = 0; p < patterns; p++)
        {
            var pattern = new ModCell[Rows * channels];
            for (int i = 0; i < pattern.Length; i++)
            {
                int c = at + p * patternBytes + i * 4;
                if (c + 3 >= bytes.Length)
                    break;
                pattern[i] = new ModCell((bytes[c] & 0xF0) | (bytes[c + 2] >> 4), ((bytes[c] & 0x0F) << 8) | bytes[c + 1],
                                         bytes[c + 2] & 0x0F, bytes[c + 3]);
            }
            cells.Add(pattern);
        }
        at += patterns * patternBytes;

        var samples = new List<ModSample>();
        foreach (var h in headers)
        {
            int length = Math.Max(0, Math.Min(h.Length, bytes.Length - at));
            var data = new sbyte[length];
            if (length > 0)
                Buffer.BlockCopy(bytes, at, data, 0, length);
            at += h.Length;
            // A loop past the sample's end is cut to it, as players do.
            int loopStart = Math.Min(h.LoopStart, length);
            int loopLength = Math.Min(h.LoopLength, length - loopStart);
            samples.Add(new ModSample(h.Name, data, h.FineTune, h.Volume, loopStart, loopLength > 2 ? loopLength : 0));
        }

        return new ModFile
        {
            Title = Text(bytes, 0, 20),
            Format = old ? "Soundtracker" : Tag(bytes),
            Channels = channels,
            Samples = samples,
            Order = order[..songLength],
            Restart = restart < songLength ? restart : 0,
            Patterns = cells,
        };
    }

    private static string Tag(byte[] bytes) => Encoding.ASCII.GetString(bytes, 1080, 4);

    private static int ChannelsOf(string tag) => tag switch
    {
        "M.K." or "M!K!" or "M&K!" or "FLT4" or "4CHN" or "N.T." => 4,
        "6CHN" => 6,
        "8CHN" or "FLT8" or "CD81" or "OKTA" or "OCTA" => 8,
        _ when tag.Length == 4 && tag.EndsWith("CH", StringComparison.Ordinal) && int.TryParse(tag[..2], out int n) && n is >= 2 and <= 32 => n,
        _ when tag.Length == 4 && tag.EndsWith("CHN", StringComparison.Ordinal) && tag[0] is >= '2' and <= '9' => tag[0] - '0',
        _ => 0,
    };

    /// <summary>A 15-sample Soundtracker module, by its shape: every sample's volume at most 64, its
    /// order list within 128 patterns, and the file long enough for its patterns.</summary>
    private static bool LooksOld(byte[] bytes)
    {
        if (bytes.Length < 600 + 1024)
            return false;
        for (int s = 0, at = 20; s < 15; s++, at += 30)
        {
            if (bytes[at + 25] > 64 || bytes[at + 24] > 15)
                return false;
            for (int i = at; i < at + 22; i++)
                if (bytes[i] is not 0 and (< 32 or > 126))
                    return false;
        }
        int length = bytes[470];
        if (length is 0 or > 128)
            return false;
        int patterns = 0;
        for (int i = 0; i < 128; i++)
        {
            if (bytes[472 + i] > 127)
                return false;
            patterns = Math.Max(patterns, bytes[472 + i] + 1);
        }
        return bytes.Length >= 600 + patterns * 1024;
    }

    private static int Word(byte[] bytes, int at) => (bytes[at] << 8) | bytes[at + 1];

    private static string Text(byte[] bytes, int at, int length)
    {
        var text = new StringBuilder();
        for (int i = at; i < at + length && i < bytes.Length && bytes[i] != 0; i++)
            text.Append(bytes[i] is >= 32 and < 127 ? (char)bytes[i] : ' ');
        return text.ToString().TrimEnd();
    }
}
