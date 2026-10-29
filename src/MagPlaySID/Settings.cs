using System.Text.Json;

namespace MagPlaySid;

/// <summary>
/// What MagPlaySID remembers between runs, in settings.json in the user's application data -
/// %APPDATA%\MagPlaySID on Windows, ~/.config/MagPlaySID on macOS and Linux. A file that is missing
/// or cannot be read is the defaults; one that cannot be written is let go, as the settings are
/// only a convenience.
/// </summary>
internal sealed class Settings
{
    /// <summary>Where MagPlaySID keeps what it remembers - the favourite playlists too.</summary>
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagPlaySID");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Whether a newer release is looked for at start. Null until the user has been
    /// asked, which is the first time MagPlaySID starts: off until they say yes.</summary>
    public bool? CheckForUpdates { get; set; }

    /// <summary>The bigger of the two text sizes - the rows, the tune's details and the labels a
    /// quarter larger. Small, the size it always was, until it is chosen.</summary>
    public bool BigFont { get; set; }

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
