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
    private static bool _swapSpacebar;
    private static bool _compactLoops;
    private static string _sidAuthor = string.Empty;
    private static int _sidLoadAddress = MagSid.Core.SidLoadAddress.Default;
    private static string _sidOpenDirectory = string.Empty;
    private static string _audioExportDirectory = string.Empty;
    private static string _instrumentDirectory = string.Empty;
    private static string _midiImportDirectory = string.Empty;
    private static string _midiDevice = string.Empty;
    private static string _midiKnobBindings = string.Empty;
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

    /// <summary>False - the default - gives Space the Edit/Jam toggle. True hands it the
    /// transport instead, and F5 takes over Edit/Jam. Which one a tracker musician wants depends
    /// entirely on which tracker they came from, so it is a preference rather than a choice to
    /// make for them.</summary>
    public static bool SwapSpacebar
    {
        get
        {
            EnsureLoaded();
            return _swapSpacebar;
        }
        set
        {
            EnsureLoaded();
            if (_swapSpacebar == value)
                return;

            _swapSpacebar = value;
            Save();
        }
    }

    /// <summary>False - the default - draws the retro view's LS/LE loops unrolled, once per
    /// pass. True draws a loop's rows once, as the view always used to.</summary>
    public static bool CompactLoops
    {
        get
        {
            EnsureLoaded();
            return _compactLoops;
        }
        set
        {
            EnsureLoaded();
            if (_compactLoops == value)
                return;

            _compactLoops = value;
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

    /// <summary>Where a .sid was last opened from, for the player's own file dialog.</summary>
    public static string SidOpenDirectory
    {
        get => ExistingDirectory(ref _sidOpenDirectory);
        set => SetDirectory(ref _sidOpenDirectory, value);
    }

    /// <summary>
    /// Where audio was last written, kept apart from <see cref="SidOpenDirectory"/> on purpose.
    ///
    /// A save dialog with no directory of its own inherits whichever one the process used last, so
    /// exporting after opening a tune offered to write the audio straight back into the folder the
    /// tunes live in. That is how a three-megabyte render ended up committed to this repository.
    /// </summary>
    public static string AudioExportDirectory
    {
        get => ExistingDirectory(ref _audioExportDirectory);
        set => SetDirectory(ref _audioExportDirectory, value);
    }

    /// <summary>Where an instrument file was last loaded or saved.</summary>
    public static string InstrumentDirectory
    {
        get => ExistingDirectory(ref _instrumentDirectory);
        set => SetDirectory(ref _instrumentDirectory, value);
    }

    /// <summary>Where a MIDI file was last imported from.</summary>
    public static string MidiImportDirectory
    {
        get => ExistingDirectory(ref _midiImportDirectory);
        set => SetDirectory(ref _midiImportDirectory, value);
    }

    /// <summary>The MIDI input last used, by name rather than index - a device's number moves when
    /// another is plugged in, and reopening by number would quietly attach to the wrong one.</summary>
    public static string MidiDevice
    {
        get { EnsureLoaded(); return _midiDevice; }
        set
        {
            EnsureLoaded();
            string trimmed = value?.Trim() ?? string.Empty;
            if (_midiDevice == trimmed) return;
            _midiDevice = trimmed;
            Save();
        }
    }

    /// <summary>Which controller turns which knob, as MidiKnobBindings spells it.</summary>
    public static string MidiKnobBindings
    {
        get { EnsureLoaded(); return _midiKnobBindings; }
        set
        {
            EnsureLoaded();
            string text = value ?? string.Empty;
            if (_midiKnobBindings == text) return;
            _midiKnobBindings = text;
            Save();
        }
    }

    /// <summary>Empty for a folder that has since been deleted or moved - a dialog pointed at one
    /// that no longer exists opens somewhere arbitrary rather than where it was told.</summary>
    private static string ExistingDirectory(ref string field)
    {
        EnsureLoaded();
        return field.Length > 0 && Directory.Exists(field) ? field : string.Empty;
    }

    private static void SetDirectory(ref string field, string? value)
    {
        EnsureLoaded();
        string trimmed = value?.Trim() ?? string.Empty;
        if (field == trimmed)
            return;

        field = trimmed;
        Save();
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
                _swapSpacebar = stored.SwapSpacebar;
                _compactLoops = stored.CompactLoops;
                _sidAuthor = stored.SidAuthor ?? string.Empty;
                // Absent in settings written before the address was settable, and 0 there means
                // "never chosen" rather than "load at $0000".
                if (stored.SidLoadAddress is > 0)
                    _sidLoadAddress = stored.SidLoadAddress.Value;

                _sidOpenDirectory = stored.SidOpenDirectory ?? string.Empty;
                _audioExportDirectory = stored.AudioExportDirectory ?? string.Empty;
                _instrumentDirectory = stored.InstrumentDirectory ?? string.Empty;
                _midiImportDirectory = stored.MidiImportDirectory ?? string.Empty;
                _midiDevice = stored.MidiDevice ?? string.Empty;
                _midiKnobBindings = stored.MidiKnobBindings ?? string.Empty;
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
                SwapSpacebar = _swapSpacebar,
                CompactLoops = _compactLoops,
                SidAuthor = _sidAuthor,
                SidLoadAddress = _sidLoadAddress,
                SidOpenDirectory = _sidOpenDirectory,
                AudioExportDirectory = _audioExportDirectory,
                InstrumentDirectory = _instrumentDirectory,
                MidiImportDirectory = _midiImportDirectory,
                MidiDevice = _midiDevice,
                MidiKnobBindings = _midiKnobBindings,
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
        public bool SwapSpacebar { get; set; }
        public bool CompactLoops { get; set; }
        public string? SidAuthor { get; set; }
        public int? SidLoadAddress { get; set; }
        public string? SidOpenDirectory { get; set; }
        public string? AudioExportDirectory { get; set; }
        public string? InstrumentDirectory { get; set; }
        public string? MidiImportDirectory { get; set; }
        public string? MidiDevice { get; set; }
        public string? MidiKnobBindings { get; set; }
    }
}
