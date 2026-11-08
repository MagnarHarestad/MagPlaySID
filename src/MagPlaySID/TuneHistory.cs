using System.Security.Cryptography;
using System.Text.Json;

namespace MagPlaySid;

/// <summary>What is known of a tune only from playing it: each song's length as measured, in
/// seconds, how many times it has been played, and when last.</summary>
internal sealed class TuneRecord
{
    public Dictionary<int, double> Lengths { get; set; } = [];

    public int Plays { get; set; }

    public DateTime? LastPlayed { get; set; }

    /// <summary>The longest of its songs measured so far; null before any has been played.</summary>
    public TimeSpan? Length => Lengths.Count == 0 ? null : TimeSpan.FromSeconds(Lengths.Values.Max());
}

/// <summary>
/// The tunes played, in tunes.json beside the settings, so a playlist can sort by them without
/// measuring anything: measuring a length takes up to 14 seconds a tune, but playing one measures
/// it anyway. By the file's contents, so a tune keeps its record when the file moves. A file that
/// cannot be read is no history; one that cannot be written is let go.
/// </summary>
internal static class TuneHistory
{
    private static readonly string FilePath = Path.Combine(Settings.Folder, "tunes.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static Dictionary<string, TuneRecord>? _tunes;

    private static Dictionary<string, TuneRecord> Tunes => _tunes ??= Load();

    public static string KeyOf(byte[] file) => Convert.ToHexString(MD5.HashData(file));

    /// <summary>The record of the tune with <paramref name="key"/>; null for one never played.</summary>
    public static TuneRecord? Of(string? key) => key is not null && Tunes.TryGetValue(key, out var record) ? record : null;

    /// <summary>A song's length, now it has been measured.</summary>
    public static void Measured(string key, int song, TimeSpan length)
    {
        Record(key).Lengths[song] = length.TotalSeconds;
        Save();
    }

    /// <summary>A song's length let go, to be measured again when it plays.</summary>
    public static void Forget(string key, int song)
    {
        if (Tunes.TryGetValue(key, out var record) && record.Lengths.Remove(song))
            Save();
    }

    /// <summary>The tune has played long enough to count.</summary>
    public static void Played(string key)
    {
        var record = Record(key);
        record.Plays++;
        record.LastPlayed = DateTime.UtcNow;
        Save();
    }

    private static TuneRecord Record(string key)
    {
        if (!Tunes.TryGetValue(key, out var record))
            Tunes[key] = record = new TuneRecord();
        return record;
    }

    private static Dictionary<string, TuneRecord> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, TuneRecord>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Tunes, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
