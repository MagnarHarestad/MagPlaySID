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

    /// <summary>Whether the playlist strip is shown - Show playlists in the menu. Null until the user
    /// has chosen: shown then when there are favourites to show.</summary>
    public bool? ShowPlaylists { get; set; }

    /// <summary>Whether a playlist plays every song of a tune - all its sub-tunes, one after another -
    /// before the next tune, or only the song it starts on.</summary>
    public bool PlayEverySong { get; set; }

    /// <summary>Where the volume knob stands, 0-1; as loud as the tune is until it is turned.</summary>
    public double Volume { get; set; } = 1;

    /// <summary>How far apart an Amiga module's left and right are, 0 mono to 1 the Amiga's own.</summary>
    public double StereoWidth { get; set; } = StereoSlider.Usual;

    /// <summary>The stereo width chosen for each other kind of module - "S3M", "XM", "IT" - whose
    /// instruments are panned where their composer put them: as the module has them until changed.</summary>
    public Dictionary<string, double> StereoWidths { get; set; } = [];

    /// <summary>Which meter shows the sound's spectrum - Meter in the menu, or a click on it.</summary>
    public MeterKind Meter { get; set; }

    /// <summary>Where the user left off, to start from again: the tune last played and its song -
    /// null before any has played.</summary>
    public string? LastTune { get; set; }

    public int LastSong { get; set; }

    /// <summary>The playlist the last tune played from, by name; null for a tune played on its own.</summary>
    public string? LastQueue { get; set; }

    /// <summary>The playlist shown when the program was closed, by name.</summary>
    public string? LastShown { get; set; }

    /// <summary>How tall the playlists were dragged in the strip, in pixels; 0 for as many as fit.</summary>
    public double PlaylistsHeight { get; set; }

    /// <summary>Where the playlists and the tunes shown were scrolled to when the program was closed,
    /// to open on the same view: the row at the top of each list, and how many pixels of it were
    /// scrolled past - kept by row, so a bigger font or a composer's line read late opens on the same
    /// tunes. Below zero, none kept.</summary>
    public int PlaylistsTop { get; set; } = -1;

    public double PlaylistsInto { get; set; }

    public int TunesTop { get; set; } = -1;

    public double TunesInto { get; set; }

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
