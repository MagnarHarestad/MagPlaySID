using System.Text.Json;

namespace MagSid.Editor.App;

/// <summary>
/// Small, disk-backed store for editor preferences that should outlive a single dialog and a
/// single run - the instrument editor's "show hex values GoatTracker-style" toggle, and the
/// author name typed into the .sid export, which is the same on every export and is not worth
/// retyping.
/// Lives at %AppData%\MagSid.Editor\settings.json; a missing or unreadable file is silently
/// treated as defaults rather than surfaced, since a lost UI preference is not worth interrupting
/// startup over.
/// </summary>
public static class EditorSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagSid.Editor", "settings.json");

    private static bool _showHighHex;
    private static string _sidAuthor = string.Empty;
    private static int _sidLoadAddress = MagSid.Core.SidLoadAddress.Default;
    private static bool _loaded;

    public static bool ShowHighHex
    {
        get
        {
            EnsureLoaded();
            return _showHighHex;
        }
        set
        {
            EnsureLoaded();
            if (_showHighHex == value)
                return;

            _showHighHex = value;
            Save();
        }
    }

    /// <summary>The author last exported under, offered as the default next time. Empty until
    /// one has been typed.</summary>
    public static string SidAuthor
    {
        get
        {
            EnsureLoaded();
            return _sidAuthor;
        }
        set
        {
            EnsureLoaded();
            string trimmed = value?.Trim() ?? string.Empty;
            if (_sidAuthor == trimmed)
                return;

            _sidAuthor = trimmed;
            Save();
        }
    }

    /// <summary>The address last exported to. Someone building a demo exports to the same spot
    /// every time, and $1000 is only the right default until they have chosen otherwise once.</summary>
    public static int SidLoadAddress
    {
        get
        {
            EnsureLoaded();
            return _sidLoadAddress;
        }
        set
        {
            EnsureLoaded();
            if (_sidLoadAddress == value)
                return;

            _sidLoadAddress = value;
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath)) is { } stored)
            {
                _showHighHex = stored.ShowHighHex;
                _sidAuthor = stored.SidAuthor ?? string.Empty;
                // Absent in settings written before the address was settable, and 0 there means
                // "never chosen" rather than "load at $0000".
                if (stored.SidLoadAddress is > 0)
                    _sidLoadAddress = stored.SidLoadAddress.Value;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults rather than block startup.
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new StoredSettings
            {
                ShowHighHex = _showHighHex,
                SidAuthor = _sidAuthor,
                SidLoadAddress = _sidLoadAddress,
            }));
        }
        catch
        {
            // Best-effort - losing a UI preference is not worth surfacing an error for.
        }
    }

    private sealed class StoredSettings
    {
        public bool ShowHighHex { get; set; }
        public string? SidAuthor { get; set; }
        public int? SidLoadAddress { get; set; }
    }
}
