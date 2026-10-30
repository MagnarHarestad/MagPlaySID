using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MagPlay.C64;
using MagPlaySid.Player;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>
/// MagPlaySID's window: a .sid plays - opened, dropped on the window from Explorer, Finder or the
/// desktop, or named on the command line - with the MagSID player's spectrum and a scope per
/// voice, below them the tune transcribed into a score whose rows scroll with the music, and at
/// the bottom a keyboard lit by the notes the voices play. A folder dropped on it becomes a
/// playlist down the left, its tunes played one after another; favourite playlists are made there
/// from any tune played.
/// The sound is always the tune's own 6502 code, run on the C64 - any tune, RSIDs and digis too;
/// the score is only a view of it.
/// </summary>
internal sealed class PlayerWindow : Window
{
    private static readonly FontFamily Mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    /// <summary>Seconds of a tune transcribed first, to show while the whole of it is.</summary>
    private const int OpeningSeconds = 30;

    /// <summary>The big text against the small: a quarter larger.</summary>
    private const double BigFont = 1.25;

    /// <summary>How many times its speed a tune plays while it fast-forwards.</summary>
    private const int FastForwardSpeed = 4;

    /// <summary>Into a tune further than this, Previous starts it again rather than going back a tune.</summary>
    private static readonly TimeSpan PreviousRestarts = TimeSpan.FromSeconds(3);

    /// <summary>How long a playlist's tune plays when its length cannot be measured.</summary>
    private static readonly TimeSpan UnmeasuredLength = TimeSpan.FromMinutes(3);

    private readonly SpectrumView _spectrum = new();
    private VoiceScopeView[] _scopes = [];
    private readonly Grid _scopeRow = new() { Height = 110, Margin = new Thickness(18, 6, 18, 4) };
    private readonly TransportButton _previous = new(TransportGlyph.Previous);
    private readonly TransportButton _playPause = new(TransportGlyph.Pause);
    private readonly TransportButton _fastForward = new(TransportGlyph.FastForward);
    private readonly TransportButton _next = new(TransportGlyph.Next);
    private readonly TextBlock _title = new();
    private readonly TextBlock _details = new();
    private readonly TextBlock _elapsed = new();
    private readonly TextBlock _empty = new();
    private readonly TextBlock _retroStatus = new();
    private readonly Button _open = new();
    private readonly Button _favourite = new();
    private readonly TextBlock _favouriteText = new() { Text = "Add to playlist", VerticalAlignment = VerticalAlignment.Center };
    private readonly ScoreView _view = new();
    private readonly PianoView _piano = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool[] _muted = new bool[9];
    private readonly Settings _settings = Settings.Load();
    private readonly MenuItem _checkForUpdates = new();
    private readonly MenuItem _smallFont = new();
    private readonly MenuItem _bigFont = new();
    private readonly MenuItem _showPlaylists = new();
    private readonly MenuItem _everySong = new();
    private readonly TextBlock _songLabel = new() { Text = "SONG", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _songPills = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Grid _songRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 12, 0, 0) };
    private readonly PlaylistStrip _strip;

    private SidFile? _file;
    private C64PlaybackEngine? _engine;
    private IAudioOutput? _output;
    private SidSongLength.Result? _length;
    private int _song;
    private string? _path;

    /// <summary>The playlist playing and its tune: the next follows when this one ends.</summary>
    private Playlist? _queue;
    private PlaylistTune? _queueTune;
    /// <summary>The playlist's last tune has played: nothing follows it.</summary>
    private bool _finished;
    /// <summary>The tune's length could not be measured: <see cref="UnmeasuredLength"/> stands in.</summary>
    private bool _lengthUnknown;

    private Transcription? _transcription;
    private CancellationTokenSource? _transcribing;

    public PlayerWindow(string? path = null)
    {
        Title = UpdateCheck.Product;
        if (typeof(PlayerWindow).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) is { } icon)
            Icon = new WindowIcon(typeof(PlayerWindow).Assembly.GetManifestResourceStream(icon)!);
        Width = 1180;
        Height = 900;
        MinWidth = 900;
        MinHeight = 600;
        Background = SidPlayerPalette.Brush(SidPlayerPalette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _strip = new PlaylistStrip(this);
        _strip.Play += PlayFromList;
        _strip.ShowRequested += () => ShowPlaylists(true);
        Content = BuildLayout();
        ShowFontSize();
        ShowPlaylists(_settings.ShowPlaylists ?? _strip.HasPlaylists, remember: false);

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                TogglePause();
                e.Handled = true;
            }
            else if (e.Key == Key.O && (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
            {
                _ = OpenAsync();
                e.Handled = true;
            }
        };

        // The shortcuts before any control: the arrows would otherwise move the keyboard's focus.
        // Shift too: on some keyboards + is a shifted key.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && Shortcut(e.Key) is { } shortcut)
            {
                shortcut();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // A .sid dropped anywhere on the window loads and plays; a folder - or several tunes -
        // becomes a playlist, played from its first tune.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.TryGetFile() is not null ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var dropped = (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (dropped is [var one] && File.Exists(one))
                PlayFile(one);
            else if (dropped.Count > 0)
                _ = OpenFolderAsync(dropped);
        });

        Closed += (_, _) =>
        {
            _timer.Stop();
            _transcribing?.Cancel();
            StopPlayback();
        };

        ShowEmpty();
        if (path is not null)
            Opened += (_, _) => PlayFile(path);
        Opened += async (_, _) => await CheckForUpdatesAsync();
    }

    private Control BuildLayout()
    {
        _title.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        _details.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _details.Margin = new Thickness(0, 2, 0, 0);

        // Under the tune's name and details, in the player's own colours rather than the theme's.
        _open.Content = "Open .sid...";
        _open.Click += (_, _) => _ = OpenAsync();
        _open.HorizontalAlignment = HorizontalAlignment.Left;
        _open.Margin = new Thickness(0, 10, 0, 0);
        _open.Padding = new Thickness(14, 5);
        InPlayerColours(_open);

        // Beside it, the tune playing into a favourite playlist.
        _favourite.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children = { PlaylistStrip.HeartGlyph(), _favouriteText },
        };
        _favourite.Margin = new Thickness(8, 10, 0, 0);
        _favourite.Padding = new Thickness(12, 5);
        InPlayerColours(_favourite);
        // The menu made new on each click, its items before it opens - one filled as it opens shows empty.
        _favourite.Click += (_, _) =>
        {
            if (_path is null)
                return;
            var addTo = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var item in _strip.AddToItems(_path))
                addTo.Items.Add(item);
            addTo.ShowAt(_favourite);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _open, _favourite } };
        // The tune's songs - sub-tunes - as pills, the one playing lit; shown for a tune of several.
        _songLabel.FontWeight = FontWeight.Bold;
        _songLabel.LetterSpacing = 1.5;
        _songLabel.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _songLabel.Margin = new Thickness(0, 0, 12, 0);
        var pills = new ScrollViewer
        {
            Content = _songPills,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(pills, 1);
        _songRow.Children.Add(_songLabel);
        _songRow.Children.Add(pills);

        var details = new StackPanel { Children = { _title, _details, buttons, _songRow } };
        var header = new Grid { Margin = new Thickness(18, 12, 18, 6), Children = { details, BuildMenu() } };

        BuildScopes(3);

        _elapsed.FontFamily = Mono;
        _elapsed.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        _elapsed.VerticalAlignment = VerticalAlignment.Center;
        _elapsed.Margin = new Thickness(14, 0);
        _elapsed.Text = "00:00 - --:--";

        _previous.Click += PreviousTune;
        _fastForward.Click += ToggleFastForward;
        ToolTip.SetTip(_previous, "Previous tune - or this one from its start");
        ToolTip.SetTip(_fastForward, "Fast-forward, 4x (F)");
        ToolTip.SetTip(_next, "Next tune in the playlist");
        _playPause.Click += TogglePause;
        _next.Click += NextTune;

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(18, 4, 18, 6),
            Children = { _previous, _elapsed, _playPause, _fastForward, _next },
        };

        _empty.Text = "Open a .sid file - or drop one here";
        _empty.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;

        var spectrumHost = new Grid { Height = 150, Margin = new Thickness(18, 4) };
        spectrumHost.Children.Add(_spectrum);
        spectrumHost.Children.Add(_empty);

        // The Retro view: read-only - it follows the music; a click picks the voice whose
        // instrument it shows.
        _view.Width = _view.ContentWidth;
        _view.Focusable = false;
        _retroStatus.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _retroStatus.Margin = new Thickness(0, 0, 0, 4);
        var retro = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 4, 18, 8) };
        DockPanel.SetDock(_retroStatus, Dock.Top);
        retro.Children.Add(_retroStatus);
        retro.Children.Add(_view);

        var root = new DockPanel();
        DockPanel.SetDock(_strip, Dock.Left);
        root.Children.Add(_strip);
        foreach (var top in new Control[] { header, spectrumHost, _scopeRow, transport })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }
        _piano.Margin = new Thickness(18, 0, 18, 12);
        DockPanel.SetDock(_piano, Dock.Bottom);
        root.Children.Add(_piano);
        // Sideways when the rows are wider than the window: big text, or a 2SID or 3SID tune's columns.
        root.Children.Add(new ScrollViewer
        {
            Content = retro,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        return root;
    }

    /// <summary>A button in the player's own colours rather than the theme's.</summary>
    private static void InPlayerColours(Button button)
    {
        button.CornerRadius = new CornerRadius(4);
        button.BorderThickness = new Thickness(1);
        var panel = SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground);
        var border = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder);
        var lit = SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.VoiceColour(1), 40));
        var edge = SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.VoiceColour(1), 150));
        foreach (var (key, value) in new (string, IBrush)[]
        {
            ("ButtonBackground", panel), ("ButtonBackgroundPointerOver", lit), ("ButtonBackgroundPressed", panel),
            ("ButtonBorderBrush", border), ("ButtonBorderBrushPointerOver", edge), ("ButtonBorderBrushPressed", edge),
            ("ButtonForeground", SidPlayerPalette.Brush(SidPlayerPalette.Control)),
            ("ButtonForegroundPointerOver", SidPlayerPalette.Brush(SidPlayerPalette.ControlHover)),
            ("ButtonForegroundPressed", SidPlayerPalette.Brush(SidPlayerPalette.ControlHover)),
        })
            button.Resources[key] = value;
    }

    /// <summary>The menu behind the three lines at the top right.</summary>
    private Button BuildMenu()
    {
        _checkForUpdates.Header = "Check for updates at start";
        _checkForUpdates.ToggleType = MenuItemToggleType.CheckBox;
        _checkForUpdates.IsChecked = _settings.CheckForUpdates == true;
        _checkForUpdates.Click += (_, _) =>
        {
            _settings.CheckForUpdates = _checkForUpdates.IsChecked;
            _settings.Save();
        };

        _showPlaylists.Header = "Show playlists";
        _showPlaylists.ToggleType = MenuItemToggleType.CheckBox;
        _showPlaylists.Click += (_, _) => ShowPlaylists(_showPlaylists.IsChecked);

        _everySong.Header = "Play every song of a tune";
        _everySong.ToggleType = MenuItemToggleType.CheckBox;
        _everySong.IsChecked = _settings.PlayEverySong;
        _everySong.Click += (_, _) =>
        {
            _settings.PlayEverySong = _everySong.IsChecked;
            _settings.Save();
        };

        _smallFont.Header = "Small font";
        _bigFont.Header = "Big font";
        foreach (var (item, big) in new[] { (_smallFont, false), (_bigFont, true) })
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.GroupName = "Font";
            item.Click += (_, _) =>
            {
                _settings.BigFont = big;
                _settings.Save();
                ShowFontSize();
            };
        }

        var menu = new Button
        {
            Content = new PathIcon
            {
                Data = Geometry.Parse("M0,0 H16 V2 H0 Z M0,6 H16 V8 H0 Z M0,12 H16 V14 H0 Z"),
                Width = 16,
                Height = 14,
            },
            Padding = new Thickness(9, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, Items = { _showPlaylists, _everySong, new Separator(), _checkForUpdates, new Separator(), _smallFont, _bigFont } },
        };
        ToolTip.SetTip(menu, "Menu");
        AutomationProperties.SetName(menu, "Menu");
        InPlayerColours(menu);
        return menu;
    }

    /// <summary>The playlist strip shown or hidden, the menu's tick with it - and the choice kept,
    /// unless it is only the start's default.</summary>
    private void ShowPlaylists(bool show, bool remember = true)
    {
        _strip.IsVisible = show;
        _showPlaylists.IsChecked = show;
        if (remember && _settings.ShowPlaylists != show)
        {
            _settings.ShowPlaylists = show;
            _settings.Save();
        }
    }

    /// <summary>How much larger than the small size the text is: 1, or <see cref="BigFont"/>.</summary>
    private double TextScale => _settings.BigFont ? BigFont : 1;

    /// <summary>Every text in the window at the size chosen in the menu - the small one is the size
    /// it always was - and the menu's choice ticked.</summary>
    private void ShowFontSize()
    {
        double scale = TextScale;
        _title.FontSize = 20 * scale;
        _details.FontSize = 11.5 * scale;
        _open.FontSize = 12 * scale;
        _favouriteText.FontSize = 12 * scale;
        _songLabel.FontSize = 11 * scale;
        BuildSongPills();
        _strip.TextScale = scale;
        _elapsed.FontSize = 16 * scale;
        _empty.FontSize = 14 * scale;
        _retroStatus.FontSize = 12 * scale;
        _view.TextScale = scale;
        _piano.TextScale = scale;
        foreach (var scope in _scopes)
        {
            scope.TextScale = scale;
            scope.InvalidateVisual();
        }
        _smallFont.IsChecked = !_settings.BigFont;
        _bigFont.IsChecked = _settings.BigFont;
    }

    /// <summary>The first start asks whether to look for updates - off until the user says yes,
    /// and asked once, either way. Then, when it is on, a newer release is offered.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_settings.CheckForUpdates is null)
        {
            _settings.CheckForUpdates = await MessageWindow.AskAsync(this, "Check for updates?",
                "Should MagPlaySID check for a newer version each time it starts? It asks the website " +
                "which version is the latest, and nothing else.\n\nThis can be changed later in the menu at the top right.");
            _settings.Save();
            _checkForUpdates.IsChecked = _settings.CheckForUpdates == true;
        }

        if (_settings.CheckForUpdates != true || await UpdateCheck.NewerVersionAsync() is not { } newer)
            return;

        if (await MessageWindow.AskAsync(this, "A newer MagPlaySID",
                $"MagPlaySID {UpdateCheck.Describe(newer)} is out - this is {UpdateCheck.Describe(UpdateCheck.Current)}. " +
                "Open the website to download it?", "Open the website", "Not now"))
            await Launcher.LaunchUriAsync(new Uri(UpdateCheck.Website));
    }

    /// <summary>A scope for each voice - three a chip, headed as the score's columns are - made
    /// again only when the number changes; a change of chips unmutes every voice.</summary>
    private void BuildScopes(int voices)
    {
        if (_scopes.Length == voices)
            return;

        Array.Clear(_muted);
        _scopeRow.Children.Clear();
        _scopeRow.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", voices)));
        _scopes = new VoiceScopeView[voices];
        for (int i = 0; i < voices; i++)
        {
            var scope = new VoiceScopeView(i, voices > 3 ? $"SID{i / 3 + 1} V{i % 3 + 1}" : null)
            {
                Margin = new Thickness(i == 0 ? 0 : 5, 0, i == voices - 1 ? 0 : 5, 0),
                TextScale = TextScale,
            };
            scope.MuteToggled += (voice, muted) =>
            {
                _muted[voice] = muted;
                _engine?.SetVoiceMuted(voice, muted);
            };
            Grid.SetColumn(scope, i);
            _scopeRow.Children.Add(scope);
            _scopes[i] = scope;
        }
    }

    private void ShowEmpty()
    {
        bool loaded = _file is not null;
        _empty.IsVisible = !loaded;
        _previous.IsEnabled = _playPause.IsEnabled = _fastForward.IsEnabled = _favourite.IsEnabled = loaded;
        _next.IsEnabled = loaded && _queueTune is not null && !_finished;
        _songRow.IsVisible = loaded && _file!.Songs > 1;
        if (!loaded)
        {
            _title.Text = UpdateCheck.Product;
            _details.Text = "Plays a .sid and shows it as tracker rows";
            _retroStatus.Text = "";
        }
    }

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a .sid",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SID tunes") { Patterns = ["*.sid"] }, FilePickerFileTypes.All],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            PlayFile(path);
    }

    /// <summary>The tunes in folders - and any tunes - dropped on the window, as the folder playlist,
    /// played from the first.</summary>
    private async Task OpenFolderAsync(IReadOnlyList<string> paths)
    {
        var tunes = await Task.Run(() => Playlist.FindTunes(paths));
        string name = paths is [var one] && Directory.Exists(one)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(one)) is { Length: > 0 } folder ? folder : one
            : "Dropped tunes";
        if (tunes.Count == 0)
        {
            await MessageWindow.ShowAsync(this, "No tunes", $"There is no .sid in {name}.");
            return;
        }

        var playlist = _strip.ShowFolder(name, tunes);
        PlayFromList(playlist, playlist.Tunes[0]);
    }

    /// <summary>One tune, on its own - whatever playlist was playing stops there.</summary>
    private void PlayFile(string path)
    {
        _queue = null;
        _queueTune = null;
        _finished = false;
        _strip.ShowPlaying(null, null);
        Load(path);
    }

    /// <summary>A playlist's tune, and the rest after it; one that will not play is passed over.</summary>
    private void PlayFromList(Playlist playlist, PlaylistTune tune)
    {
        for (PlaylistTune? next = tune; next is not null; next = playlist.After(next))
        {
            _queue = playlist;
            _queueTune = next;
            _finished = false;
            _strip.ShowPlaying(playlist, next);
            // Every song of it from the first, or the one it starts on.
            if (Load(next.Path, quiet: true, firstSong: _settings.PlayEverySong ? 1 : null))
                return;
        }

        EndOfPlaylist();
    }

    private void PlayNextInList()
    {
        if (_queue is { } playlist && _queueTune is { } tune && playlist.After(tune) is { } next)
            PlayFromList(playlist, next);
        else
            EndOfPlaylist();
    }

    /// <summary>The last tune played: it stops there, paused, until the user picks another.</summary>
    private void EndOfPlaylist()
    {
        _finished = true;
        if (_engine is { } engine)
        {
            engine.Paused = true;
            _playPause.Glyph = TransportGlyph.Play;
        }
        if (_file is not null)
            ShowEmpty();
    }

    /// <summary>Whether the playlist's tune has played: its whole length once - to where it would
    /// start again - or the tune stopped.</summary>
    private bool TuneEnded(C64PlaybackEngine engine, long at)
    {
        if (engine.Failure is not null)
            return true;
        TimeSpan? end = _length?.Total ?? (_lengthUnknown ? UnmeasuredLength : null);
        return end is { } length && at / (double)C64PlaybackEngine.SampleRate >= Math.Max(1, length.TotalSeconds);
    }

    /// <summary>Plays the tune at <paramref name="path"/> - from <paramref name="firstSong"/>, or the
    /// song it starts on: false, and nothing changed, when it cannot be - said in a message unless
    /// <paramref name="quiet"/>, as a playlist passes it over.</summary>
    private bool Load(string path, bool quiet = false, int? firstSong = null)
    {
        SidFile file;
        try
        {
            file = SidFile.Parse(File.ReadAllBytes(path));
            if (SidTune.Unsupported(file) is { } reason)
                throw new SidPlaybackException(reason);
        }
        catch (Exception ex) when (ex is SidPlaybackException or IOException or UnauthorizedAccessException)
        {
            if (!quiet)
                _ = MessageWindow.ShowAsync(this, "Cannot play this .sid", ex.Message);
            return false;
        }

        _file = file;
        _path = path;
        _song = Math.Clamp(firstSong ?? file.StartSong, 1, Math.Max(1, file.Songs));
        Title = $"{(string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(path) : file.Name)} - {UpdateCheck.Product}";
        _title.Text = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(path) : file.Name;
        BuildSongPills();
        ShowEmpty();
        Restart();
        return true;
    }

    private string DescribeFile()
    {
        var file = _file!;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(file.Author))
            parts.Add(file.Author);
        if (!string.IsNullOrWhiteSpace(file.Released))
            parts.Add(file.Released);
        parts.Add(file.Is8580 ? "8580" : "6581");
        if (file.IsNtsc)
            parts.Add("NTSC");
        if (file.IsRsid)
            parts.Add("RSID");
        if (file.ExtraSids.Count > 0)
            parts.Add($"{1 + file.ExtraSids.Count}SID");
        if (file.UsesCiaTimer(_song))
            parts.Add("CIA timer");
        if (file.Songs > 1)
            parts.Add($"{file.Songs} songs");
        return string.Join("   ·   ", parts);
    }

    private void Restart()
    {
        _spectrum.Clear();
        foreach (var scope in _scopes)
            scope.Clear();
        _piano.Clear();
        Start(_song);
        Transcribe(_song);
    }

    private void Start(int song)
    {
        StopPlayback();
        if (_file is not { } file)
            return;

        try
        {
            _engine = new C64PlaybackEngine(file, song);
            BuildScopes(3 * _engine.Chips);
            for (int voice = 0; voice < _scopes.Length; voice++)
                _engine.SetVoiceMuted(voice, _muted[voice]);
            _output = AudioOutput.Start(_engine, latencyMs: 100);
        }
        catch (Exception ex) when (ex is SidPlaybackException or InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            StopPlayback();
            _ = MessageWindow.ShowAsync(this, "Cannot play this .sid", ex.Message);
            return;
        }

        _playPause.Glyph = TransportGlyph.Pause;
        _fastForward.Lit = false;
        _details.Text = DescribeFile();
        LightSongPills();
    }

    private void StopPlayback()
    {
        var output = _output;
        _output = null;
        _engine = null;
        output?.Stop();
        output?.Dispose();
    }

    /// <summary>Transcribes the tune off the UI thread while it already plays: its first half
    /// minute first, shown at once, then the whole of it to its loop with its length - measured on
    /// the C64 it plays on, so the length is in the time it plays in, and an RSID has one.</summary>
    private void Transcribe(int song)
    {
        _transcribing?.Cancel();
        var cancel = new CancellationTokenSource();
        _transcribing = cancel;
        _transcription = null;
        _length = null;
        _lengthUnknown = false;
        _view.SetScore(null, null);
        _retroStatus.Text = "Transcribing...";
        var file = _file!;

        void Show(Action shown) => Dispatcher.UIThread.Post(() =>
        {
            if (!cancel.IsCancellationRequested && _file == file && _song == song)
                shown();
        });

        Task.Run(() =>
        {
            try
            {
                var opening = Transcription.Opening(file, song, OpeningSeconds, cancel.Token);
                Show(() =>
                {
                    if (_transcription is not null)
                        return;
                    _transcription = opening;
                    _view.SetScore(opening.Score, opening.Timeline);
                    _retroStatus.Text = $"The first {OpeningSeconds} seconds - the whole tune is on its way";
                });

                var (whole, length) = Transcription.Whole(file, song, cancel.Token);
                Show(() =>
                {
                    (_transcription, _length) = (whole, length);
                    _lengthUnknown = length is null;
                    _view.SetScore(whole.Score, whole.Timeline);
                    _retroStatus.Text = whole.Describe();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Show(() => _lengthUnknown = true);
                Show(() => _retroStatus.Text = _transcription is null
                    ? $"No rows to show: {ex.Message}"
                    : $"Only the first {OpeningSeconds} seconds: {ex.Message}");
            }
        }, cancel.Token);
    }

    private void TogglePause()
    {
        if (_engine is null)
            return;

        _engine.Paused = !_engine.Paused;
        _playPause.Glyph = _engine.Paused ? TransportGlyph.Play : TransportGlyph.Pause;
    }

    /// <summary>Back a tune in the playlist, early in this one - past <see cref="PreviousRestarts"/>,
    /// or with no tune before it, this one from its start.</summary>
    private void PreviousTune()
    {
        if (_queue is { } playlist && _queueTune is { } tune && playlist.Before(tune) is { } before
            && Elapsed() < PreviousRestarts)
            PlayFromList(playlist, before);
        else
            Restart();
    }

    /// <summary>On a tune in the playlist - whatever song of this one plays.</summary>
    private void NextTune()
    {
        if (_queue is { } playlist && _queueTune is { } tune && !_finished && playlist.After(tune) is { } next)
            PlayFromList(playlist, next);
    }

    /// <summary>The tune's song <paramref name="song"/>, from its start: nothing when it has no such song.</summary>
    private void PlaySong(int song)
    {
        if (_file is not { } file || song < 1 || song > file.Songs || song == _song)
            return;
        _song = song;
        Restart();
    }

    private void ToggleFastForward()
    {
        if (_engine is not { } engine)
            return;
        engine.Speed = engine.Speed > 1 ? 1 : FastForwardSpeed;
        _fastForward.Lit = engine.Speed > 1;
    }

    /// <summary>The keys without a modifier: 1-9 a song, + and - the next or last song, the arrows
    /// the next or previous tune, F fast-forward.</summary>
    private Action? Shortcut(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => () => PlaySong(key - Key.D0),
        >= Key.NumPad1 and <= Key.NumPad9 => () => PlaySong(key - Key.NumPad0),
        Key.OemPlus or Key.Add => () => PlaySong(_song + 1),
        Key.OemMinus or Key.Subtract => () => PlaySong(_song - 1),
        Key.Left => PreviousTune,
        Key.Right => NextTune,
        Key.F => ToggleFastForward,
        _ => null,
    };

    /// <summary>How far into the song the listener is: the tune's own time, not the device's.</summary>
    private TimeSpan Elapsed() => _engine is { } engine && _output is { } output
        ? TimeSpan.FromSeconds(engine.TuneSampleAt(output.SamplesPlayed) / (double)C64PlaybackEngine.SampleRate)
        : TimeSpan.Zero;

    /// <summary>A pill for each of the tune's songs, at the text's size.</summary>
    private void BuildSongPills()
    {
        _songPills.Children.Clear();
        if (_file is not { Songs: > 1 } file)
            return;
        double scale = TextScale;
        for (int song = 1; song <= file.Songs; song++)
        {
            int chosen = song;
            var pill = new Button
            {
                Content = song.ToString(),
                MinWidth = 30 * scale,
                Height = 28 * scale,
                Padding = new Thickness(10 * scale, 0),
                CornerRadius = new CornerRadius(14 * scale),
                BorderThickness = new Thickness(1),
                FontFamily = Mono,
                FontSize = 13 * scale,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                // Never the keyboard's: Space stays pause, not a click on the last pill clicked.
                Focusable = false,
            };
            pill.Click += (_, _) => PlaySong(chosen);
            ToolTip.SetTip(pill, $"Song {song} of {file.Songs}");
            _songPills.Children.Add(pill);
        }
        LightSongPills();
    }

    /// <summary>The song playing's pill filled in the accent, the others in the panel's colours -
    /// and scrolled to, when there are more than fit.</summary>
    private void LightSongPills()
    {
        var accent = SidPlayerPalette.VoiceColour(1);
        for (int i = 0; i < _songPills.Children.Count; i++)
        {
            var pill = (Button)_songPills.Children[i];
            bool on = i + 1 == _song;
            foreach (var (key, colour) in new (string, Color)[]
            {
                ("ButtonBackground", on ? accent : SidPlayerPalette.PanelBackground),
                ("ButtonBackgroundPointerOver", on ? accent : SidPlayerPalette.WithAlpha(accent, 40)),
                ("ButtonBackgroundPressed", on ? accent : SidPlayerPalette.PanelBackground),
                ("ButtonBorderBrush", on ? accent : SidPlayerPalette.PanelBorder),
                ("ButtonBorderBrushPointerOver", accent),
                ("ButtonBorderBrushPressed", accent),
                ("ButtonForeground", on ? SidPlayerPalette.Background : SidPlayerPalette.Control),
                ("ButtonForegroundPointerOver", on ? SidPlayerPalette.Background : SidPlayerPalette.ControlHover),
                ("ButtonForegroundPressed", on ? SidPlayerPalette.Background : SidPlayerPalette.ControlHover),
            })
                pill.Resources[key] = SidPlayerPalette.Brush(colour);
            pill.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
            if (on)
                pill.BringIntoView();
        }
    }

    private void Refresh()
    {
        if (_engine is not { } engine || _output is not { } output)
            return;

        long at = engine.TuneSampleAt(output.SamplesPlayed);
        if (_queueTune is not null && !_finished && !engine.Paused && TuneEnded(engine, at))
        {
            if (_settings.PlayEverySong && engine.Failure is null && _file is { } file && _song < file.Songs)
                PlaySong(_song + 1);
            else
                PlayNextInList();
            return;
        }

        _spectrum.Update(engine, at);
        foreach (var scope in _scopes)
            scope.Update(engine, at);
        _piano.Update(engine, at, _muted);

        var position = PositionWithinSong(at);
        _elapsed.Text = $"{Clock(position)} - {(_length is { } length ? Clock(length.Total) : "--:--")}";
        if (_transcription is not null)
            _view.SetCycle(engine.CycleAt(position.TotalSeconds * C64PlaybackEngine.SampleRate));
        if (engine.Failure is { } failure && !_details.Text!.StartsWith("The tune stopped", StringComparison.Ordinal))
            _details.Text = $"The tune stopped: {failure}";
    }

    /// <summary>Where the listener is inside the song: past the end it folds back into the loop.</summary>
    private TimeSpan PositionWithinSong(long samples)
    {
        var elapsed = TimeSpan.FromSeconds(samples / (double)C64PlaybackEngine.SampleRate);
        if (_length is not { } length || length.LoopFrames <= 0 || elapsed <= length.Total)
            return elapsed;

        double intoLoop = (elapsed - length.LoopStart).TotalSeconds % length.Loop.TotalSeconds;
        return length.LoopStart + TimeSpan.FromSeconds(intoLoop);
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
}
