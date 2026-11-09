using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MagPlay.Amiga;
using MagPlay.Audio;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>A row of a section: what it is, and what the file says.</summary>
internal readonly record struct InfoRow(string Label, string Value);

/// <summary>A heading and its rows.</summary>
internal sealed record InfoSection(string Heading, List<InfoRow> Rows);

/// <summary>
/// What is known about a tune in a playlist - a .sid, a module or a recording: its header or tags,
/// how it plays, each song's length and where it came from, how often it has been played, the file
/// itself, and - for a tune in HVSC - what HVSC's STIL and bug list say about it.
/// </summary>
internal sealed class InfoSheet
{
    public string Title { get; set; } = "";

    public string Subtitle { get; set; } = "";

    public List<InfoSection> Sections { get; } = [];

    /// <summary>Longer texts: STIL's comments, a module's sample names.</summary>
    public List<(string Heading, string Text)> Notes { get; } = [];

    /// <summary>How many songs, the first, and the keys its lengths and history are kept by - read
    /// on the UI thread, where they are written.</summary>
    public int Songs { get; set; } = 1;

    public int StartSong { get; set; } = 1;

    public string LengthKey { get; set; } = "";

    public string HistoryKey { get; set; } = "";

    /// <summary>Its length as the file gives it - a recording's, a module's played through - for when
    /// none is kept; null when it must be played to be known.</summary>
    public TimeSpan? FileLength { get; set; }

    /// <summary>A recording, whose length is its own.</summary>
    public bool IsRecording { get; set; }

    /// <summary>The file's own words - read away from the UI thread: it may be on a slow disk.</summary>
    public static InfoSheet Read(string path)
    {
        var sheet = new InfoSheet { Title = Path.GetFileNameWithoutExtension(path) };
        if (AudioFile.IsAudioName(path))
            sheet.ReadRecording(path);
        else
        {
            byte[] bytes = File.ReadAllBytes(path);
            sheet.LengthKey = SongLengths.KeyOf(bytes);
            sheet.HistoryKey = TuneHistory.KeyOf(bytes);
            if (bytes.Length >= 4 && bytes[1] == 'S' && bytes[2] == 'I' && bytes[3] == 'D')
                sheet.ReadSid(bytes);
            else if (TrackerModules.Looks(bytes))
                sheet.ReadModule(TrackerModules.Parse(bytes));
            sheet.Sections.Add(new InfoSection("File", FileRows(path, Convert.ToHexString(System.Security.Cryptography.MD5.HashData(bytes)))));
        }
        sheet.ReadHvscNotes(path);
        return sheet;
    }

    private void ReadRecording(string path)
    {
        byte[] key = Playlist.RecordingKey(path);
        LengthKey = SongLengths.KeyOf(key);
        HistoryKey = TuneHistory.KeyOf(key);
        var tags = AudioFile.ReadTags(path);
        IsRecording = true;
        Title = string.IsNullOrWhiteSpace(tags.Title) ? Title : tags.Title.Trim();
        Subtitle = tags.Artist?.Trim() ?? "";

        var rows = new List<InfoRow>();
        Add(rows, "Title", tags.Title);
        Add(rows, "Artist", tags.Artist);
        Add(rows, "Album", tags.Album);
        Add(rows, "Year", tags.Year);
        using (var decoder = AudioFile.Open(path))
        {
            Add(rows, "Format", decoder.Description);
            string channels = decoder.Channels switch { 1 => "mono", 2 => "stereo", var n => $"{n} channels" };
            rows.Add(new InfoRow("Sound", $"{(decoder.SampleRate / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} kHz, {channels}"));
            FileLength = decoder.Duration;
        }
        Sections.Add(new InfoSection("Recording", rows));
        Sections.Add(new InfoSection("File", FileRows(path, null)));
    }

    private void ReadSid(byte[] bytes)
    {
        var file = SidFile.Parse(bytes);
        Title = string.IsNullOrWhiteSpace(file.Name) ? Title : file.Name.Trim();
        Subtitle = file.Author?.Trim() ?? "";
        Songs = Math.Max(1, file.Songs);
        StartSong = Math.Clamp(file.StartSong, 1, Songs);

        var tune = new List<InfoRow>();
        Add(tune, "Title", file.Name);
        Add(tune, "Author", file.Author);
        Add(tune, "Released", file.Released);
        tune.Add(new InfoRow("Songs", Songs == 1 ? "1" : $"{Songs}, starting with song {StartSong}"));
        Sections.Add(new InfoSection("Tune", tune));

        // The header's flags, read as they are written: either, neither or both are answers too.
        int flags = file.Version >= 2 && bytes.Length >= 0x78 ? (bytes[0x76] << 8) | bytes[0x77] : 0;
        var c64 = new List<InfoRow>
        {
            new("Format", $"{(file.IsRsid ? "RSID" : "PSID")} version {file.Version}" +
                          (file.IsBasic ? " - a BASIC program" : file.IsRsid ? " - runs on a real C64's interrupts" : "")),
            new("Machine", ((flags >> 2) & 3) switch { 1 => "PAL", 2 => "NTSC", 3 => "PAL or NTSC", _ => "Not said - played as PAL" }),
            new("SID", Model((flags >> 4) & 3)),
        };
        for (int i = 0; i < file.ExtraSids.Count; i++)
        {
            int shift = i == 0 ? 6 : 8;
            c64.Add(new InfoRow($"SID {i + 2}", $"${file.ExtraSids[i]:X4}, {Model((flags >> shift) & 3, sameAsFirst: true)}"));
        }
        int end = file.LoadAddress + file.Data.Length - 1;
        c64.Add(new InfoRow("Memory", $"${file.LoadAddress:X4}-${end:X4}, {file.Data.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes"));
        if (!file.IsBasic)
            c64.Add(new InfoRow("Init", $"${(file.InitAddress == 0 ? file.LoadAddress : file.InitAddress):X4}"));
        if (!file.IsRsid)
        {
            c64.Add(new InfoRow("Play", file.PlayAddress == 0 ? "None - it plays from interrupts of its own" : $"${file.PlayAddress:X4}"));
            if (file.PlayAddress != 0)
            {
                var timed = Enumerable.Range(1, Songs).Where(file.UsesCiaTimer).ToList();
                c64.Add(new InfoRow("Called by", timed.Count == 0 ? "The raster, once a frame"
                    : timed.Count == Songs ? "CIA 1's timer"
                    : $"CIA 1's timer for song {string.Join(", ", timed)}, the raster for the rest"));
            }
        }
        Sections.Add(new InfoSection("On the C64", c64));
    }

    private static string Model(int bits, bool sameAsFirst = false) => bits switch
    {
        1 => "MOS 6581",
        2 => "MOS 8580",
        3 => "6581 or 8580",
        _ => sameAsFirst ? "the same as the first" : "Not said",
    };

    private void ReadModule(ITrackerModule module)
    {
        if (!string.IsNullOrWhiteSpace(module.Title))
            Title = module.Title.Trim();
        var sounds = module.SoundSamples;
        var used = sounds.Count(s => s.Bytes > 0);
        var rows = new List<InfoRow>();
        Add(rows, "Title", module.Title);
        rows.Add(new InfoRow("Format", module.Format switch
        {
            "Soundtracker" => "Soundtracker, 15 samples",
            "S3M" or "XM" or "IT" => module.TrackerName,
            _ => $"ProTracker \"{module.Format}\"",
        }));
        Add(rows, "Made with", module.MadeWith);
        rows.Add(new InfoRow("Channels", module.Channels.ToString(CultureInfo.InvariantCulture)));
        if (module.SamplesHeading == "INSTRUMENTS")
            rows.Add(new InfoRow("Instruments", module.Samples.Count.ToString(CultureInfo.InvariantCulture)));
        rows.Add(new InfoRow("Samples", $"{used} of {sounds.Count}, " +
            $"{sounds.Sum(s => (long)s.Bytes).ToString("N0", CultureInfo.InvariantCulture)} bytes"));
        rows.Add(new InfoRow("Patterns", module.PatternCount.ToString(CultureInfo.InvariantCulture)));
        int positions = module.Order.Count(p => p < module.PatternCount);
        rows.Add(new InfoRow("Positions", module.Restart > 0 && module.Restart < module.Order.Count
            ? $"{positions}, restarting at {module.Restart}" : positions.ToString(CultureInfo.InvariantCulture)));
        Sections.Add(new InfoSection(module.Kind, rows));
        FileLength = TrackerModules.Measure(module);

        if (module.Message.Length > 0)
            Notes.Add(("Song message", module.Message));
        // Composers write to the listener in the names - all of them, the empty ones too.
        void Names(string heading, IReadOnlyList<ModuleSample> list)
        {
            var names = list.Select((s, i) => (Number: i + 1, Name: s.Name.TrimEnd())).ToList();
            int last = names.FindLastIndex(n => n.Name.Trim().Length > 0);
            if (last >= 0)
                Notes.Add((heading, string.Join("\n", names.Take(last + 1).Select(n => $"{n.Number,2}  {n.Name}"))));
        }
        if (module.SamplesHeading == "INSTRUMENTS")
            Names("Instrument names", module.Samples);
        Names("Sample names", sounds);
    }

    private static List<InfoRow> FileRows(string path, string? md5)
    {
        var info = new FileInfo(path);
        var rows = new List<InfoRow>
        {
            new("Name", info.Name),
            new("Folder", info.DirectoryName ?? ""),
            new("Size", $"{info.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes"),
            new("Changed", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        };
        if (md5 is not null)
            rows.Add(new InfoRow("MD5", md5.ToLowerInvariant()));
        return rows;
    }

    private static void Add(List<InfoRow> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            rows.Add(new InfoRow(label, value.Trim()));
    }

    // ------------------------------------------------------------------ HVSC's own notes

    private static readonly Regex Field = new(@"^\s*(NAME|AUTHOR|TITLE|ARTIST|COMMENT|BUG):\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex SongMark = new(@"^\(#(\d+)\)\s*$", RegexOptions.Compiled);

    /// <summary>For a tune in HVSC - found by its DOCUMENTS folder above it - what STIL says about
    /// it and its composer's folder, and what the bug list says.</summary>
    private void ReadHvscNotes(string path)
    {
        string? root = Path.GetDirectoryName(Path.GetFullPath(path));
        while (root is not null && !File.Exists(Path.Combine(root, "DOCUMENTS", "STIL.txt")))
            root = Path.GetDirectoryName(root);
        if (root is null)
            return;

        string tune = "/" + Path.GetRelativePath(root, path).Replace('\\', '/');
        var folders = new List<string>();
        for (int at = tune.LastIndexOf('/'); at > 0; at = tune.LastIndexOf('/', at - 1))
            folders.Add(tune[..(at + 1)]);

        var stil = Entries(Path.Combine(root, "DOCUMENTS", "STIL.txt"), [tune, .. folders]);
        if (stil.TryGetValue(tune, out var about))
            Notes.Add(("HVSC's STIL", about));
        foreach (var folder in folders)
            if (stil.TryGetValue(folder, out var text))
                Notes.Add(($"HVSC's STIL on {folder}", text));
        if (Entries(Path.Combine(root, "DOCUMENTS", "BUGlist.txt"), [tune]).TryGetValue(tune, out var bugs))
            Notes.Add(("HVSC's bug list", bugs));
    }

    /// <summary>The entries for <paramref name="paths"/> in a STIL-formatted file, their fields
    /// unwrapped - STIL breaks its lines at 80 columns - and each song's under its number.</summary>
    private static Dictionary<string, string> Entries(string file, IReadOnlyCollection<string> paths)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file))
            return found;
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        StringBuilder? text = null;
        string? current = null;
        foreach (string line in File.ReadLines(file, Encoding.Latin1))
        {
            if (current is null)
            {
                if (line.StartsWith('/') && wanted.Contains(line.TrimEnd()))
                {
                    current = line.TrimEnd();
                    text = new StringBuilder();
                }
                continue;
            }
            if (line.Trim().Length == 0)
            {
                found[current] = text!.ToString().Trim();
                current = null;
                if (found.Count == wanted.Count)
                    break;
                continue;
            }
            if (SongMark.Match(line) is { Success: true } song)
                text!.Append($"\n\nSong {song.Groups[1].Value}");
            else if (Field.Match(line) is { Success: true } field)
                text!.Append($"\n{Capitalised(field.Groups[1].Value)}: {field.Groups[2].Value.Trim()}");
            else
                text!.Append(' ').Append(line.Trim());
        }
        if (current is not null)
            found[current] = text!.ToString().Trim();
        return found;
    }

    private static string Capitalised(string word) => word[..1] + word[1..].ToLowerInvariant();
}

/// <summary>The window that shows a tune's <see cref="InfoSheet"/>, from a playlist.</summary>
internal static class InfoWindow
{
    public static async Task ShowAsync(Window owner, string path)
    {
        InfoSheet sheet;
        try
        {
            sheet = await Task.Run(() => InfoSheet.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SidPlaybackException or InvalidDataException)
        {
            await MessageWindow.ShowAsync(owner, "Tune info", $"The file could not be read: {ex.Message}");
            return;
        }
        AddKept(sheet, path);

        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 8), Spacing = 4 };
        body.Children.Add(new SelectableTextBlock { Text = sheet.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (sheet.Subtitle.Length > 0)
            body.Children.Add(new SelectableTextBlock { Text = sheet.Subtitle, FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });

        foreach (var section in sheet.Sections.Where(s => s.Rows.Count > 0))
        {
            body.Children.Add(Heading(section.Heading));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 0, 0, 2) };
            for (int i = 0; i < section.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = section.Rows[i].Label, Opacity = 0.6, FontSize = 12.5, Margin = new Thickness(0, 2, 10, 2) };
                var value = new SelectableTextBlock { Text = section.Rows[i].Value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            body.Children.Add(grid);
        }
        foreach (var (heading, text) in sheet.Notes)
        {
            body.Children.Add(Heading(heading));
            body.Children.Add(new SelectableTextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = heading == "Sample names" ? new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace") : FontFamily.Default,
            });
        }

        var show = new Button { Content = "Show in folder", MinWidth = 110 };
        var close = new Button { Content = "Close", MinWidth = 80, IsDefault = true, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 8, 20, 16), Children = { show, close },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var window = new Window
        {
            Title = "Tune info",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(360, (owner.Screens.ScreenFromWindow(owner)?.WorkingArea.Height ?? 900) / (owner.Screens.ScreenFromWindow(owner)?.Scaling ?? 1) - 80),
            MinWidth = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel { Children = { buttons, new ScrollViewer { Content = body } } },
        };
        show.Click += (_, _) => ShowInFolder(path);
        close.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }

    /// <summary>What the app keeps about the tune - each song's length and where it came from, and
    /// its plays - added on the UI thread, where it is written.</summary>
    private static void AddKept(InfoSheet sheet, string path)
    {
        var record = TuneHistory.Of(sheet.HistoryKey);
        var songs = new List<InfoRow>();
        for (int song = 1; song <= sheet.Songs; song++)
        {
            string length =
                SongLengths.User(sheet.LengthKey, path, song) is { } own ? $"{SongLengths.Format(own)}, set by you"
                : record?.Lengths.TryGetValue(song, out double seconds) == true ? $"{SongLengths.Format(TimeSpan.FromSeconds(seconds))}, measured"
                : SongLengths.Hvsc(sheet.LengthKey, song) is { } hvsc ? $"{SongLengths.Format(hvsc)}, from HVSC's list"
                : sheet.FileLength is { } whole ? SongLengths.Format(whole) + (sheet.IsRecording ? "" : ", played through")
                : "Not known until it is played";
            string first = sheet.Songs > 1 && song == sheet.StartSong ? " (the first)" : "";
            songs.Add(new InfoRow(sheet.Songs == 1 ? "Length" : $"Song {song}{first}", length));
        }
        // Many songs none of which has been played say so once.
        if (songs.Count > 1 && songs.All(r => r.Value.StartsWith("Not known", StringComparison.Ordinal)))
            songs = [new InfoRow("Lengths", $"Not known until they are played - song {sheet.StartSong} plays first")];
        int at = Math.Min(sheet.Sections.Count, sheet.Sections.FindIndex(s => s.Heading == "File") is var file and >= 0 ? file : sheet.Sections.Count);
        sheet.Sections.Insert(at, new InfoSection(sheet.Songs == 1 ? "Length" : "Songs", songs));

        var played = new List<InfoRow>
        {
            new("Played", record?.Plays switch { null or 0 => "Not yet", 1 => "Once", var n => $"{n} times" }),
        };
        if (record?.LastPlayed is { } last)
            played.Add(new InfoRow("Last played", last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        sheet.Sections.Insert(at + 1, new InfoSection("Played", played));
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Opacity = 0.55,
        Margin = new Thickness(0, 14, 0, 2),
    };

    /// <summary>The file shown, chosen, in the system's file browser.</summary>
    private static void ShowInFolder(string path)
    {
        try
        {
            var start = new ProcessStartInfo { UseShellExecute = false };
            if (OperatingSystem.IsWindows())
            {
                start.FileName = "explorer.exe";
                start.Arguments = $"/select,\"{path}\"";
            }
            else if (OperatingSystem.IsMacOS())
            {
                start.FileName = "open";
                start.ArgumentList.Add("-R");
                start.ArgumentList.Add(path);
            }
            else
            {
                start.FileName = "xdg-open";
                start.ArgumentList.Add(Path.GetDirectoryName(path) ?? ".");
            }
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
