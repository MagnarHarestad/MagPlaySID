using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MagPlaySid.Player;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>
/// The strip down the left of the window: the playlists - a folder dropped on the window, and the
/// favourites the user made from tunes they played - and below them the tunes of the one chosen.
/// A click on a tune plays it, and the playlist from there; a tune held and dragged moves up or
/// down the playlist, in the user's own order. Sort above the tunes puts them in another, to go
/// back from. Shown or hidden from the window's menu.
/// </summary>
internal sealed class PlaylistStrip : Border
{
    public const double StripWidth = 290;

    private static readonly Geometry HeartIcon = Geometry.Parse(
        "M8,14 C8,14 1,9.5 1,5.2 C1,3 2.7,1.5 4.6,1.5 C6,1.5 7.3,2.3 8,3.5 C8.7,2.3 10,1.5 11.4,1.5 C13.3,1.5 15,3 15,5.2 C15,9.5 8,14 8,14 Z");
    private static readonly Geometry PlusIcon = Geometry.Parse("M7,1 H9 V7 H15 V9 H9 V15 H7 V9 H1 V7 H7 Z");
    private static readonly Geometry PlayIcon = Geometry.Parse("M0,0 L10,6 L0,12 Z");
    private static readonly Geometry BarsIcon = Geometry.Parse("M0,6 H3 V14 H0 Z M5,0 H8 V14 H5 Z M10,4 H13 V14 H10 Z");
    private static readonly Geometry SortIcon = Geometry.Parse("M0,0 H14 V2 H0 Z M0,5 H10 V7 H0 Z M0,10 H6 V12 H0 Z");

    /// <summary>The sorts as the menu offers them, the groups apart; the note says what a sort can
    /// only know of tunes that have been played.</summary>
    private static readonly (TuneSort Sort, string Name, string? Note)[][] Sorts =
    [
        [(TuneSort.Own, "Your order", null)],
        [(TuneSort.YearNewest, "Release year: newest first", null), (TuneSort.YearOldest, "Release year: oldest first", null),
         (TuneSort.TitleAToZ, "Title: A to Z", null), (TuneSort.TitleZToA, "Title: Z to A", null),
         (TuneSort.ComposerAToZ, "Composer: A to Z", null), (TuneSort.GroupAToZ, "Group: A to Z", null)],
        [(TuneSort.FileNewest, "File date: newest first", null), (TuneSort.FileOldest, "File date: oldest first", null),
         (TuneSort.SongsMost, "Songs: most first", null), (TuneSort.SongsFewest, "Songs: fewest first", null)],
        [(TuneSort.LengthLongest, "Length: longest first", PlayedNote), (TuneSort.LengthShortest, "Length: shortest first", PlayedNote),
         (TuneSort.RecentlyPlayed, "Recently played", PlayedNote), (TuneSort.MostPlayed, "Most played", PlayedNote)],
        [(TuneSort.Shuffle, "Shuffle", null)],
    ];

    private const string PlayedNote = "Known once a tune has been played. Tunes not played yet come last.";

    /// <summary>The colour of what plays: the second voice's, as the player's buttons light.</summary>
    private static readonly Color Accent = SidPlayerPalette.VoiceColour(1);
    private static readonly Color Heart = SidPlayerPalette.VoiceColour(0);

    /// <summary>The colours a favourite's heart can take; the first is the usual one, kept as none.</summary>
    private static readonly (string Name, Color Colour)[] HeartColours =
    [
        ("Orange", Heart),
        ("Red", Color.FromRgb(0xE5, 0x48, 0x4D)),
        ("Pink", Color.FromRgb(0xF0, 0x6F, 0xB4)),
        ("Violet", Color.FromRgb(0xA9, 0x7B, 0xF0)),
        ("Blue", Color.FromRgb(0x5B, 0x9B, 0xF5)),
        ("Cyan", Color.FromRgb(0x45, 0xC8, 0xD8)),
        ("Green", Color.FromRgb(0x62, 0xC9, 0x6F)),
        ("Yellow", Color.FromRgb(0xF2, 0xD3, 0x4F)),
        ("White", Color.FromRgb(0xE8, 0xE8, 0xF0)),
    ];

    private readonly Window _owner;
    private readonly List<Playlist> _favourites;
    private readonly ObservableCollection<Playlist> _lists = [];
    private readonly ListBox _listBox = new();
    private readonly ListBox _tuneBox = new();
    private readonly TextBlock _caption = new();
    private readonly TextBlock _heading = new();
    private readonly TextBlock _count = new();
    private readonly TextBlock _empty = new();
    private readonly Button _new = new();
    private readonly Button _playAll = new();
    private readonly Button _sort = new();
    private readonly TextBlock _sortName = new();
    private readonly Dictionary<TuneSort, MenuItem> _sortItems = [];
    private PlaylistTune? _playing;
    private double _scale = 1;

    /// <summary>How far a tune is dragged before it moves - less is a click.</summary>
    private const double DragThreshold = 6;

    private PlaylistTune? _dragTune;
    private Point _dragStart;
    private bool _dragging;
    /// <summary>The click that ends a drag is not a click on the tune: it does not play it.</summary>
    private bool _dragged;

    /// <summary>A playlist held to be moved up or down the list, as a tune is.</summary>
    private Playlist? _dragList;
    private Point _dragListStart;
    private bool _draggingList;

    /// <summary>The strip's parts: the playlists above the line the user drags to make them taller
    /// or shorter, the line, and the tunes below it.</summary>
    private readonly Grid _layout = new();
    private readonly GridSplitter _splitter = new();
    private readonly Border _line = new();

    /// <summary>The height the user dragged the playlists to, in pixels; 0 when they have not, and
    /// the playlists take what they need, up to <see cref="PlaylistsShown"/>.</summary>
    private double _playlistsHeight;

    private const double PlaylistsShown = 210, FewestPlaylistPixels = 44, FewestTunePixels = 140;

    /// <summary>The playlists dragged taller or shorter: the height to keep.</summary>
    public event Action<double>? PlaylistsResized;

    /// <summary>A tune chosen to play, and the playlist to go on with after it.</summary>
    public event Action<Playlist, PlaylistTune>? Play;

    /// <summary>A playlist was made or a folder opened: the strip should be shown, if it is not.</summary>
    public event Action? ShowRequested;

    /// <summary>Whether there is any playlist.</summary>
    public bool HasPlaylists => _lists.Count > 0;

    public PlaylistStrip(Window owner)
    {
        _owner = owner;
        _favourites = Favourites.Load();
        foreach (var favourite in _favourites)
            _lists.Add(favourite);

        Width = StripWidth;
        Background = SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground);
        BorderBrush = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder);
        BorderThickness = new Thickness(0, 0, 1, 0);
        Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                // The rows pad themselves, so all of a row is theirs to click.
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MarginProperty, new Thickness(8, 1)),
                new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(ListBoxItem.MinHeightProperty, 0.0),
            },
        });

        Child = BuildLayout();
        _listBox.SelectionChanged += (_, _) => ShowTunes();
        if (_lists.Count > 0)
            _listBox.SelectedIndex = 0;
        ReadHeaders(_favourites.SelectMany(p => p.Tunes), HeadersRead);
        ApplyScale();

        // Scrolled by hand before the view kept is back: the hand wins.
        foreach (var list in new[] { _listBox, _tuneBox })
        {
            list.AddHandler(PointerWheelChangedEvent, (_, _) => _scrolledByHand = true, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerPressedEvent, (_, _) => _scrolledByHand = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    // ------------------------------------------------------------------ the view kept

    /// <summary>The headers of the playlists' tunes have all been read: each tune's row is as tall
    /// as it will be - a composer's line under its name - so a list can be scrolled to where it was.</summary>
    private bool _headersRead;

    private (ListPlace Playlists, ListPlace Tunes)? _restore;
    private bool _scrolledByHand;

    /// <summary>Where a list is scrolled to: the row at its top, and the pixels of it scrolled past;
    /// a row below zero for none.</summary>
    public readonly record struct ListPlace(int Row, double Into);

    private static ScrollViewer? Scroller(ListBox list) => list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>Where the playlists and the tunes shown are scrolled to.</summary>
    public (ListPlace Playlists, ListPlace Tunes) Places => (TopOf(_listBox), TopOf(_tuneBox));

    /// <summary>The row at the top of <paramref name="list"/>'s view - the first one any of which shows -
    /// and how far into it the view starts.</summary>
    private static ListPlace TopOf(ListBox list)
    {
        if (Scroller(list) is not { } scroller)
            return new ListPlace(-1, 0);
        var top = new ListPlace(-1, 0);
        double highest = double.MaxValue;
        foreach (var container in list.GetRealizedContainers())
        {
            if (!container.IsVisible || container.TranslatePoint(new Point(0, 0), scroller) is not { } at)
                continue;
            if (at.Y + container.Bounds.Height <= 0 || at.Y >= highest)
                continue;
            highest = at.Y;
            top = new ListPlace(list.IndexFromContainer(container), Math.Max(0, -at.Y));
        }
        return top;
    }

    /// <summary><paramref name="list"/> scrolled so <paramref name="place"/>'s row is at the top, as far
    /// into it as it was.</summary>
    private static void ScrollTo(ListBox list, ListPlace place)
    {
        if (place.Row < 0 || place.Row >= list.ItemCount || Scroller(list) is not { } scroller)
            return;
        list.ScrollIntoView(place.Row);
        list.UpdateLayout();
        if (list.ContainerFromIndex(place.Row) is { } container && container.TranslatePoint(new Point(0, 0), scroller) is { } at)
        {
            scroller.Offset = scroller.Offset.WithY(Math.Max(0, scroller.Offset.Y + at.Y + place.Into));
            list.UpdateLayout();
        }
    }

    /// <summary>
    /// The lists scrolled back to where they were - once every row is as tall as it will be. Scrolled
    /// at once, the rows still without their composers' lines put the tune playing far below the
    /// view when those came in: Lightforce, 48th of Rob Hubbard's tunes, opened on the 26th to 37th.
    /// With nothing kept, below zero, the tune playing is scrolled into view instead. Kept by row, not
    /// by pixels, it opens on the same tunes in a bigger font too.
    /// </summary>
    public void RestoreScroll(ListPlace playlists, ListPlace tunes)
    {
        _restore = (playlists, tunes);
        _scrolledByHand = false;
        if (_headersRead)
            Dispatcher.UIThread.Post(ApplyRestore, DispatcherPriority.Background);
    }

    private void HeadersRead()
    {
        _headersRead = true;
        if (_restore is not null)
            Dispatcher.UIThread.Post(ApplyRestore, DispatcherPriority.Background);
    }

    private void ApplyRestore()
    {
        if (_restore is not { } restore || _scrolledByHand)
            return;
        _restore = null;
        _listBox.UpdateLayout();
        _tuneBox.UpdateLayout();
        ScrollTo(_listBox, restore.Playlists);
        if (restore.Tunes.Row >= 0)
            ScrollTo(_tuneBox, restore.Tunes);
        else if (_playing is not null && Shown?.Tunes.Contains(_playing) == true)
            _tuneBox.ScrollIntoView(_playing);
    }

    /// <summary>1 for the small text, more for bigger.</summary>
    public double TextScale
    {
        get => _scale;
        set
        {
            _scale = value;
            ApplyScale();
        }
    }

    private Control BuildLayout()
    {
        _caption.Text = "PLAYLISTS";
        _caption.FontWeight = FontWeight.Bold;
        _caption.LetterSpacing = 1.5;
        _caption.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _caption.VerticalAlignment = VerticalAlignment.Center;

        _new.Content = Icon(PlusIcon, 11, 11);
        _new.HorizontalAlignment = HorizontalAlignment.Right;
        _new.Padding = new Thickness(7);
        _new.Click += (_, _) => _ = NewPlaylistAsync(null);
        ToolTip.SetTip(_new, "New playlist");
        AutomationProperties.SetName(_new, "New playlist");
        Quiet(_new);

        var top = new Grid { Margin = new Thickness(18, 14, 10, 6), Children = { _caption, _new } };

        Restyle(_listBox);
        _listBox.ItemsSource = _lists;
        _listBox.MaxHeight = PlaylistsShown;
        _listBox.ItemTemplate = new FuncDataTemplate<Playlist>((_, _) => PlaylistRow(), supportsRecycling: true);
        _listBox.AddHandler(PointerPressedEvent, ListDragPressed, RoutingStrategies.Tunnel);
        _listBox.AddHandler(PointerMovedEvent, ListDragMoved, RoutingStrategies.Tunnel);
        _listBox.AddHandler(PointerReleasedEvent, ListDragReleased, RoutingStrategies.Tunnel);
        _listBox.AddHandler(PointerCaptureLostEvent, (_, _) => EndListDrag(), RoutingStrategies.Direct);

        // The line between the playlists and the tunes, dragged up or down to give either more room.
        _line.Height = 1;
        _line.Margin = new Thickness(18, 0);
        _line.VerticalAlignment = VerticalAlignment.Center;
        _line.IsHitTestVisible = false;
        _line.Background = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder);
        _splitter.ResizeDirection = GridResizeDirection.Rows;
        _splitter.Height = 22;
        _splitter.Background = Brushes.Transparent;
        _splitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        _splitter.Template = new FuncControlTemplate<GridSplitter>((_, _) => new Border { Background = Brushes.Transparent });
        ToolTip.SetTip(_splitter, "Drag to give the playlists or the tunes more room");
        _splitter.PointerEntered += (_, _) => _line.Background = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _splitter.PointerExited += (_, _) => _line.Background = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder);
        _splitter.DragStarted += (_, _) => HoldPlaylistsHeight();
        _splitter.DragCompleted += (_, _) =>
        {
            _playlistsHeight = Math.Round(_layout.RowDefinitions[1].ActualHeight);
            PlaylistsResized?.Invoke(_playlistsHeight);
        };

        _heading.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        _heading.FontWeight = FontWeight.SemiBold;
        _heading.TextTrimming = TextTrimming.CharacterEllipsis;
        _count.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _count.Margin = new Thickness(0, 2, 0, 0);

        // Round and lit, as a player's play button is: the whole playlist from its first tune.
        _playAll.Content = Icon(PlayIcon, 11, 13, new Thickness(3, 0, 0, 0));
        _playAll.Width = _playAll.Height = 40;
        _playAll.CornerRadius = new CornerRadius(20);
        _playAll.HorizontalAlignment = HorizontalAlignment.Right;
        _playAll.VerticalAlignment = VerticalAlignment.Center;
        _playAll.HorizontalContentAlignment = HorizontalAlignment.Center;
        _playAll.VerticalContentAlignment = VerticalAlignment.Center;
        _playAll.Click += (_, _) =>
        {
            if (Shown is { Tunes.Count: > 0 } playlist)
                Play?.Invoke(playlist, playlist.Tunes[0]);
        };
        ToolTip.SetTip(_playAll, "Play the playlist");
        AutomationProperties.SetName(_playAll, "Play the playlist");
        foreach (var (key, colour) in new (string, Color)[]
        {
            ("ButtonBackground", Accent), ("ButtonBackgroundPointerOver", Lighter(Accent)), ("ButtonBackgroundPressed", Accent),
            ("ButtonBackgroundDisabled", SidPlayerPalette.PanelBorder),
            ("ButtonForeground", SidPlayerPalette.Background), ("ButtonForegroundPointerOver", SidPlayerPalette.Background),
            ("ButtonForegroundPressed", SidPlayerPalette.Background), ("ButtonForegroundDisabled", SidPlayerPalette.ControlDisabled),
            ("ButtonBorderBrush", Colors.Transparent), ("ButtonBorderBrushPointerOver", Colors.Transparent),
            ("ButtonBorderBrushPressed", Colors.Transparent), ("ButtonBorderBrushDisabled", Colors.Transparent),
        })
            _playAll.Resources[key] = SidPlayerPalette.Brush(colour);

        var titles = new StackPanel { Margin = new Thickness(0, 0, 50, 0), Children = { _heading, _count } };
        var heading = new Grid { Margin = new Thickness(18, 0, 14, 4), Children = { titles, _playAll } };

        var sorts = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in Sorts)
        {
            if (sorts.Items.Count > 0)
                sorts.Items.Add(new Separator());
            foreach (var (sort, name, note) in group)
            {
                var item = Item(name, () => ChooseSort(sort));
                item.ToggleType = MenuItemToggleType.Radio;
                item.GroupName = "Sort";
                if (note is not null)
                    ToolTip.SetTip(item, note);
                _sortItems[sort] = item;
                sorts.Items.Add(item);
            }
        }
        _sort.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children = { Icon(SortIcon, 11, 10), _sortName },
        };
        _sort.Flyout = sorts;
        _sort.Margin = new Thickness(11, 0, 0, 6);
        _sort.Padding = new Thickness(7, 4);
        _sort.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(_sort, "Sort the tunes");
        AutomationProperties.SetName(_sort, "Sort the tunes");
        Quiet(_sort);

        _empty.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Margin = new Thickness(18, 8, 18, 0);

        Restyle(_tuneBox);
        _tuneBox.ItemTemplate = new FuncDataTemplate<PlaylistTune>((_, _) => TuneRow(), supportsRecycling: true);
        // Before the rows and the list see the pointer: a press may start a drag.
        _tuneBox.AddHandler(PointerPressedEvent, DragPressed, RoutingStrategies.Tunnel);
        _tuneBox.AddHandler(PointerMovedEvent, DragMoved, RoutingStrategies.Tunnel);
        _tuneBox.AddHandler(PointerReleasedEvent, DragReleased, RoutingStrategies.Tunnel);
        _tuneBox.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Direct);

        var tunes = new DockPanel();
        foreach (var docked in new Control[] { heading, _sort, _empty })
        {
            DockPanel.SetDock(docked, Dock.Top);
            tunes.Children.Add(docked);
        }
        tunes.Children.Add(_tuneBox);

        _layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto) { MinHeight = FewestPlaylistPixels });
        _layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _layout.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = FewestTunePixels });
        Grid.SetRow(_listBox, 1);
        Grid.SetRow(_line, 2);
        Grid.SetRow(_splitter, 2);
        Grid.SetRow(tunes, 3);
        _layout.Children.Add(top);
        _layout.Children.Add(_listBox);
        _layout.Children.Add(_line);
        _layout.Children.Add(_splitter);
        _layout.Children.Add(tunes);
        return _layout;
    }

    /// <summary>The playlists' height the user dragged them to, in pixels; 0 for as many as fit in
    /// <see cref="PlaylistsShown"/>.</summary>
    public double PlaylistsHeight
    {
        get => _playlistsHeight;
        set
        {
            _playlistsHeight = Math.Max(0, value);
            if (_playlistsHeight > 0)
            {
                _listBox.MaxHeight = double.PositiveInfinity;
                _layout.RowDefinitions[1].Height = new GridLength(Math.Max(FewestPlaylistPixels, _playlistsHeight), GridUnitType.Pixel);
            }
            else
            {
                _listBox.MaxHeight = PlaylistsShown;
                _layout.RowDefinitions[1].Height = GridLength.Auto;
            }
        }
    }

    /// <summary>A drag of the line starts from the height the playlists have now; from then on they
    /// are as tall as it is dragged, not only as tall as they need.</summary>
    private void HoldPlaylistsHeight()
    {
        var row = _layout.RowDefinitions[1];
        if (row.Height.IsAbsolute)
            return;
        double now = Math.Max(FewestPlaylistPixels, row.ActualHeight);
        _listBox.MaxHeight = double.PositiveInfinity;
        row.Height = new GridLength(now, GridUnitType.Pixel);
    }

    /// <summary>A playlist in the list: a folder or a heart, its name, how many tunes.</summary>
    private Control PlaylistRow()
    {
        var icon = new PathIcon { Data = HeartIcon, Width = 14 * _scale, Height = 14 * _scale, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            FontSize = 13 * _scale,
            Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Control),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        name.Bind(TextBlock.TextProperty, new Binding(nameof(Playlist.Name)));
        var count = new TextBlock
        {
            FontSize = 11 * _scale,
            Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle),
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.Bind(TextBlock.TextProperty, new Binding("Tunes.Count"));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(count, 2);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(count);
        var row = new Border { Padding = new Thickness(10, 7), Background = Brushes.Transparent, Child = grid };

        icon.Bind(PathIcon.ForegroundProperty, new Binding(nameof(Playlist.Heart))
        {
            Converter = new FuncValueConverter<string?, IBrush>(heart => SidPlayerPalette.Brush(HeartColour(heart))),
        });
        row.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (row.DataContext is Playlist playlist)
                OpenMenu(row, [Item("Rename...", () => _ = RenameAsync(playlist)),
                               HeartColourItem(playlist),
                               Item("Delete playlist...", () => _ = DeleteAsync(playlist))]);
        };
        return row;
    }

    /// <summary>The menu's choice of colours for a favourite's heart, each shown as a heart of
    /// its colour, and the one it has ticked.</summary>
    private MenuItem HeartColourItem(Playlist playlist)
    {
        var colours = new MenuItem { Header = "Heart colour" };
        var current = HeartColour(playlist.Heart);
        foreach (var (name, colour) in HeartColours)
        {
            var item = Item(colour == current ? $"{name}  \u2713" : name, () =>
            {
                playlist.Heart = colour == Heart ? null : colour.ToString();
                Favourites.Save(_favourites);
            });
            item.Icon = new PathIcon { Data = HeartIcon, Width = 13, Height = 12, Foreground = SidPlayerPalette.Brush(colour) };
            colours.Items.Add(item);
        }
        return colours;
    }

    /// <summary>The colour kept for a heart; the usual one for none, or for one that cannot be read.</summary>
    private static Color HeartColour(string? kept) => Color.TryParse(kept, out var colour) ? colour : Heart;

    /// <summary>A tune in the list: its number - bars while it plays - its name and author.</summary>
    private Control TuneRow()
    {
        var number = new TextBlock
        {
            FontSize = 11 * _scale,
            Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        number.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTune.Number)));
        number.Bind(IsVisibleProperty, new Binding(nameof(PlaylistTune.IsPlaying)) { Converter = BoolConverters.Not });
        var bars = Icon(BarsIcon, 12 * _scale, 13 * _scale);
        bars.Foreground = SidPlayerPalette.Brush(Accent);
        bars.HorizontalAlignment = HorizontalAlignment.Right;
        bars.Margin = new Thickness(0, 0, 12, 0);
        bars.Bind(IsVisibleProperty, new Binding(nameof(PlaylistTune.IsPlaying)));

        var title = new TextBlock { FontSize = 13 * _scale, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTune.Title)));
        title.Bind(TextBlock.ForegroundProperty, new Binding(nameof(PlaylistTune.IsPlaying))
        {
            Converter = new FuncValueConverter<bool, IBrush>(playing =>
                SidPlayerPalette.Brush(playing ? Accent : SidPlayerPalette.Title)),
        });
        var author = new TextBlock
        {
            FontSize = 11 * _scale,
            Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0),
        };
        author.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTune.Author)));
        author.Bind(IsVisibleProperty, new Binding(nameof(PlaylistTune.Author)) { Converter = StringConverters.IsNotNullOrEmpty });

        // A tune of several songs says how many, in a small badge at the end of its row.
        var songs = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            BorderBrush = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder),
            Child = new TextBlock { FontSize = 10 * _scale, Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle) },
        };
        ((TextBlock)songs.Child).Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTune.Songs)) { StringFormat = "{0} songs" });
        songs.Bind(IsVisibleProperty, new Binding(nameof(PlaylistTune.Songs))
        {
            Converter = new FuncValueConverter<int, bool>(count => count > 1),
        });

        // Its length, once it has been played.
        var length = new TextBlock
        {
            FontSize = 11 * _scale,
            Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0),
        };
        length.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTune.Length))
        {
            Converter = new FuncValueConverter<TimeSpan?, string>(t => t is { } known ? $"{(int)known.TotalMinutes}:{known.Seconds:00}" : ""),
        });
        length.Bind(IsVisibleProperty, new Binding(nameof(PlaylistTune.Length)) { Converter = ObjectConverters.IsNotNull });
        var ending = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { length, songs } };

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, author } };
        var grid = new Grid
        {
            // Built, not parsed from text: "37,5,*,Auto" - Big font's 37.5 where a comma is the
            // decimal point - is four columns, and the names' was 5 pixels wide.
            ColumnDefinitions =
            {
                new ColumnDefinition(30 * _scale, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Children = { number, bars, names, ending },
        };
        Grid.SetColumn(names, 1);
        Grid.SetColumn(ending, 2);
        var row = new Border { Padding = new Thickness(6, 6, 10, 6), Background = Brushes.Transparent, Child = grid };

        row.Tapped += (_, _) =>
        {
            if (!_dragged && row.DataContext is PlaylistTune tune && Shown is { } playlist)
                Play?.Invoke(playlist, tune);
        };
        row.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (row.DataContext is not PlaylistTune tune)
                return;
            var add = new MenuItem { Header = "Add to playlist" };
            foreach (var item in AddToItems(tune.Path))
                add.Items.Add(item);
            var items = new List<Control> { add, Item("Set length...", () => _ = SetLengthAsync(tune)) };
            if (Shown is { } playlist)
                items.Add(Item("Remove from playlist", () => Remove(playlist, tune)));
            OpenMenu(row, items);
        };
        return row;
    }

    /// <summary>A right-click menu, its items made before it opens: one filled as it opens shows
    /// empty - Avalonia has built it by then.</summary>
    private static void OpenMenu(Control target, IReadOnlyList<Control> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
            menu.Items.Add(item);
        menu.Open(target);
    }

    // ------------------------------------------------------------------ moving a playlist

    private void ListDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_listBox).Properties.IsLeftButtonPressed
            || (e.Source as StyledElement)?.DataContext is not Playlist playlist)
            return;
        _dragList = playlist;
        _dragListStart = e.GetPosition(_listBox);
    }

    /// <summary>The playlist held follows the pointer, past the middle of the next one up or down
    /// taking its place - as a tune does in its playlist.</summary>
    private void ListDragMoved(object? sender, PointerEventArgs e)
    {
        if (_dragList is not { } playlist || !_lists.Contains(playlist))
            return;

        var at = e.GetPosition(_listBox);
        if (!_draggingList)
        {
            if (Math.Abs(at.Y - _dragListStart.Y) < DragThreshold)
                return;
            _draggingList = true;
            e.Pointer.Capture(_listBox);
            _listBox.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            _listBox.SelectedItem = playlist;
        }

        int from = _lists.IndexOf(playlist);
        if (IndexAt(_listBox, at.Y) is { } to && to != from)
        {
            _lists.Move(from, to);
            _listBox.SelectedItem = playlist;
        }
        ScrollNearEdge(_listBox, at.Y);
        e.Handled = true;
    }

    private void ListDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggingList)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        EndListDrag();
    }

    /// <summary>The drag over: the playlists kept in their new order.</summary>
    private void EndListDrag()
    {
        if (_draggingList)
        {
            _favourites.Clear();
            _favourites.AddRange(_lists);
            Favourites.Save(_favourites);
        }
        _dragList = null;
        _draggingList = false;
        _listBox.Cursor = null;
    }

    // ------------------------------------------------------------------ moving a tune

    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragged = false;
        if (!e.GetCurrentPoint(_tuneBox).Properties.IsLeftButtonPressed
            || (e.Source as StyledElement)?.DataContext is not PlaylistTune tune)
            return;
        _dragTune = tune;
        _dragStart = e.GetPosition(_tuneBox);
    }

    /// <summary>The tune held follows the pointer: past the middle of the next row up or down, it
    /// takes that row's place. Near the list's top or bottom, the list scrolls.</summary>
    private void DragMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTune is not { } tune || Shown is not { } playlist || !playlist.Tunes.Contains(tune))
            return;

        var at = e.GetPosition(_tuneBox);
        if (!_dragging)
        {
            if (Math.Abs(at.Y - _dragStart.Y) < DragThreshold)
                return;
            _dragging = _dragged = true;
            e.Pointer.Capture(_tuneBox);
            _tuneBox.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            // Only the user's own order can be moved in: back to it, the tune still held.
            if (playlist.Sort != TuneSort.Own)
            {
                playlist.SortBy(TuneSort.Own);
                ShowSort();
                _tuneBox.ScrollIntoView(tune);
            }
            _tuneBox.SelectedItem = tune;
        }

        int from = playlist.Tunes.IndexOf(tune);
        if (IndexAt(_tuneBox, at.Y) is { } to && to != from)
        {
            playlist.Tunes.Move(from, to);
            _tuneBox.SelectedItem = tune;
        }
        ScrollNearEdge(_tuneBox, at.Y);
        e.Handled = true;
    }

    private void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        EndDrag();
    }

    /// <summary>The drag over: the playlist's new order kept.</summary>
    private void EndDrag()
    {
        if (_dragging && Shown is not null)
            Favourites.Save(_favourites);
        _dragTune = null;
        _dragging = false;
        _tuneBox.Cursor = null;
    }

    /// <summary>The place in the list of the row at <paramref name="y"/> in it - the first or last
    /// row shown when it is above or below them all.</summary>
    private static int? IndexAt(ListBox list, double y)
    {
        int? first = null, last = null;
        double top = double.MaxValue, bottom = double.MinValue;
        foreach (var container in list.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, list) is not { } origin)
                continue;
            int index = list.IndexFromContainer(container);
            double height = container.Bounds.Height;
            if (y >= origin.Y && y < origin.Y + height)
                return index;
            if (origin.Y < top)
                (top, first) = (origin.Y, index);
            if (origin.Y + height > bottom)
                (bottom, last) = (origin.Y + height, index);
        }
        return y < top ? first : y >= bottom ? last : null;
    }

    private void ScrollNearEdge(ListBox list, double y)
    {
        if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroller)
            return;
        double edge = 24 * _scale, step = 12 * _scale;
        if (y < edge)
            scroller.Offset = scroller.Offset.WithY(Math.Max(0, scroller.Offset.Y - step));
        else if (y > list.Bounds.Height - edge)
            scroller.Offset = scroller.Offset.WithY(scroller.Offset.Y + step);
    }

    /// <summary>The playlist whose tunes are shown.</summary>
    private Playlist? Shown => _listBox.SelectedItem as Playlist;

    /// <summary>The name of the playlist shown; null with none.</summary>
    public string? ShownName => Shown?.Name;

    /// <summary>The playlist called <paramref name="name"/>; null when there is none.</summary>
    public Playlist? Find(string? name) =>
        name is null ? null : _lists.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The playlist called <paramref name="name"/> shown, if there is one.</summary>
    public void Show(string? name)
    {
        if (Find(name) is { } playlist)
            _listBox.SelectedItem = playlist;
    }

    private void ShowTunes()
    {
        var playlist = Shown;
        _tuneBox.ItemsSource = playlist?.Tunes;
        _heading.Text = playlist?.Name ?? "";
        _playAll.IsEnabled = playlist is { Tunes.Count: > 0 };
        _playAll.IsVisible = playlist is not null;
        _empty.Text = playlist is null
            ? "No playlists yet. Drop a folder of .sid tunes on the window, or open one from the menu, to play them all as a playlist - or start one of favourites with +."
            : "No tunes yet. Play one, then add it here with Add to playlist - or right-click a tune in another playlist.";
        _empty.IsVisible = playlist is null or { Tunes.Count: 0 };
        _sort.IsVisible = playlist is { Tunes.Count: > 0 };
        ShowSort();
        Count();
        if (_playing is not null && playlist?.Tunes.Contains(_playing) == true)
            _tuneBox.SelectedItem = _playing;
    }

    /// <summary>The shown playlist in this order, and the tune playing still lit and in view. A
    /// favourite keeps its sort.</summary>
    private void ChooseSort(TuneSort sort)
    {
        if (Shown is not { } playlist)
            return;
        playlist.SortBy(sort);
        Favourites.Save(_favourites);
        ShowSort();
        LightPlaying(playlist);
    }

    private void ShowSort()
    {
        var sort = Shown?.Sort ?? TuneSort.Own;
        foreach (var (each, item) in _sortItems)
            item.IsChecked = each == sort;
        _sortName.Text = _sortItems.TryGetValue(sort, out var chosen) ? (string?)chosen.Header : "";
    }

    /// <summary>Sorting empties the list's selection: the tune playing chosen again, and in view.</summary>
    private void LightPlaying(Playlist playlist)
    {
        if (Shown == playlist && _playing is not null && playlist.Tunes.Contains(_playing))
        {
            _tuneBox.SelectedItem = _playing;
            _tuneBox.ScrollIntoView(_playing);
        }
    }

    /// <summary>The song to play first of a tune, kept with the favourites; null, the tune's own.</summary>
    public void ChooseFirstSong(string path, int? song)
    {
        Favourites.SetFirstSong(path, song);
        Favourites.Save(_favourites);
    }

    /// <summary>How many times in all a tune plays, kept with the favourites: null, once.</summary>
    public void ChooseRepeat(string path, int? times)
    {
        Favourites.SetRepeat(path, times);
        Favourites.Save(_favourites);
    }

    /// <summary>A song's length set by hand, from a tune's right-click: kept in songlengths.json, and
    /// shown and played to at once - its tune's path.</summary>
    public event Action<string>? LengthSet;

    private async Task SetLengthAsync(PlaylistTune tune)
    {
        byte[] bytes;
        SidFile? file;
        try
        {
            bytes = await File.ReadAllBytesAsync(tune.Path);
            file = bytes.Length >= 4 && bytes[1] == 'S' && bytes[2] == 'I' && bytes[3] == 'D' ? SidFile.Parse(bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SidPlaybackException)
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        string key = SongLengths.KeyOf(bytes), history = TuneHistory.KeyOf(bytes);
        string name = string.IsNullOrWhiteSpace(file?.Author) ? tune.Title : $"{tune.Title} by {file.Author.Trim()}";
        (TimeSpan? Length, bool Own) LengthOf(int song) =>
            SongLengths.User(key, tune.Path, song) is { } own ? (own, true)
            : (TuneHistory.Of(history)?.Lengths.TryGetValue(song, out double seconds) == true ? TimeSpan.FromSeconds(seconds)
               : SongLengths.Hvsc(key, song), false);

        int first = Favourites.FirstSong(tune.Path) ?? file?.StartSong ?? 1;
        if (await LengthWindow.AskAsync(owner, name, Math.Max(1, file?.Songs ?? 1), first, LengthOf) is not { } chosen)
            return;
        SongLengths.SetUser(key, tune.Path, name, chosen.Song, chosen.Length);
        if (chosen.Length is { } length)
            TuneHistory.Measured(history, chosen.Song, length);
        else
            TuneHistory.Forget(history, chosen.Song);
        Measured(history);
        LengthSet?.Invoke(tune.Path);
    }

    /// <summary>A tune's length measured: shown wherever the tune is listed.</summary>
    public void Measured(string key)
    {
        var length = TuneHistory.Of(key)?.Length;
        foreach (var tune in _lists.SelectMany(p => p.Tunes).Where(t => t.Key == key))
            tune.Length = length;
    }

    private void Count()
    {
        var playlist = Shown;
        _count.Text = playlist is null ? "" : playlist.Tunes.Count == 1 ? "1 tune" : $"{playlist.Tunes.Count} tunes";
    }

    /// <summary>A folder's tunes as a playlist of its own, kept with the others and shown, with
    /// their names read while it plays. The folder opened again, with the same tunes, is the
    /// playlist it made before, if that still has the folder's name; else a new one beside it.</summary>
    public Playlist AddFolder(string name, IReadOnlyList<string> paths)
    {
        var same = _favourites.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
            && p.Tunes.Count == paths.Count
            && p.OwnOrder.Select(t => t.Path).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase));
        if (same is not null)
        {
            ShowRequested?.Invoke();
            _listBox.SelectedItem = same;
            return same;
        }

        string unique = name;
        for (int n = 2; _favourites.Any(p => string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
            unique = $"{name} {n}";
        var playlist = new Playlist(unique, paths);
        _favourites.Add(playlist);
        _lists.Add(playlist);
        Favourites.Save(_favourites);
        ShowRequested?.Invoke();
        _listBox.SelectedItem = playlist;
        ReadHeaders(playlist.Tunes);
        return playlist;
    }

    /// <summary>The tune playing lit, wherever it is; its playlist's tunes shown and scrolled to it.</summary>
    public void ShowPlaying(Playlist? playlist, PlaylistTune? tune)
    {
        if (_playing is not null)
            _playing.IsPlaying = false;
        _playing = tune;
        if (tune is null)
            return;

        tune.IsPlaying = true;
        if (playlist is not null && Shown != playlist)
            _listBox.SelectedItem = playlist;
        _tuneBox.SelectedItem = tune;
        _tuneBox.ScrollIntoView(tune);
    }

    /// <summary>The favourites to add a tune to - one already holding it ticked and not offered
    /// again - and a new playlist for it.</summary>
    public IEnumerable<Control> AddToItems(string path)
    {
        foreach (var favourite in _favourites)
        {
            bool holds = favourite.Contains(path);
            var item = Item(favourite.Name, () => Add(favourite, path));
            item.IsEnabled = !holds;
            if (holds)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = true;
            }
            yield return item;
        }
        if (_favourites.Count > 0)
            yield return new Separator();
        yield return Item("New playlist...", () => _ = NewPlaylistAsync(path));
    }

    private void Add(Playlist favourite, string path)
    {
        if (favourite.Contains(path))
            return;
        var tune = new PlaylistTune(path);
        favourite.Add(tune);
        ReadHeaders([tune]);
        Favourites.Save(_favourites);
        if (Shown == favourite)
            ShowTunes();
    }

    private void Remove(Playlist favourite, PlaylistTune tune)
    {
        favourite.Remove(tune);
        Favourites.Save(_favourites);
        ShowTunes();
    }

    /// <summary>A new favourite playlist, named by the user - with <paramref name="path"/> in it
    /// when one is given - shown.</summary>
    private async Task NewPlaylistAsync(string? path)
    {
        string suggestion = "Favourites";
        for (int n = 2; _favourites.Any(p => string.Equals(p.Name, suggestion, StringComparison.OrdinalIgnoreCase)); n++)
            suggestion = $"Favourites {n}";

        if (await MessageWindow.PromptAsync(_owner, "New playlist", "A name for the playlist:", suggestion, "Create") is not { } name)
            return;

        var playlist = new Playlist(name, []);
        _favourites.Add(playlist);
        _lists.Add(playlist);
        if (path is not null)
            Add(playlist, path);
        Favourites.Save(_favourites);
        ShowRequested?.Invoke();
        _listBox.SelectedItem = playlist;
    }

    private async Task RenameAsync(Playlist playlist)
    {
        if (await MessageWindow.PromptAsync(_owner, "Rename playlist", "A new name for the playlist:", playlist.Name, "Rename") is not { } name)
            return;
        playlist.Name = name;
        Favourites.Save(_favourites);
        if (Shown == playlist)
            _heading.Text = name;
    }

    private async Task DeleteAsync(Playlist playlist)
    {
        if (!await MessageWindow.AskAsync(_owner, "Delete playlist",
                $"Delete the playlist \"{playlist.Name}\"? Its tunes stay where they are; only the list goes.", "Delete", "Cancel"))
            return;
        _favourites.Remove(playlist);
        _lists.Remove(playlist);
        Favourites.Save(_favourites);
        if (_listBox.SelectedItem is null && _lists.Count > 0)
            _listBox.SelectedIndex = 0;
        ShowTunes();
    }

    /// <summary>Names and authors from the tunes' headers, read off the UI thread and shown as they
    /// come; a sorted playlist sorted again when they are all in, as it sorts by them.</summary>
    private void ReadHeaders(IEnumerable<PlaylistTune> tunes, Action? done = null)
    {
        var list = tunes.ToList();
        Task.Run(() =>
        {
            foreach (var tune in list)
            {
                if (tune.ReadHeader() is { } header)
                    Dispatcher.UIThread.Post(() => tune.Take(header), DispatcherPriority.Background);
            }
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var playlist in _lists.Where(p => p.Sort != TuneSort.Own && p.Tunes.Any(list.Contains)))
                {
                    if (_dragging && Shown == playlist)
                        continue;
                    playlist.Resort();
                    LightPlaying(playlist);
                }
                done?.Invoke();
            }, DispatcherPriority.Background);
        });
    }

    private void ApplyScale()
    {
        _caption.FontSize = 11 * _scale;
        _heading.FontSize = 18 * _scale;
        _count.FontSize = 12 * _scale;
        _empty.FontSize = 12 * _scale;
        _sortName.FontSize = 12 * _scale;
        Width = StripWidth * (1 + (_scale - 1) / 2);
        // The rows are made at the size: made again.
        var selected = _listBox.SelectedItem;
        _listBox.ItemsSource = null;
        _listBox.ItemsSource = _lists;
        _listBox.SelectedItem = selected;
        ShowTunes();
    }

    // ------------------------------------------------------------------ looks

    /// <summary>A list in the strip's colours: see-through, rows lit softly on hover, the chosen
    /// one in the accent.</summary>
    private static void Restyle(ListBox list)
    {
        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        foreach (var (key, colour) in new (string, Color)[]
        {
            ("SystemControlHighlightListLowBrush", SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 14)),
            ("SystemControlHighlightListMediumBrush", SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 24)),
            ("SystemControlHighlightListAccentLowBrush", SidPlayerPalette.WithAlpha(Accent, 38)),
            ("SystemControlHighlightListAccentMediumBrush", SidPlayerPalette.WithAlpha(Accent, 52)),
            ("SystemControlHighlightListAccentHighBrush", SidPlayerPalette.WithAlpha(Accent, 64)),
        })
            list.Resources[key] = SidPlayerPalette.Brush(colour);
    }

    /// <summary>A button that is only its icon until the pointer is on it.</summary>
    private static void Quiet(Button button)
    {
        button.CornerRadius = new CornerRadius(6);
        foreach (var (key, colour) in new (string, Color)[]
        {
            ("ButtonBackground", Colors.Transparent), ("ButtonBackgroundPointerOver", SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 20)),
            ("ButtonBackgroundPressed", SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 30)),
            ("ButtonForeground", SidPlayerPalette.Subtitle), ("ButtonForegroundPointerOver", SidPlayerPalette.Title),
            ("ButtonForegroundPressed", SidPlayerPalette.Title),
            ("ButtonBorderBrush", Colors.Transparent), ("ButtonBorderBrushPointerOver", Colors.Transparent),
            ("ButtonBorderBrushPressed", Colors.Transparent),
        })
            button.Resources[key] = SidPlayerPalette.Brush(colour);
    }

    private static PathIcon Icon(Geometry data, double width, double height, Thickness margin = default) =>
        new() { Data = data, Width = width, Height = height, Margin = margin };

    /// <summary>The heart the window's Add to playlist button wears.</summary>
    public static PathIcon HeartGlyph() => new()
    {
        Data = HeartIcon,
        Width = 13,
        Height = 12,
        Foreground = SidPlayerPalette.Brush(Heart),
    };

    private static Color Lighter(Color colour) =>
        Color.FromRgb((byte)((colour.R + 255) / 2), (byte)((colour.G + 255) / 2), (byte)((colour.B + 255) / 2));

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }
}
