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
/// The collections - HVSC, Modland - in the strip: an entry for each above the playlists that shows its
/// folders and tunes in place of a playlist's - a folder or a search's hits played on as a playlist is -
/// the search box searching it while it is shown. A tune is fetched when it is first played.
/// </summary>
internal sealed partial class PlaylistStrip
{
    private static readonly Geometry GlobeIcon = Geometry.Parse(
        "M7,0 A7,7 0 1 1 6.99,0 Z M7,1.3 A5.7,5.7 0 1 0 7.01,1.3 Z M6.35,0.6 H7.65 V13.4 H6.35 Z M0.6,6.35 H13.4 V7.65 H0.6 Z " +
        "M1.8,3.4 H12.2 V4.5 H1.8 Z M1.8,9.5 H12.2 V10.6 H1.8 Z");
    private static readonly Geometry FolderIcon = Geometry.Parse("M0,2 C0,1.4 0.4,1 1,1 H5 L6.5,2.5 H13 C13.6,2.5 14,2.9 14,3.5 V11 C14,11.6 13.6,12 13,12 H1 C0.4,12 0,11.6 0,11 Z");
    private static readonly Geometry UpIcon = Geometry.Parse("M6,0 L12,6 L10.6,7.4 L7,3.8 V14 H5 V3.8 L1.4,7.4 L0,6 Z");

    /// <summary>The most hits a search of a collection lists: enough to find one, not so many as to wait.</summary>
    private const int MostHits = 500;

    /// <summary>A collection as the strip shows it: its entry above the playlists, its list once read,
    /// the folder shown and the tunes listed so far.</summary>
    private sealed class Browsed(MusicSource source)
    {
        public MusicSource Source { get; } = source;

        public Border Row { get; } = new();

        public TextBlock RowName { get; } = new();

        public TextBlock RowCount { get; } = new();

        public MusicCatalog? Catalog { get; set; }

        public bool Loading { get; set; }

        public string? Error { get; set; }

        public MusicFolder? Folder { get; set; }

        /// <summary>The folder to show once the list is in, when it was asked for before.</summary>
        public string Wanted { get; set; } = "";

        /// <summary>The tunes listed, as the playlist they are played on in.</summary>
        public Playlist? List { get; set; }

        /// <summary>Each of its tunes listed so far, by its path in it: the same row object in a folder
        /// and in a search, so the one playing stays lit.</summary>
        public Dictionary<string, PlaylistTune> Tunes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How <see cref="ShownName"/> starts while it is shown: "HVSC:".</summary>
        public string Prefix => Source.Name + ":";

        public string Where => Source.OnThisPc ? "on this PC" : "online";
    }

    private readonly Browsed[] _browsed = [.. MusicSource.All.Select(s => new Browsed(s))];

    /// <summary>The collection shown in place of a playlist; null while a playlist is.</summary>
    private Browsed? _at;

    private bool CollectionShown => _at is not null;

    private readonly ListBox _musicBox = new();
    private readonly Grid _musicNav = new();
    private readonly Button _musicUp = new();
    private readonly TextBlock _musicWay = new();
    private readonly Button _musicRetry = new();

    /// <summary>The playlists made of the collections' tunes: none of them is one of the strip's own.</summary>
    private readonly HashSet<Playlist> _musicLists = [];

    private Browsed Of(MusicSource source) => _browsed.First(b => b.Source == source);

    /// <summary>The entries above the playlists: one for each collection, with a globe, lit while it is shown.</summary>
    private Control BuildCollectionRows()
    {
        var rows = new StackPanel();
        foreach (var browsed in _browsed)
            rows.Children.Add(BuildCollectionRow(browsed));
        return rows;
    }

    private Control BuildCollectionRow(Browsed browsed)
    {
        var icon = Icon(GlobeIcon, 14, 14);
        icon.Foreground = SidPlayerPalette.Brush(Accent);
        icon.VerticalAlignment = VerticalAlignment.Center;
        browsed.RowName.Text = browsed.Source.Name;
        browsed.RowName.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Control);
        browsed.RowName.VerticalAlignment = VerticalAlignment.Center;
        browsed.RowName.Margin = new Thickness(10, 0, 8, 0);
        browsed.RowCount.Text = browsed.Where;
        browsed.RowCount.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        browsed.RowCount.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { icon, browsed.RowName, browsed.RowCount } };
        Grid.SetColumn(browsed.RowName, 1);
        Grid.SetColumn(browsed.RowCount, 2);
        var row = browsed.Row;
        row.Child = grid;
        row.Padding = new Thickness(10, 7);
        row.Margin = new Thickness(8, 2, 8, 0);
        row.CornerRadius = new CornerRadius(6);
        row.Background = Brushes.Transparent;
        row.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(row, browsed.Source.Tip);
        AutomationProperties.SetName(row, browsed.Source.Name);
        row.PointerEntered += (_, _) => LightRow(browsed, hover: true);
        row.PointerExited += (_, _) => LightRow(browsed, hover: false);
        row.Tapped += (_, _) =>
        {
            bool here = _at == browsed;
            if (here && Searching)
                EndSearch();
            ShowCollection(browsed.Source, here ? "" : null);
        };
        return row;
    }

    private void LightRow(Browsed browsed, bool hover = false) =>
        browsed.Row.Background = SidPlayerPalette.Brush(_at == browsed ? SidPlayerPalette.WithAlpha(Accent, 52)
            : hover ? SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 14) : Colors.Transparent);

    /// <summary>The bar over a collection's list: up a folder, and the way to the one shown.</summary>
    private Control BuildMusicNav()
    {
        _musicUp.Content = Icon(UpIcon, 10, 12);
        _musicUp.Padding = new Thickness(7, 4);
        _musicUp.VerticalAlignment = VerticalAlignment.Center;
        _musicUp.Click += (_, _) => MusicUp();
        ToolTip.SetTip(_musicUp, "Up a folder (Backspace)");
        AutomationProperties.SetName(_musicUp, "Up a folder");
        Quiet(_musicUp);
        _musicWay.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _musicWay.TextTrimming = TextTrimming.CharacterEllipsis;
        _musicWay.VerticalAlignment = VerticalAlignment.Center;
        _musicWay.Margin = new Thickness(6, 0, 0, 0);
        _musicNav.ColumnDefinitions = new ColumnDefinitions("Auto,*");
        _musicNav.Margin = new Thickness(11, 0, 14, 6);
        _musicNav.Children.Add(_musicUp);
        _musicNav.Children.Add(_musicWay);
        Grid.SetColumn(_musicWay, 1);
        _musicNav.IsVisible = false;

        _musicRetry.Content = "Try again";
        _musicRetry.Margin = new Thickness(18, 8, 0, 0);
        _musicRetry.HorizontalAlignment = HorizontalAlignment.Left;
        _musicRetry.IsVisible = false;
        _musicRetry.Click += (_, _) =>
        {
            if (_at is { } browsed)
                _ = LoadCollectionAsync(browsed);
        };

        Restyle(_musicBox);
        _musicBox.IsVisible = false;
        _musicBox.DataTemplates.Add(new FuncDataTemplate<MusicFolder>((_, _) => FolderRow(), supportsRecycling: true));
        _musicBox.DataTemplates.Add(new FuncDataTemplate<PlaylistTune>((_, _) => TuneRow(), supportsRecycling: true));
        _musicBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Back)
            {
                MusicUp();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _musicBox.SelectedItem is { } item)
            {
                OpenMusicItem(item);
                e.Handled = true;
            }
        };
        return new StackPanel { Children = { _musicNav, _musicRetry } };
    }

    /// <summary>A folder in a collection's list: its name and how many tunes it holds in all.</summary>
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
            if (row.DataContext is MusicFolder folder)
            {
                // A composer of Modland's found by a search: in which format, as one has a folder in several.
                name.Text = folder.From == Collection.Modland && Searching && folder.Parent is { Path.Length: > 0 } format
                    ? $"{folder.Listed} · {format.Listed}" : folder.Listed;
                count.Text = folder.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                ToolTip.SetTip(row, folder.Way);
            }
        };
        row.Tapped += (_, _) =>
        {
            if (row.DataContext is MusicFolder folder)
                OpenMusicItem(folder);
        };
        return row;
    }

    /// <summary>HVSC shown in the strip, at the folder <paramref name="path"/> - the one it was at, for null.</summary>
    public void ShowHvsc(string? path = null) => ShowCollection(MusicSource.Hvsc, path);

    /// <summary>A collection shown in the strip, at the folder <paramref name="path"/> - the one it was
    /// at, for null - its list fetched first if need be.</summary>
    public void ShowCollection(MusicSource source, string? path = null)
    {
        var browsed = Of(source);
        if (path is not null)
            browsed.Wanted = path;
        if (_at != browsed)
        {
            var was = _at;
            _at = browsed;
            if (was is not null)
                LightRow(was);
            else
                _listBox.SelectedItem = null;
            if (Searching)
                _search.Text = "";
            _search.Watermark = $"Search {source.Name}";
            LightRow(browsed);
        }
        if (browsed.Catalog is not null && path is not null)
            browsed.Folder = browsed.Catalog.Folder(path) ?? browsed.Catalog.Root;
        ShowMusicView();
        if (browsed.Catalog is null && !browsed.Loading)
            _ = LoadCollectionAsync(browsed);
    }

    /// <summary>A collection's entry shown above the playlists, or left out - HVSC for those who play
    /// no .sid, Modland for those who play no module - and the collection left if it was shown.</summary>
    public void ShowCollectionRow(MusicSource source, bool show)
    {
        var browsed = Of(source);
        browsed.Row.IsVisible = show;
        if (!show && _at == browsed)
        {
            LeaveCollection();
            if (_lists.Count > 0)
                _listBox.SelectedIndex = 0;
        }
    }

    /// <summary>Where a collection comes from has changed - the user's copy of HVSC has come, gone or been
    /// updated, Modland's list fetched again: its list read again, and the folder shown kept.</summary>
    public void CollectionChanged(MusicSource source)
    {
        var browsed = Of(source);
        browsed.Wanted = browsed.Folder?.Path ?? browsed.Wanted;
        browsed.Catalog = null;
        browsed.Folder = null;
        browsed.Error = null;
        browsed.RowCount.Text = browsed.Where;
        if (_at == browsed && !browsed.Loading)
            _ = LoadCollectionAsync(browsed);
    }

    /// <summary>A playlist chosen: no collection shown.</summary>
    private void LeaveCollection()
    {
        if (_at is not { } was)
            return;
        _at = null;
        _search.Watermark = SearchHint;
        LightRow(was);
        _musicBox.IsVisible = _musicNav.IsVisible = _musicRetry.IsVisible = false;
        _tuneBox.IsVisible = true;
    }

    private async Task LoadCollectionAsync(Browsed browsed)
    {
        browsed.Loading = true;
        browsed.Error = null;
        ShowMusicView();
        await FirstFrameAsync();
        try
        {
            browsed.Catalog = await browsed.Source.CatalogAsync();
            browsed.Folder = browsed.Catalog.Folder(browsed.Wanted) ?? browsed.Catalog.Root;
            browsed.RowCount.Text = browsed.Catalog.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (Exception ex) when (ex is MusicSourceException or IOException or UnauthorizedAccessException)
        {
            browsed.Error = ex.Message;
        }
        browsed.Loading = false;
        if (_at == browsed)
            ShowMusicView();
    }

    /// <summary>Whether the window has drawn its first frame.</summary>
    private bool _drawn;

    /// <summary>Done once the window's first frame is drawn - or after a second, if no frame comes. A
    /// collection's list read as the app starts - 166,000 of Modland's modules - makes garbage the
    /// collector stops every thread for: read after the window is up, it no longer holds it back.</summary>
    private async Task FirstFrameAsync()
    {
        if (_drawn || TopLevel.GetTopLevel(this) is not { } top)
            return;
        var drawn = new TaskCompletionSource();
        // The second frame asked for in the first: called once the first has been drawn.
        top.RequestAnimationFrame(_ => top.RequestAnimationFrame(_ => drawn.TrySetResult()));
        await Task.WhenAny(drawn.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        _drawn = true;
    }

    /// <summary>What the collection shown shows now: the folder it is at, or the search's hits - or why it cannot.</summary>
    private void ShowMusicView()
    {
        if (_at is not { } browsed)
            return;
        var source = browsed.Source;
        _tuneBox.IsVisible = false;
        _sort.IsVisible = false;
        _musicBox.IsVisible = true;
        _musicRetry.IsVisible = browsed.Error is not null;
        if (browsed.Catalog is not { } catalog)
        {
            _heading.Text = source.Name;
            _count.Text = source.FullName;
            _playAll.IsVisible = false;
            _musicNav.IsVisible = false;
            _musicBox.ItemsSource = null;
            _empty.Text = browsed.Error is { } error ? $"{source.Name} could not be had: {error}." : source.Loading;
            _empty.IsVisible = true;
            return;
        }

        var items = new List<object>();
        var tunes = new List<PlaylistTune>();
        if (Searching)
        {
            string text = _search.Text!.Trim();
            var folders = catalog.SearchFolders(text, 50);
            var hits = catalog.Search(text, MostHits);
            items.AddRange(folders);
            foreach (var hit in hits)
            {
                var tune = MusicTuneRow(browsed, hit);
                // Where it is, for a tune its composer's name does not place: HVSC's game's or demo's;
                // for Modland's, its format.
                tune.FoundIn = hit.From == Collection.Modland ? hit.Path[..hit.Path.IndexOf('/')]
                    : hit.Composer.Length == 0 ? hit.Folder : null;
                tunes.Add(tune);
            }
            _heading.Text = $"Search {source.Name}";
            string found = hits.Count == 0 ? "No tunes" : hits.Count == 1 ? "1 tune" : hits.Count >= MostHits ? $"The first {hits.Count} tunes" : $"{hits.Count} tunes";
            _count.Text = folders.Count > 0 ? $"{found}, {(folders.Count == 1 ? "1 folder" : $"{folders.Count} folders")}" : found;
            _musicNav.IsVisible = false;
            _empty.Text = $"Nothing in {source.Name} matches “{text}” - by its name, composer or folder.";
            _empty.IsVisible = hits.Count == 0 && folders.Count == 0;
        }
        else
        {
            var folder = browsed.Folder ??= catalog.Root;
            items.AddRange(folder.Folders);
            foreach (var hit in folder.Tunes)
            {
                var tune = MusicTuneRow(browsed, hit);
                tune.FoundIn = null;
                tunes.Add(tune);
            }
            _heading.Text = folder.Path.Length == 0 ? source.Name : folder.Title;
            _count.Text = folder.Count == 1 ? "1 tune" : $"{folder.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} tunes";
            _musicWay.Text = folder.Path.Length == 0 ? source.FullName : $"{source.Name} › {folder.Way}";
            _musicUp.IsEnabled = folder.Parent is not null;
            _musicNav.IsVisible = true;
            _empty.IsVisible = false;
        }
        foreach (var tune in tunes)
            tune.IsPlaying = tune == _playing;
        items.AddRange(tunes);

        // The tunes listed as a playlist of their own, to play on in: made anew, numbered as shown.
        var list = new Playlist(source.Name, []);
        foreach (var tune in tunes)
            list.Add(tune);
        _musicLists.Add(list);
        browsed.List = list;
        _playAll.IsVisible = true;
        _playAll.IsEnabled = tunes.Count > 0;
        ToolTip.SetTip(_playAll, Searching ? "Play the tunes found" : "Play the folder's tunes");
        _musicBox.ItemsSource = items;
        if (_playing is not null && tunes.Contains(_playing))
        {
            _musicBox.SelectedItem = _playing;
            _musicBox.ScrollIntoView(_playing);
        }
        else if (items.Count > 0)
            _musicBox.ScrollIntoView(items[0]);
        ReadMusicHeaders(tunes);
    }

    /// <summary>The row of one of a collection's tunes - the one made before, if it has been listed -
    /// named from its file and folder, with HVSC's count of songs and the first's length, until its own
    /// header is read.</summary>
    private static PlaylistTune MusicTuneRow(Browsed browsed, MusicTune hit)
    {
        if (browsed.Tunes.TryGetValue(hit.Path, out var made))
            return made;
        var tune = new PlaylistTune(browsed.Source.LocalPath(hit.Path));
        DescribeFromList(tune, hit);
        browsed.Tunes[hit.Path] = tune;
        return tune;
    }

    /// <summary>A tune of a collection not fetched yet, named as the collection's list names it.</summary>
    private static void DescribeFromList(PlaylistTune tune, MusicTune hit)
    {
        tune.Title = hit.Name;
        tune.Author = hit.Composer;
        tune.Songs = Math.Max(1, hit.Lengths.Length);
        tune.Length ??= ListedLength(hit);
    }

    private static TimeSpan? ListedLength(MusicTune hit) => hit.Lengths is [{ TotalSeconds: > 0 } first, ..] ? first : null;

    /// <summary>The headers of the tunes already fetched read, off the UI thread: their own names and
    /// composers in place of their files' - but for a collection that keeps its own.</summary>
    private void ReadMusicHeaders(IReadOnlyList<PlaylistTune> tunes)
    {
        var kept = tunes.Where(t => t.Key is null && File.Exists(t.Path)).ToList();
        if (kept.Count > 0)
            Task.Run(() =>
            {
                foreach (var tune in kept)
                    if (tune.ReadHeader() is { } header)
                        Dispatcher.UIThread.Post(() => TakeMusicHeader(tune, header), DispatcherPriority.Background);
            });
    }

    private static void TakeMusicHeader(PlaylistTune tune, TuneHeader header)
    {
        var (length, title, author) = (tune.Length, tune.Title, tune.Author);
        tune.Take(header);
        tune.Length ??= length;
        if (MusicSource.Of(tune.Path) is { KeepsNames: true })
            (tune.Title, tune.Author) = (title, author);
    }

    /// <summary>A tune of a collection just fetched: its own name and composer shown.</summary>
    public void TuneFetched(PlaylistTune tune) => ReadMusicHeaders([tune]);

    private void OpenMusicItem(object item)
    {
        if (item is MusicFolder folder)
        {
            if (Searching)
                EndSearch();
            if (_at is { } browsed)
                browsed.Folder = folder;
            ShowMusicView();
            _musicBox.Focus();
        }
        else if (item is PlaylistTune tune && _at?.List is { } list)
            Play?.Invoke(list, tune);
    }

    private void MusicUp()
    {
        if (Searching)
        {
            EndSearch();
            return;
        }
        if (_at is not { Folder.Parent: { } parent } browsed)
            return;
        var left = browsed.Folder;
        browsed.Folder = parent;
        ShowMusicView();
        _musicBox.SelectedItem = left;
        _musicBox.ScrollIntoView(left!);
    }

    /// <summary>Whether a playlist is one made of a collection's tunes, not one of the strip's.</summary>
    private bool IsMusicList(Playlist? playlist) => playlist is not null && _musicLists.Contains(playlist);

    /// <summary>A right-click on one of a collection's tunes: play it, add it to a playlist - fetched
    /// first - or read about it.</summary>
    private List<Control> MusicMenu(PlaylistTune tune)
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
        return [Item("Play", () => OpenMusicItem(tune)), add, Item("Info...", () => _ = ShowInfoAsync(tune))];
    }

    /// <summary><paramref name="then"/> once the tune is fetched - at once, when it is kept already;
    /// why not, when it cannot be.</summary>
    private async void Fetched(PlaylistTune tune, Action then)
    {
        if (await FetchMusicAsync(tune))
            then();
    }

    /// <summary>One of a collection's tunes fetched, if it is to be; false - and the user told why - when
    /// it cannot be.</summary>
    private async Task<bool> FetchMusicAsync(PlaylistTune tune)
    {
        if (MusicSource.Of(tune.Path) is not { } source || !source.NeedsFetch(tune.Path))
            return true;
        try
        {
            await source.FetchAsync(tune.Path);
            TuneFetched(tune);
            return true;
        }
        catch (Exception ex) when (ex is MusicSourceException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(_owner, source.Name, $"{tune.Title} could not be fetched from {source.Name}: {ex.Message}.");
            return false;
        }
    }

    private const string SearchHint = "Search tunes";
}
