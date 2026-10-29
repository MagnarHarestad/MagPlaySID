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
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagPlaySID", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Whether a newer release is looked for at start. Null until the user has been
    /// asked, which is the first time MagPlaySID starts: off until they say yes.</summary>
    public bool? CheckForUpdates { get; set; }

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
