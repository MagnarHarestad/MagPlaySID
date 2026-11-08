using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MagPlaySid;

/// <summary>A tune's songs' lengths in songlengths.json: by its HVSC key, or by its file name for one
/// written by hand; the tune's name only to tell the entries apart.</summary>
internal sealed class SongLengthEntry
{
    public string? Tune { get; set; }

    /// <summary>Each song's length, by its number from 1, as m:ss - or m:ss.fff, or seconds.</summary>
    public Dictionary<string, string> Songs { get; set; } = [];
}

/// <summary>
/// Song lengths not measured: the user's own, which come before all others, and HVSC's for the
/// songs MagPlaySID cannot measure - a song whose music never repeats, played at random, or timed
/// by the clock. The app measures every song itself first; HVSC's list here holds only the songs
/// that measuring finds no length for, by HVSC's own key for a tune, so a tune is known wherever
/// its file is and whatever it is called.
///
/// The user's are in songlengths.json beside the settings, set from a tune's right-click in a
/// playlist or written by hand: each tune by its HVSC key - as the app writes it - or by its file
/// name, with each song's length as m:ss. A file that cannot be read is no lengths; one that cannot
/// be written is let go.
/// </summary>
internal static class SongLengths
{
    private static readonly string FilePath = Path.Combine(Settings.Folder, "songlengths.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static Dictionary<string, SongLengthEntry>? _user;

    private static Dictionary<string, Dictionary<string, string>>? _hvsc;

    private static Dictionary<string, SongLengthEntry> UserLengths => _user ??= LoadUser();

    private static Dictionary<string, Dictionary<string, string>> HvscLengths => _hvsc ??= LoadHvsc();

    /// <summary>
    /// The key HVSC knows a tune by in its list of lengths: an MD5 of its C64 data, its init and play
    /// addresses, how many songs, each song's speed - 60 for the CIA's timer, 0 for the raster - and
    /// NTSC; as sidplay2 has it. It gives HVSC's own key for every one of its 61,157 tunes.
    /// </summary>
    public static string HvscKey(byte[] file)
    {
        int Be16(int at) => (file[at] << 8) | file[at + 1];
        bool rsid = file[0] == 'R';
        int version = Be16(4), offset = Be16(6);
        int load = Be16(8), init = Be16(10), play = Be16(12), songs = Be16(14);
        uint speed = (uint)((file[18] << 24) | (file[19] << 16) | (file[20] << 8) | file[21]);
        int flags = version >= 2 && offset >= 0x7C ? Be16(0x76) : 0;
        int dataAt = offset;
        if (load == 0)
        {
            load = file[offset] | (file[offset + 1] << 8);
            dataAt += 2;
        }
        if (init == 0 && !rsid)
            init = load;

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(file, dataAt, file.Length - dataAt);
        md5.AppendData([(byte)init, (byte)(init >> 8), (byte)play, (byte)(play >> 8), (byte)songs, (byte)(songs >> 8)]);
        for (int s = 0; s < songs; s++)
            md5.AppendData([(byte)(rsid || ((speed >> Math.Min(s, 31)) & 1) != 0 ? 60 : 0)]);
        if (((flags >> 2) & 3) == 2)
            md5.AppendData([2]);
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The key a tune's lengths are kept by: HVSC's for a .sid, the file's MD5 for a module.</summary>
    public static string KeyOf(byte[] file) =>
        file.Length >= 0x7C && file[1] == 'S' && file[2] == 'I' && file[3] == 'D'
            ? HvscKey(file) : Convert.ToHexString(MD5.HashData(file)).ToLowerInvariant();

    /// <summary>The length the user gave <paramref name="song"/> of the tune - by its key, or by its
    /// file's name for one written by hand; null for none.</summary>
    public static TimeSpan? User(string key, string path, int song)
    {
        var lengths = UserLengths;
        if (!lengths.TryGetValue(key, out var entry) &&
            !lengths.TryGetValue(Path.GetFileName(path), out entry))
            return null;
        return entry.Songs.TryGetValue(song.ToString(CultureInfo.InvariantCulture), out var text) ? Parse(text) : null;
    }

    /// <summary>HVSC's length for <paramref name="song"/>, if it is one the app cannot measure.</summary>
    public static TimeSpan? Hvsc(string key, int song) =>
        HvscLengths.TryGetValue(key, out var songs) && songs.TryGetValue(song.ToString(CultureInfo.InvariantCulture), out var text)
            ? Parse(text) : null;

    /// <summary>The user's length for a song set, or with null taken away - kept in songlengths.json
    /// by the tune's key, with its name.</summary>
    public static void SetUser(string key, string path, string tune, int song, TimeSpan? length)
    {
        var lengths = UserLengths;
        string number = song.ToString(CultureInfo.InvariantCulture);
        // An entry written by hand under the file's name is kept to, not doubled.
        string at = !lengths.ContainsKey(key) && lengths.ContainsKey(Path.GetFileName(path)) ? Path.GetFileName(path) : key;
        if (!lengths.TryGetValue(at, out var entry))
            lengths[at] = entry = new SongLengthEntry { Tune = tune };
        if (length is { } set)
            entry.Songs[number] = Format(set);
        else
            entry.Songs.Remove(number);
        if (entry.Songs.Count == 0)
            lengths.Remove(at);
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(lengths, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A length as m:ss, with thousandths only when it has them.</summary>
    public static string Format(TimeSpan length)
    {
        string text = $"{(int)length.TotalMinutes}:{length.Seconds:00}";
        return length.Milliseconds == 0 ? text : $"{text}.{length.Milliseconds:000}";
    }

    /// <summary>A length as m:ss, m:ss.fff, h:mm:ss or a number of seconds; null for anything else,
    /// or none.</summary>
    public static TimeSpan? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3)
            return null;
        double seconds = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) ||
                (!last && value != Math.Floor(value)) || (i > 0 && value >= 60))
                return null;
            seconds = seconds * 60 + value;
        }
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    private static Dictionary<string, SongLengthEntry> LoadUser()
    {
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, SongLengthEntry>>(File.ReadAllText(FilePath), Json) ?? [];
            // Keys as written by hand: an MD5 in either case, a file name in any.
            return new Dictionary<string, SongLengthEntry>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, SongLengthEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, Dictionary<string, string>> LoadHvsc()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HvscLengths.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? [];
    }
}
