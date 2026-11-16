using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MagPlaySid;

/// <summary>
/// The music archives, from the menu: for each collection MagPlaySID browses, where it comes from. For
/// HVSC, whether a copy of it is on this computer, which is the newest online, and the means to download
/// it, to use a copy the user has, or to stop using one - a copy on this computer is used first, online
/// only without one. For Modland, its list and the modules kept from it, played again with no line.
/// </summary>
internal sealed class ArchivesWindow : Window
{
    private readonly Settings _settings;
    private readonly Action<MusicSource?> _changed;
    private readonly TextBlock _local = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _online = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Button _download = new() { MinWidth = 80 };
    private readonly Button _useFolder = new() { Content = "Use a folder...", MinWidth = 80 };
    private readonly Button _from7z = new() { Content = "Install from a .7z...", MinWidth = 80 };
    private readonly Button _forget = new() { Content = "Stop using it...", MinWidth = 80 };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 6 };
    private readonly TextBlock _step = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _stop = new() { Content = "Cancel", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _working = new() { Spacing = 8, IsVisible = false };
    private readonly CheckBox _show = new() { Content = "Show HVSC above the playlists" };
    private readonly CheckBox _tell = new() { Content = "Tell me when a newer HVSC is out than my copy" };
    private readonly TextBlock _modlandList = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _modlandKept = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Button _modlandFetch = new() { Content = "Fetch the list again", MinWidth = 80 };
    private readonly Button _modlandEmpty = new() { Content = "Remove the kept modules...", MinWidth = 80 };
    private readonly CheckBox _modlandShow = new() { Content = "Show Modland above the playlists" };
    private int? _modlandCount;
    private (int Count, long Bytes)? _modlandKeptSize;
    private bool _modlandBusy;
    private HvscRelease? _latest;
    private long? _latestSize;

    /// <summary>How large each update is to download, by where it is, as it is learned.</summary>
    private readonly Dictionary<string, long?> _updateSizes = [];
    private string? _latestFailure;
    private bool _asking = true;
    private CancellationTokenSource? _work;

    public ArchivesWindow(Settings settings, Action<MusicSource?> changed)
    {
        _settings = settings;
        _changed = changed;
        Title = "Music archives";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _show.IsChecked = !settings.HideHvsc;
        _tell.IsChecked = settings.TellNewHvsc;
        _show.IsCheckedChanged += (_, _) =>
        {
            _settings.HideHvsc = _show.IsChecked != true;
            _settings.Save();
            _changed(null);
        };
        _modlandShow.IsChecked = !settings.HideModland;
        _modlandShow.IsCheckedChanged += (_, _) =>
        {
            _settings.HideModland = _modlandShow.IsChecked != true;
            _settings.Save();
            _changed(null);
        };
        _modlandFetch.Click += (_, _) => _ = FetchModlandAsync();
        _modlandEmpty.Click += (_, _) => _ = EmptyModlandAsync();
        _tell.IsCheckedChanged += (_, _) =>
        {
            _settings.TellNewHvsc = _tell.IsChecked == true;
            _settings.Save();
        };
        _download.Click += (_, _) => _ = DownloadAsync();
        _useFolder.Click += (_, _) => _ = UseFolderAsync();
        _from7z.Click += (_, _) => _ = From7zAsync();
        _forget.Click += (_, _) => _ = ForgetAsync();
        _stop.Click += (_, _) => _work?.Cancel();
        _working.Children.Add(_step);
        _working.Children.Add(_bar);
        _working.Children.Add(_stop);

        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var card = Card(
            new TextBlock { Text = "HVSC - the High Voltage SID Collection", FontWeight = FontWeight.SemiBold, FontSize = 15 },
            _local,
            _online,
            new WrapPanel { Children = { Spaced(_download), Spaced(_useFolder), Spaced(_from7z), Spaced(_forget) } },
            _working,
            _show,
            _tell);
        var modland = Card(
            new TextBlock { Text = "Modland - the Amiga's and the PC's modules", FontWeight = FontWeight.SemiBold, FontSize = 15 },
            _modlandList,
            _modlandKept,
            new WrapPanel { Children = { Spaced(_modlandFetch), Spaced(_modlandEmpty) } },
            _modlandShow);
        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "The collections MagPlaySID browses above the playlists. A copy of HVSC on this computer is used first, and "
                         + "nothing is asked of HVSC online while it is there. Without one - and for Modland - each tune is fetched "
                         + "online as it is played, and kept: it plays again with no line.",
                    TextWrapping = TextWrapping.Wrap,
                },
                card,
                modland,
                close,
            },
        };
        Opened += async (_, _) =>
        {
            ShowState();
            ShowModland();
            _ = MeasureModlandAsync();
            await AskOnlineAsync();
        };
        Closing += (_, _) => _work?.Cancel();
    }

    public static Task ShowAsync(Window owner, Settings settings, Action<MusicSource?> changed) => new ArchivesWindow(settings, changed).ShowDialog(owner);

    private static Border Card(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.AddRange(children);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = panel,
        };
    }

    private static Button Spaced(Button button)
    {
        button.Margin = new Thickness(0, 0, 8, 6);
        return button;
    }

    /// <summary>HVSC's version API asked which HVSC is the newest, and how large it is to download.</summary>
    private async Task AskOnlineAsync()
    {
        _asking = true;
        ShowState();
        try
        {
            _latest = await HvscArchive.LatestAsync(CancellationToken.None);
            _latestFailure = null;
            _latestSize = await MusicWeb.SizeAsync(_latest.CompleteUrl, CancellationToken.None);
            await MeasureUpdatesAsync();
        }
        catch (Exception ex) when (MusicWeb.Failed(ex))
        {
            _latestFailure = ex.Message;
        }
        _asking = false;
        ShowState();
    }

    /// <summary>The updates that take the user's copy to the newest HVSC, when it can be updated so - and
    /// each is there to download: one the site does not have, the whole of HVSC is offered instead.</summary>
    private IReadOnlyList<(int Version, string Url)>? Updates()
    {
        var updates = _latest is { } latest && HvscArchive.Folder is { } folder && HvscArchive.VersionOf(folder) is { } mine
            ? HvscArchive.UpdatesFor(latest, mine) : null;
        return updates is not null && updates.Any(u => _updateSizes.TryGetValue(u.Url, out var size) && size is null) ? null : updates;
    }

    /// <summary>How large the updates are, asked of where they are.</summary>
    private async Task MeasureUpdatesAsync()
    {
        foreach (var (_, url) in Updates() ?? [])
            if (!_updateSizes.ContainsKey(url))
                _updateSizes[url] = await MusicWeb.SizeAsync(url, CancellationToken.None) is > 0 and var size ? size : null;
    }

    private void ShowState()
    {
        string? folder = HvscArchive.Folder;
        int? mine = folder is null ? null : HvscArchive.VersionOf(folder);
        bool busy = _work is not null;
        if (folder is null)
            _local.Text = "On this computer: none. Tunes are fetched from HVSC online as they are played, and kept.";
        else
        {
            int count = HvscArchive.CountTunes(folder);
            _local.Text = $"On this computer: {(mine is { } v ? $"HVSC #{v}" : "HVSC")}, {count.ToString("N0", CultureInfo.CurrentCulture)} tunes, played from{Environment.NewLine}{folder}";
        }

        bool older = _latest is { } latest && (mine is null || latest.Version > mine);
        _online.Text = _asking ? "Online: asking HVSC which version is the newest..."
            : _latestFailure is { } failure ? $"Online: {failure}."
            : _latest is { } newest ? $"Online: HVSC #{newest.Version} is the newest.{(folder is not null && older ? " Your copy is older." : folder is not null ? " Your copy is up to date." : "")}"
            : "";

        string size = _latestSize is { } bytes ? $" ({bytes / (1024.0 * 1024.0):0} MB)" : "";
        var updates = Updates();
        if (updates is not null)
        {
            // The updates' own size: a few megabytes, where the whole is eighty.
            var sizes = updates.Select(u => _updateSizes.GetValueOrDefault(u.Url)).ToList();
            size = sizes.All(s => s is not null) ? $" ({sizes.Sum(s => s!.Value) / (1024.0 * 1024.0):0.0} MB)" : "";
        }
        _download.Content = _latest is null ? "Download HVSC"
            : folder is null ? $"Download HVSC #{_latest.Version}{size}"
            : $"Update to HVSC #{_latest.Version}{size}";
        _download.IsVisible = folder is null || older;
        _download.IsEnabled = _latest is not null && !busy;
        _useFolder.IsEnabled = _from7z.IsEnabled = !busy;
        _forget.IsVisible = folder is not null;
        _forget.IsEnabled = !busy;
        _working.IsVisible = busy;
    }

    private async Task DownloadAsync()
    {
        if (_latest is not { } latest)
            return;
        if (HvscArchive.Folder is { } folder && Updates() is { } updates)
        {
            await UpdateInPlaceAsync(folder, latest, updates);
            return;
        }
        if (await ParentAsync() is { } parent)
            await InstallAsync(latest.CompleteUrl, parent);
    }

    /// <summary>The user's copy updated where it is, by HVSC's updates; when that cannot be done, the
    /// whole of HVSC offered in its place.</summary>
    private async Task UpdateInPlaceAsync(string folder, HvscRelease latest, IReadOnlyList<(int Version, string Url)> updates)
    {
        using var work = _work = new CancellationTokenSource();
        _step.Text = "Starting...";
        _bar.Value = 0;
        ShowState();
        string? failure = null;
        try
        {
            var progress = new Progress<(string Step, double Part)>(told =>
            {
                _step.Text = $"{told.Step}... {told.Part * 100:0}%";
                _bar.Value = told.Part;
            });
            await HvscArchive.UpdateAsync(folder, updates, progress, work.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (MusicWeb.Failed(ex))
        {
            failure = ex.Message;
        }
        finally
        {
            _work = null;
            // The list read again: the copy may have moved on by an update or more, even when a later one failed.
            Use(folder);
        }
        if (failure is not null && IsVisible
            && await MessageWindow.AskAsync(this, "HVSC could not be updated",
                   $"The update could not be applied: {failure}.{Environment.NewLine}{Environment.NewLine}Your copy is as it was before it. "
                   + $"Download the whole of HVSC #{latest.Version}{(_latestSize is { } bytes ? $" ({bytes / (1024.0 * 1024.0):0} MB)" : "")} in its place instead?",
                   "Download it", "Cancel"))
            await InstallAsync(latest.CompleteUrl, Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar))!);
    }

    private async Task From7zAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install HVSC from its .7z archive",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("HVSC archives") { Patterns = ["*.7z"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } archive || await ParentAsync() is not { } parent)
            return;
        await InstallAsync(archive, parent);
    }

    /// <summary>Where HVSC is to go: in place of the user's copy, when they say so; else a folder they choose.</summary>
    private async Task<string?> ParentAsync()
    {
        if (HvscArchive.Folder is { } folder)
        {
            string parent = Path.GetDirectoryName(folder)!;
            return await MessageWindow.AskAsync(this, "Replace your copy?",
                $"The new HVSC takes the place of your copy in {folder}. Your copy stays as it is until the new one is in.", "Replace", "Cancel")
                ? parent : null;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to put HVSC - it takes about 500 MB",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return null;
        if (folders[0].TryGetLocalPath() is { } chosen)
            return chosen;
        await MessageWindow.ShowAsync(this, "Cannot use that folder", $"{folders[0].Name} is not a folder on this computer.");
        return null;
    }

    private async Task InstallAsync(string archive, string parent)
    {
        using var work = _work = new CancellationTokenSource();
        _step.Text = "Starting...";
        _bar.Value = 0;
        ShowState();
        string? failure = null;
        try
        {
            var progress = new Progress<(string Step, double Part)>(told =>
            {
                _step.Text = $"{told.Step}... {told.Part * 100:0}%";
                _bar.Value = told.Part;
            });
            string installed = await HvscArchive.InstallAsync(archive, parent, progress, work.Token);
            Use(installed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (MusicWeb.Failed(ex))
        {
            failure = ex.Message;
        }
        finally
        {
            _work = null;
            ShowState();
        }
        if (failure is not null && IsVisible)
            await MessageWindow.ShowAsync(this, "HVSC could not be installed", failure);
    }

    private async Task UseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your copy of HVSC - its C64Music folder",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
            return;
        if (folders[0].TryGetLocalPath() is { } chosen && HvscArchive.Find(chosen) is { } found)
            Use(found);
        else
            await MessageWindow.ShowAsync(this, "No HVSC there",
                $"There is no HVSC in {folders[0].Name}: choose its C64Music folder - the one with DOCUMENTS{Path.DirectorySeparatorChar}Songlengths.md5 in it - or the folder that holds it.");
    }

    private async Task ForgetAsync()
    {
        if (HvscArchive.Folder is not { } folder
            || !await MessageWindow.AskAsync(this, "Stop using your copy?",
                   $"Stop using the copy of HVSC in {folder}? Its files stay where they are; tunes are fetched from HVSC online again.", "Stop using it", "Cancel"))
            return;
        Use(null);
    }

    /// <summary>The user's copy of HVSC from now on - none for null - kept, and the list read again from it.</summary>
    private void Use(string? folder)
    {
        _settings.HvscArchive = folder;
        _settings.Save();
        HvscArchive.Folder = folder;
        Hvsc.Reset();
        _changed(MusicSource.Hvsc);
        ShowState();
    }

    /// <summary>How many modules the list has and how many are kept, counted off the UI thread.</summary>
    private async Task MeasureModlandAsync()
    {
        (_modlandCount, _modlandKeptSize) = await Task.Run(() =>
        {
            int? count = Modland.Catalog?.Count;
            if (count is null && File.Exists(Modland.ListPath))
            {
                try
                {
                    count = File.ReadLines(Modland.ListPath).Count(line => line.Contains('\t'));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return (count, ((int, long)?)Modland.Kept());
        });
        ShowModland();
    }

    private void ShowModland()
    {
        var culture = CultureInfo.CurrentCulture;
        _modlandList.Text = Modland.ListFetched is { } fetched
            ? $"The list: {(_modlandCount is { } count ? $"{count.ToString("N0", culture)} modules" : "the modules")} in the formats played here - "
              + $"Protracker, Soundtracker, Fasttracker, Screamtracker 3, Impulse Tracker and OpenMPT - fetched {fetched.ToString("d", culture)}, and again once a week."
            : "The list: not fetched yet. It is fetched - 6 MB - when Modland is first opened above the playlists.";
        _modlandKept.Text = _modlandKeptSize is not { } kept ? "Kept on this computer: counting..."
            : kept.Count == 0 ? "Kept on this computer: none yet. Each module is kept as it is first played."
            : $"Kept on this computer: {(kept.Count == 1 ? "1 module" : $"{kept.Count.ToString("N0", culture)} modules")}, "
              + $"{kept.Bytes / (1024.0 * 1024.0):0.0} MB, in{Environment.NewLine}{Modland.Root}";
        _modlandFetch.IsEnabled = !_modlandBusy;
        _modlandEmpty.IsEnabled = !_modlandBusy && _modlandKeptSize is { Count: > 0 };
    }

    private async Task FetchModlandAsync()
    {
        _modlandBusy = true;
        _modlandFetch.Content = "Fetching the list...";
        ShowModland();
        string? failure = null;
        try
        {
            var catalog = await Modland.UpdateAsync(CancellationToken.None);
            _modlandCount = catalog.Count;
            _changed(MusicSource.Modland);
        }
        catch (Exception ex) when (MusicWeb.Failed(ex))
        {
            failure = ex.Message;
        }
        _modlandBusy = false;
        _modlandFetch.Content = "Fetch the list again";
        ShowModland();
        if (failure is not null && IsVisible)
            await MessageWindow.ShowAsync(this, "Modland's list could not be fetched", $"{failure}. The list kept is played from as before.");
    }

    private async Task EmptyModlandAsync()
    {
        if (_modlandKeptSize is not { Count: > 0 } kept
            || !await MessageWindow.AskAsync(this, "Remove the kept modules?",
                   $"Remove the {kept.Count.ToString("N0", CultureInfo.CurrentCulture)} modules kept from Modland, {kept.Bytes / (1024.0 * 1024.0):0.0} MB? "
                   + "They are fetched again as they are played. Modules in your playlists stay listed.", "Remove", "Cancel"))
            return;
        _modlandBusy = true;
        ShowModland();
        await Task.Run(() =>
        {
            try
            {
                Directory.Delete(Modland.Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One in use - playing - stays; the rest are gone.
            }
        });
        _modlandBusy = false;
        await MeasureModlandAsync();
    }
}
