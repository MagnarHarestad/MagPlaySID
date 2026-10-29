using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// The strip down the left of the window: the playlists - a folder dropped on the window, and the
/// favourites the user made from tunes they played - and below them the tunes of the one chosen.
/// A click on a tune plays it, and the playlist from there. Shown once there is a playlist.
/// </summary>
internal sealed class PlaylistStrip : Border
{
    public const double StripWidth = 290;

    private static readonly Geometry FolderIcon = Geometry.Parse("M1,3 H6 L7.5,4.5 H15 V13 H1 Z");
    private static readonly Geometry HeartIcon = Geometry.Parse(
        "M8,14 C8,14 1,9.5 1,5.2 C1,3 2.7,1.5 4.6,1.5 C6,1.5 7.3,2.3 8,3.5 C8.7,2.3 10,1.5 11.4,1.5 C13.3,1.5 15,3 15,5.2 C15,9.5 8,14 8,14 Z");
    private static readonly Geometry PlusIcon = Geometry.Parse("M7,1 H9 V7 H15 V9 H9 V15 H7 V9 H1 V7 H7 Z");
    private static readonly Geometry PlayIcon = Geometry.Parse("M0,0 L10,6 L0,12 Z");
    private static readonly Geometry BarsIcon = Geometry.Parse("M0,6 H3 V14 H0 Z M5,0 H8 V14 H5 Z M10,4 H13 V14 H10 Z");

    /// <summary>The colour of what plays: the second voice's, as the player's buttons light.</summary>
    private static readonly Color Accent = SidPlayerPalette.VoiceColour(1);
    private static readonly Color Heart = SidPlayerPalette.VoiceColour(0);

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
    private Playlist? _folder;
    private PlaylistTune? _playing;
    private double _scale = 1;

    /// <summary>A tune chosen to play, and the playlist to go on with after it.</summary>
    public event Action<Playlist, PlaylistTune>? Play;

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
        ReadHeaders(_favourites.SelectMany(p => p.Tunes));
        IsVisible = _lists.Count > 0;
        ApplyScale();
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
        _listBox.MaxHeight = 210;
        _listBox.ItemTemplate = new FuncDataTemplate<Playlist>((_, _) => PlaylistRow(), supportsRecycling: true);

        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(18, 10, 18, 12),
            Background = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder),
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
        var heading = new Grid { Margin = new Thickness(18, 0, 14, 8), Children = { titles, _playAll } };

        _empty.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Margin = new Thickness(18, 8, 18, 0);
        _empty.Text = "No tunes yet. Play one, then add it here with Add to playlist - or right-click a tune in another playlist.";

        Restyle(_tuneBox);
        _tuneBox.ItemTemplate = new FuncDataTemplate<PlaylistTune>((_, _) => TuneRow(), supportsRecycling: true);

        var panel = new DockPanel();
        foreach (var docked in new Control[] { top, _listBox, divider, heading, _empty })
        {
            DockPanel.SetDock(docked, Dock.Top);
            panel.Children.Add(docked);
        }
        panel.Children.Add(_tuneBox);
        return panel;
    }

    /// <summary>A playlist in the list: a folder or a heart, its name, how many tunes.</summary>
    private Control PlaylistRow()
    {
        var icon = new PathIcon { Width = 14 * _scale, Height = 14 * _scale, VerticalAlignment = VerticalAlignment.Center };
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

        var menu = new ContextMenu();
        row.ContextMenu = menu;
        row.DataContextChanged += (_, _) =>
        {
            bool folder = row.DataContext is Playlist { IsFolder: true };
            icon.Data = folder ? FolderIcon : HeartIcon;
            icon.Foreground = SidPlayerPalette.Brush(folder ? SidPlayerPalette.Control : Heart);
            row.ContextMenu = folder ? null : menu;
        };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (row.DataContext is not Playlist playlist || playlist.IsFolder)
                return;
            menu.Items.Add(Item("Rename...", () => _ = RenameAsync(playlist)));
            menu.Items.Add(Item("Delete playlist...", () => _ = DeleteAsync(playlist)));
        };
        return row;
    }

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

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, author } };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{30 * _scale},*"),
            Children = { number, bars, names },
        };
        Grid.SetColumn(names, 1);
        var row = new Border { Padding = new Thickness(6, 6, 10, 6), Background = Brushes.Transparent, Child = grid };

        row.Tapped += (_, _) =>
        {
            if (row.DataContext is PlaylistTune tune && Shown is { } playlist)
                Play?.Invoke(playlist, tune);
        };
        var menu = new ContextMenu();
        row.ContextMenu = menu;
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (row.DataContext is not PlaylistTune tune)
                return;
            var add = new MenuItem { Header = "Add to playlist" };
            foreach (var item in AddToItems(tune.Path))
                add.Items.Add(item);
            menu.Items.Add(add);
            if (Shown is { IsFolder: false } playlist)
                menu.Items.Add(Item("Remove from playlist", () => Remove(playlist, tune)));
        };
        return row;
    }

    /// <summary>The playlist whose tunes are shown.</summary>
    private Playlist? Shown => _listBox.SelectedItem as Playlist;

    private void ShowTunes()
    {
        var playlist = Shown;
        _tuneBox.ItemsSource = playlist?.Tunes;
        _heading.Text = playlist?.Name ?? "";
        _playAll.IsEnabled = playlist is { Tunes.Count: > 0 };
        _empty.IsVisible = playlist is { IsFolder: false, Tunes.Count: 0 };
        Count();
        if (_playing is not null && playlist?.Tunes.Contains(_playing) == true)
            _tuneBox.SelectedItem = _playing;
    }

    private void Count()
    {
        var playlist = Shown;
        _count.Text = playlist is null ? "" : playlist.Tunes.Count == 1 ? "1 tune" : $"{playlist.Tunes.Count} tunes";
    }

    /// <summary>A folder's tunes as the playlist at the top - in place of the folder before it -
    /// shown, with their names read while it plays.</summary>
    public Playlist ShowFolder(string name, IReadOnlyList<string> paths)
    {
        if (_folder is not null)
            _lists.Remove(_folder);
        _folder = new Playlist(name, isFolder: true, paths);
        _lists.Insert(0, _folder);
        _listBox.SelectedItem = _folder;
        IsVisible = true;
        ReadHeaders(_folder.Tunes);
        return _folder;
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
        favourite.Tunes.Add(tune);
        ReadHeaders([tune]);
        Favourites.Save(_favourites);
        if (Shown == favourite)
            ShowTunes();
    }

    private void Remove(Playlist favourite, PlaylistTune tune)
    {
        favourite.Tunes.Remove(tune);
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

        var playlist = new Playlist(name, isFolder: false, []);
        _favourites.Add(playlist);
        _lists.Add(playlist);
        if (path is not null)
            Add(playlist, path);
        Favourites.Save(_favourites);
        IsVisible = true;
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
        IsVisible = _lists.Count > 0;
    }

    /// <summary>Names and authors from the tunes' headers, read off the UI thread and shown as they come.</summary>
    private static void ReadHeaders(IEnumerable<PlaylistTune> tunes)
    {
        var list = tunes.ToList();
        Task.Run(() =>
        {
            foreach (var tune in list)
            {
                if (tune.ReadHeader() is { } header)
                    Dispatcher.UIThread.Post(() => (tune.Title, tune.Author) = header, DispatcherPriority.Background);
            }
        });
    }

    private void ApplyScale()
    {
        _caption.FontSize = 11 * _scale;
        _heading.FontSize = 18 * _scale;
        _count.FontSize = 12 * _scale;
        _empty.FontSize = 12 * _scale;
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
