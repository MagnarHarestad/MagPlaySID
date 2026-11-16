using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// HVSC online, in the strip: an entry above the playlists that shows the collection's folders and
/// tunes in place of a playlist's - a folder or a search's hits played on as a playlist is - the
/// search box searching it while it is shown. A tune is fetched when it is first played.
/// </summary>
internal sealed partial class PlaylistStrip
{
    private static readonly Geometry GlobeIcon = Geometry.Parse(
        "M7,0 A7,7 0 1 1 6.99,0 Z M7,1.3 A5.7,5.7 0 1 0 7.01,1.3 Z M6.35,0.6 H7.65 V13.4 H6.35 Z M0.6,6.35 H13.4 V7.65 H0.6 Z " +
        "M1.8,3.4 H12.2 V4.5 H1.8 Z M1.8,9.5 H12.2 V10.6 H1.8 Z");
    private static readonly Geometry FolderIcon = Geometry.Parse("M0,2 C0,1.4 0.4,1 1,1 H5 L6.5,2.5 H13 C13.6,2.5 14,2.9 14,3.5 V11 C14,11.6 13.6,12 13,12 H1 C0.4,12 0,11.6 0,11 Z");
    private static readonly Geometry UpIcon = Geometry.Parse("M6,0 L12,6 L10.6,7.4 L7,3.8 V14 H5 V3.8 L1.4,7.4 L0,6 Z");

    /// <summary>What a playlist made of HVSC's tunes is called - a folder's or a search's.</summary>
    private const string HvscName = "HVSC";

    /// <summary>The most hits a search of HVSC lists: enough to find one, not so many as to wait.</summary>
    private const int HvscMostHits = 500;

    private bool _hvscShown;
    private readonly Border _hvscRow = new();
    private readonly TextBlock _hvscRowName = new();
    private readonly TextBlock _hvscRowCount = new();
    private readonly ListBox _hvscBox = new();
    private readonly Grid _hvscNav = new();
    private readonly Button _hvscUp = new();
    private readonly TextBlock _hvscWay = new();
    private readonly Button _hvscRetry = new();
    private HvscCatalog? _hvscCatalog;
    private bool _hvscLoading;
    private string? _hvscError;
    private HvscFolder? _hvscFolder;

    /// <summary>The folder to show once the list is in, when it was asked for before.</summary>
    private string _hvscWanted = "";

    /// <summary>The tunes listed, as the playlist they are played on in.</summary>
    private Playlist? _hvscList;

    /// <summary>The playlists made of HVSC's tunes: none of them is one of the strip's own.</summary>
    private readonly HashSet<Playlist> _hvscLists = [];

    /// <summary>Each of HVSC's tunes listed so far, by its path in HVSC: the same row object in a folder
    /// and in a search, so the one playing stays lit.</summary>
    private readonly Dictionary<string, PlaylistTune> _hvscTunes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entry above the playlists: HVSC, with a globe, lit while it is shown.</summary>
    private Control BuildHvscRow()
    {
        var icon = Icon(GlobeIcon, 14, 14);
        icon.Foreground = SidPlayerPalette.Brush(Accent);
        icon.VerticalAlignment = VerticalAlignment.Center;
        _hvscRowName.Text = "HVSC";
        _hvscRowName.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Control);
        _hvscRowName.VerticalAlignment = VerticalAlignment.Center;
        _hvscRowName.Margin = new Thickness(10, 0, 8, 0);
        _hvscRowCount.Text = "online";
        _hvscRowCount.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _hvscRowCount.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { icon, _hvscRowName, _hvscRowCount } };
        Grid.SetColumn(_hvscRowName, 1);
        Grid.SetColumn(_hvscRowCount, 2);
        _hvscRow.Child = grid;
        _hvscRow.Padding = new Thickness(10, 7);
        _hvscRow.Margin = new Thickness(8, 2, 8, 0);
        _hvscRow.CornerRadius = new CornerRadius(6);
        _hvscRow.Background = Brushes.Transparent;
        _hvscRow.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(_hvscRow, "The High Voltage SID Collection: every C64 tune, fetched as it is played");
        AutomationProperties.SetName(_hvscRow, "HVSC");
        _hvscRow.PointerEntered += (_, _) => LightHvscRow(hover: true);
        _hvscRow.PointerExited += (_, _) => LightHvscRow(hover: false);
        _hvscRow.Tapped += (_, _) =>
        {
            if (_hvscShown && Searching)
                EndSearch();
            ShowHvsc(_hvscShown ? "" : null);
        };
        return _hvscRow;
    }

    private void LightHvscRow(bool hover = false) =>
        _hvscRow.Background = SidPlayerPalette.Brush(_hvscShown ? SidPlayerPalette.WithAlpha(Accent, 52)
            : hover ? SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 14) : Colors.Transparent);

    /// <summary>The bar over HVSC's list: up a folder, and the way to the one shown.</summary>
    private Control BuildHvscNav()
    {
        _hvscUp.Content = Icon(UpIcon, 10, 12);
        _hvscUp.Padding = new Thickness(7, 4);
        _hvscUp.VerticalAlignment = VerticalAlignment.Center;
        _hvscUp.Click += (_, _) => HvscUp();
        ToolTip.SetTip(_hvscUp, "Up a folder (Backspace)");
        AutomationProperties.SetName(_hvscUp, "Up a folder");
        Quiet(_hvscUp);
        _hvscWay.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _hvscWay.TextTrimming = TextTrimming.CharacterEllipsis;
        _hvscWay.VerticalAlignment = VerticalAlignment.Center;
        _hvscWay.Margin = new Thickness(6, 0, 0, 0);
        _hvscNav.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        _hvscNav.Margin = new Thickness(11, 0, 14, 6);
        _hvscNav.Children.Add(_hvscUp);
        _hvscNav.Children.Add(_hvscWay);
        Grid.SetColumn(_hvscWay, 1);
        _hvscNav.IsVisible = false;

        _hvscRetry.Content = "Try again";
        _hvscRetry.Margin = new Thickness(18, 8, 0, 0);
        _hvscRetry.HorizontalAlignment = HorizontalAlignment.Left;
        _hvscRetry.IsVisible = false;
        _hvscRetry.Click += (_, _) => _ = LoadHvscAsync();

        Restyle(_hvscBox);
        _hvscBox.IsVisible = false;
        _hvscBox.DataTemplates.Add(new FuncDataTemplate<HvscFolder>((_, _) => FolderRow(), supportsRecycling: true));
        _hvscBox.DataTemplates.Add(new FuncDataTemplate<PlaylistTune>((_, _) => TuneRow(), supportsRecycling: true));
        _hvscBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Back)
            {
                HvscUp();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _hvscBox.SelectedItem is { } item)
            {
                OpenHvscItem(item);
                e.Handled = true;
            }
        };
        return new StackPanel { Children = { _hvscNav, _hvscRetry } };
    }

    /// <summary>A folder in HVSC's list: its name and how many tunes it holds in all.</summary>
    private Control FolderRow()
    {
        var icon = Icon(FolderIcon, 14 * _scale, 12 * _scale);
        icon.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        icon.HorizontalAlignment = HorizontalAlignment.Right;
        icon.Margin = new Thickness(0, 0, 12, 0);
        var name = new TextBlock { FontSize = 13 * _scale, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        name.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        var count = new TextBlock
        {
            FontSize = 11 * _scale,
            Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(30 * _scale, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Children = { icon, name, count },
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(count, 2);
        var row = new Border { Padding = new Thickness(6, 7, 10, 7), Background = Brushes.Transparent, Child = grid };
        row.DataContextChanged += (_, _) =>
        {
            if (row.DataContext is HvscFolder folder)
            {
                name.Text = folder.Name.Replace('_', ' ');
                count.Text = folder.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                ToolTip.SetTip(row, folder.Way);
            }
        };
        row.Tapped += (_, _) =>
        {
            if (row.DataContext is HvscFolder folder)
                OpenHvscItem(folder);
        };
        return row;
    }

    /// <summary>HVSC shown in the strip, at the folder <paramref name="path"/> - the one it was at, for
    /// null - its list fetched first if need be.</summary>
    public void ShowHvsc(string? path = null)
    {
        if (path is not null)
            _hvscWanted = path;
        if (!_hvscShown)
        {
            _hvscShown = true;
            if (Searching)
                _search.Text = "";
            _listBox.SelectedItem = null;
            _search.Watermark = "Search HVSC";
            LightHvscRow();
        }
        if (_hvscCatalog is not null && path is not null)
            _hvscFolder = _hvscCatalog.Folder(path) ?? _hvscCatalog.Root;
        ShowHvscView();
        if (_hvscCatalog is null && !_hvscLoading)
            _ = LoadHvscAsync();
    }

    /// <summary>A playlist chosen: HVSC no longer shown.</summary>
    private void LeaveHvsc()
    {
        if (!_hvscShown)
            return;
        _hvscShown = false;
        _search.Watermark = SearchHint;
        LightHvscRow();
        _hvscBox.IsVisible = _hvscNav.IsVisible = _hvscRetry.IsVisible = false;
        _tuneBox.IsVisible = true;
    }

    private async Task LoadHvscAsync()
    {
        _hvscLoading = true;
        _hvscError = null;
        ShowHvscView();
        try
        {
            _hvscCatalog = await Hvsc.CatalogAsync();
            _hvscFolder = _hvscCatalog.Folder(_hvscWanted) ?? _hvscCatalog.Root;
            _hvscRowCount.Text = _hvscCatalog.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (Exception ex) when (ex is HvscException or IOException or UnauthorizedAccessException)
        {
            _hvscError = ex.Message;
        }
        _hvscLoading = false;
        if (_hvscShown)
            ShowHvscView();
    }

    /// <summary>What HVSC shows now: the folder it is at, or the search's hits - or why it cannot.</summary>
    private void ShowHvscView()
    {
        if (!_hvscShown)
            return;
        _tuneBox.IsVisible = false;
        _sort.IsVisible = false;
        _hvscBox.IsVisible = true;
        _hvscRetry.IsVisible = _hvscError is not null;
        if (_hvscCatalog is null)
        {
            _heading.Text = "HVSC";
            _count.Text = "The High Voltage SID Collection";
            _playAll.IsVisible = false;
            _hvscNav.IsVisible = false;
            _hvscBox.ItemsSource = null;
            _empty.Text = _hvscError is { } error
                ? $"HVSC could not be had: {error}."
                : "Getting the list of HVSC's tunes - 5 MB, fetched again once a week...";
            _empty.IsVisible = true;
            return;
        }

        var items = new List<object>();
        var tunes = new List<PlaylistTune>();
        if (Searching)
        {
            string text = _search.Text!.Trim();
            var folders = _hvscCatalog.SearchFolders(text, 50);
            var hits = _hvscCatalog.Search(text, HvscMostHits);
            items.AddRange(folders);
            foreach (var hit in hits)
            {
                var tune = HvscTune(hit);
                // Where it is, for a tune its composer's name does not place: a game's or a demo's.
                tune.FoundIn = hit.Composer.Length == 0 ? hit.Folder : null;
                tunes.Add(tune);
            }
            _heading.Text = "Search HVSC";
            string found = hits.Count == 0 ? "No tunes" : hits.Count == 1 ? "1 tune" : hits.Count >= HvscMostHits ? $"The first {hits.Count} tunes" : $"{hits.Count} tunes";
            _count.Text = folders.Count > 0 ? $"{found}, {(folders.Count == 1 ? "1 folder" : $"{folders.Count} folders")}" : found;
            _hvscNav.IsVisible = false;
            _empty.Text = $"Nothing in HVSC matches “{text}” - by its name, composer or folder.";
            _empty.IsVisible = hits.Count == 0 && folders.Count == 0;
        }
        else
        {
            var folder = _hvscFolder ??= _hvscCatalog.Root;
            items.AddRange(folder.Folders);
            foreach (var hit in folder.Tunes)
            {
                var tune = HvscTune(hit);
                tune.FoundIn = null;
                tunes.Add(tune);
            }
            _heading.Text = folder.Path.Length == 0 ? "HVSC" : folder.Title;
            _count.Text = folder.Count == 1 ? "1 tune" : $"{folder.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} tunes";
            _hvscWay.Text = folder.Path.Length == 0 ? "The High Voltage SID Collection" : $"HVSC › {folder.Way}";
            _hvscUp.IsEnabled = folder.Parent is not null;
            _hvscNav.IsVisible = true;
            _empty.IsVisible = false;
        }
        foreach (var tune in tunes)
            tune.IsPlaying = tune == _playing;
        items.AddRange(tunes);

        // The tunes listed as a playlist of their own, to play on in: made anew, numbered as shown.
        var list = new Playlist(HvscName, []);
        foreach (var tune in tunes)
            list.Add(tune);
        _hvscLists.Add(list);
        _hvscList = list;
        _playAll.IsVisible = true;
        _playAll.IsEnabled = tunes.Count > 0;
        ToolTip.SetTip(_playAll, Searching ? "Play the tunes found" : "Play the folder's tunes");
        _hvscBox.ItemsSource = items;
        if (_playing is not null && tunes.Contains(_playing))
        {
            _hvscBox.SelectedItem = _playing;
            _hvscBox.ScrollIntoView(_playing);
        }
        else if (items.Count > 0)
            _hvscBox.ScrollIntoView(items[0]);
        ReadHvscHeaders(tunes);
    }

    /// <summary>The row of one of HVSC's tunes - the one made before, if it has been listed - named from
    /// its file and folder, with HVSC's count of songs and the first's length, until its own header
    /// is read.</summary>
    private PlaylistTune HvscTune(HvscTune hit)
    {
        if (_hvscTunes.TryGetValue(hit.Path, out var made))
            return made;
        var tune = new PlaylistTune(Hvsc.LocalPath(hit.Path))
        {
            Title = hit.Name,
            Author = hit.Composer,
            Songs = Math.Max(1, hit.Lengths.Length),
            Length = HvscLength(hit),
        };
        _hvscTunes[hit.Path] = tune;
        return tune;
    }

    private static TimeSpan? HvscLength(HvscTune hit) => hit.Lengths is [{ TotalSeconds: > 0 } first, ..] ? first : null;

    /// <summary>The headers of the tunes already fetched read, off the UI thread: their own names and
    /// composers in place of their files'.</summary>
    private void ReadHvscHeaders(IReadOnlyList<PlaylistTune> tunes)
    {
        var kept = tunes.Where(t => t.Key is null && File.Exists(t.Path)).ToList();
        if (kept.Count > 0)
            Task.Run(() =>
            {
                foreach (var tune in kept)
                    if (tune.ReadHeader() is { } header)
                        Dispatcher.UIThread.Post(() => TakeHvscHeader(tune, header), DispatcherPriority.Background);
            });
    }

    private void TakeHvscHeader(PlaylistTune tune, TuneHeader header)
    {
        var length = tune.Length;
        tune.Take(header);
        tune.Length ??= length;
    }

    /// <summary>A tune of HVSC just fetched: its own name and composer shown.</summary>
    public void HvscFetched(PlaylistTune tune) => ReadHvscHeaders([tune]);

    private void OpenHvscItem(object item)
    {
        if (item is HvscFolder folder)
        {
            if (Searching)
                EndSearch();
            _hvscFolder = folder;
            ShowHvscView();
            _hvscBox.Focus();
        }
        else if (item is PlaylistTune tune && _hvscList is { } list)
            Play?.Invoke(list, tune);
    }

    private void HvscUp()
    {
        if (Searching)
        {
            EndSearch();
            return;
        }
        if (_hvscFolder?.Parent is not { } parent)
            return;
        var left = _hvscFolder;
        _hvscFolder = parent;
        ShowHvscView();
        _hvscBox.SelectedItem = left;
        _hvscBox.ScrollIntoView(left);
    }

    /// <summary>Whether a playlist is one made of HVSC's tunes, not one of the strip's.</summary>
    private bool IsHvscList(Playlist? playlist) => playlist is not null && _hvscLists.Contains(playlist);

    /// <summary>A right-click on one of HVSC's tunes: play it, add it to a playlist - fetched first -
    /// or read about it.</summary>
    private List<Control> HvscMenu(PlaylistTune tune)
    {
        var add = new MenuItem { Header = "Add to playlist" };
        foreach (var favourite in _favourites)
        {
            bool holds = favourite.Contains(tune.Path);
            var item = Item(favourite.Name, () => Fetched(tune, () => Add(favourite, tune.Path)));
            item.IsEnabled = !holds;
            if (holds)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = true;
            }
            add.Items.Add(item);
        }
        if (_favourites.Count > 0)
            add.Items.Add(new Separator());
        add.Items.Add(Item("New playlist...", () => Fetched(tune, () => _ = NewPlaylistAsync(tune.Path))));
        return [Item("Play", () => OpenHvscItem(tune)), add, Item("Info...", () => _ = ShowInfoAsync(tune))];
    }

    /// <summary><paramref name="then"/> once the tune is fetched - at once, when it is kept already;
    /// why not, when it cannot be.</summary>
    private async void Fetched(PlaylistTune tune, Action then)
    {
        if (await FetchHvscAsync(tune))
            then();
    }

    /// <summary>One of HVSC's tunes fetched, if it is to be; false - and the user told why - when it
    /// cannot be.</summary>
    private async Task<bool> FetchHvscAsync(PlaylistTune tune)
    {
        if (!Hvsc.NeedsFetch(tune.Path))
            return true;
        try
        {
            await Hvsc.FetchAsync(tune.Path);
            HvscFetched(tune);
            return true;
        }
        catch (Exception ex) when (ex is HvscException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(_owner, "HVSC", $"{tune.Title} could not be fetched from HVSC: {ex.Message}.");
            return false;
        }
    }

    private const string HvscShownPrefix = "HVSC:";

    private const string SearchHint = "Search tunes";
}
