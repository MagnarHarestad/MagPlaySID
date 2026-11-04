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
using MagPlaySid.Export;
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

    /// <summary>How many times its speed a tune plays as a fast-forward starts: twice that each
    /// <see cref="FastForwardDoubling"/> seconds it is held, up to what the computer can play.</summary>
    private const int FastForwardSpeed = 4;

    private const double FastForwardDoubling = 1.5;

    /// <summary>How long a playlist's tune plays when its length cannot be measured.</summary>
    private static readonly TimeSpan UnmeasuredLength = TimeSpan.FromMinutes(3);

    /// <summary>The least a playlist gives a song. One that comes to rest on a long note repeats in
    /// half a second, by its memory, with the note still to be heard: it would seem passed over.</summary>
    private const double ShortestSongSeconds = 5;

    private readonly SpectrumView _spectrum = new();
    private readonly LandscapeView _landscape = new();
    private readonly NeonSpectrumView _neon = new();
    private readonly NinjaView _ninja = new();
    private readonly DireView _dire = new();
    private VoiceScopeView[] _scopes = [];
    private readonly Grid _scopeRow = new() { Height = 110, Margin = new Thickness(18, 6, 18, 4) };
    private readonly TransportButton _previous = new(TransportGlyph.Previous);
    private readonly TransportButton _playPause = new(TransportGlyph.Pause);
    private readonly TransportButton _fastForward = new(TransportGlyph.FastForward);
    private readonly TransportButton _next = new(TransportGlyph.Next);
    private readonly TransportButton _repeat = new(TransportGlyph.Repeat);

    /// <summary>The times a tune may be set to play in all; <see cref="Favourites.Forever"/>, on and on.</summary>
    private static readonly (int? Times, string Name)[] RepeatChoices =
    [
        (null, "Play once"), (2, "Twice"), (3, "3 times"), (4, "4 times"), (5, "5 times"), (10, "10 times"),
        (Favourites.Forever, "Forever"),
    ];

    /// <summary>The times the tune playing has played to its end, this time round.</summary>
    private int _played;
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
    private readonly VolumeKnob _volumeKnob = new();

    // The keys' secret: with no tune playing they are a piano's, and each one struck throws up dust.
    private readonly MagicDust _dust = new();
    private readonly PianoSynth _pianoSynth = new();
    private IAudioOutput? _pianoOutput;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool[] _muted = new bool[9];
    private readonly Settings _settings = Settings.Load();
    private readonly MenuItem _checkForUpdates = new();
    private readonly MenuItem _smallFont = new();
    private readonly MenuItem _bigFont = new();
    private readonly MenuItem _spectrumMeter = new() { Header = "Spectrum" };
    private readonly MenuItem _landscapeMeter = new() { Header = "Landscape" };
    private readonly MenuItem _neonMeter = new() { Header = "Neon" };
    private readonly MenuItem _ninjaMeter = new() { Header = "Last Ninja" };
    private readonly MenuItem _direMeter = new() { Header = "Dire Guys" };
    private readonly MenuItem _showPlaylists = new();
    private readonly MenuItem _everySong = new();
    private readonly MenuItem _exportNotes = new();
    private readonly MenuItem _exportSound = new();
    private readonly TextBlock _songLabel = new() { Text = "SONG", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _songPills = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _songScroller = new();
    private readonly Button _songBack = new();
    private readonly Button _songOn = new();
    /// <summary>The songs control beside Add to playlist: shown for a tune of several songs.</summary>
    private readonly Border _songRow = new();
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

    /// <summary>What the tune playing is known by in <see cref="TuneHistory"/>, and whether this
    /// playing of it has been counted.</summary>
    private string? _tuneKey;
    private bool _playCounted;

    /// <summary>Seconds of a tune that count as playing it: skipping past one is not.</summary>
    private const int PlayCountsAfter = 10;
    /// <summary>Fast-forward held - its button or F: on into the next song or tune, until let go.</summary>
    private bool _fastForwarding;
    private readonly System.Diagnostics.Stopwatch _fastForwardHeld = new();
    /// <summary>The tune's sample the meters last showed: they are worked out and painted again only
    /// when it moves on.</summary>
    private long _metersAt = -1;
    /// <summary>The tune's length could not be measured: <see cref="UnmeasuredLength"/> stands in.</summary>
    private bool _lengthUnknown;

    private Transcription? _transcription;
    /// <summary>The whole tune is transcribed, not only its opening: there is a score to export.</summary>
    private bool _wholeTranscribed;
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

        _strip = new PlaylistStrip(this) { PlaylistsHeight = _settings.PlaylistsHeight };
        _strip.Play += PlayFromList;
        _strip.PlaylistsResized += height =>
        {
            _settings.PlaylistsHeight = height;
            _settings.Save();
        };
        _piano.KeyStruck += StrikeKey;
        _volumeKnob.Value = _settings.Volume;
        _pianoSynth.Volume = _volumeKnob.Gain;
        _volumeKnob.Changed += () =>
        {
            if (_engine is { } engine)
                engine.Volume = _volumeKnob.Gain;
            _pianoSynth.Volume = _volumeKnob.Gain;
        };
        _volumeKnob.Committed += () =>
        {
            _settings.Volume = _volumeKnob.Value;
            _settings.Save();
        };
        _strip.ShowRequested += () => ShowPlaylists(true);
        // The dust over everything, taking no clicks.
        Content = new Panel { Children = { BuildLayout(), _dust } };
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
        // Shift too: on some keyboards + is a shifted key. F fast-forwards while it is held.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None)
            {
                FastForward(true);
                e.Handled = true;
            }
            else if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && Shortcut(e.Key) is { } shortcut)
            {
                shortcut();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.F)
                FastForward(false);
        }, RoutingStrategies.Tunnel);
        // A key let go in another window is not heard here: leaving the window lets go.
        Deactivated += (_, _) => FastForward(false);

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
            _settings.LastShown = _strip.ShownName;
            _settings.Save();
            _timer.Stop();
            _transcribing?.Cancel();
            StopPlayback();
            StopPiano();
        };

        ShowEmpty();
        if (path is not null)
            Opened += (_, _) => PlayFile(path);
        else
            Opened += (_, _) => Resume();
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

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _open, _favourite, BuildSongControl() } };
        var details = new StackPanel { Children = { _title, _details, buttons } };
        // The volume and the menu together at the top right, the knob on the menu's left.
        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _volumeKnob, BuildMenu() },
        };
        var header = new Grid { Margin = new Thickness(18, 12, 18, 6), Children = { details, corner } };

        BuildScopes(3);

        _elapsed.FontFamily = Mono;
        _elapsed.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        _elapsed.VerticalAlignment = VerticalAlignment.Center;
        _elapsed.Margin = new Thickness(14, 0);
        _elapsed.Text = "00:00 - --:--";

        _previous.Click += PreviousTune;
        _fastForward.Held += () => FastForward(true);
        _fastForward.LetGo += () => FastForward(false);
        ToolTip.SetTip(_previous, "Previous tune in the playlist - or this one from its start");
        ToolTip.SetTip(_fastForward, "Hold to fast-forward - the longer it is held, the faster (or hold F)");
        ToolTip.SetTip(_next, "Next tune in the playlist");
        _playPause.Click += TogglePause;
        _next.Click += NextTune;
        _repeat.Click += OpenRepeatMenu;
        ToolTip.SetTip(_repeat, "Repeat this tune - a number of times, or forever");

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(18, 4, 18, 6),
            Children = { _previous, _elapsed, _playPause, _fastForward, _repeat, _next },
        };

        _empty.Text = "Open a .sid file - or drop one here";
        _empty.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;

        // The sound's spectrum, in the one of its meters that is chosen; a click goes on to the next.
        var spectrumHost = new Grid
        {
            Height = 150,
            Margin = new Thickness(18, 4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        spectrumHost.Children.Add(_spectrum);
        spectrumHost.Children.Add(_landscape);
        spectrumHost.Children.Add(_neon);
        spectrumHost.Children.Add(_ninja);
        spectrumHost.Children.Add(_dire);
        spectrumHost.Children.Add(_empty);
        spectrumHost.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(spectrumHost).Properties.IsLeftButtonPressed)
                ChooseMeter((MeterKind)(((int)_settings.Meter + 1) % Enum.GetValues<MeterKind>().Length));
        };
        ShowMeter();

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

    /// <summary>The tune's songs - sub-tunes - in one rounded control beside Add to playlist: SONG,
    /// back a song, a pill for each - the one playing lit, a ring round it filling as it plays -
    /// and on a song. About nine pills show at a time; more scroll, by the wheel too.</summary>
    private Border BuildSongControl()
    {
        _songLabel.FontWeight = FontWeight.Bold;
        _songLabel.LetterSpacing = 1.5;
        _songLabel.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _songLabel.Margin = new Thickness(8, 0, 2, 0);

        _songScroller.Content = _songPills;
        _songScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _songScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _songScroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            _songScroller.Offset = _songScroller.Offset.WithX(
                Math.Max(0, _songScroller.Offset.X - (e.Delta.Y + e.Delta.X) * 30 * TextScale));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        foreach (var (button, back) in new[] { (_songBack, true), (_songOn, false) })
        {
            button.Content = new PathIcon
            {
                Data = Geometry.Parse(back ? "M6,0 L7.4,1.4 L2.8,6 L7.4,10.6 L6,12 L0,6 Z" : "M1.4,0 L0,1.4 L4.6,6 L0,10.6 L1.4,12 L7.4,6 Z"),
                Width = 7,
                Height = 11,
            };
            button.Padding = new Thickness(8, 7);
            button.CornerRadius = new CornerRadius(14);
            button.Focusable = false;
            button.VerticalAlignment = VerticalAlignment.Center;
            foreach (var (key, colour) in new (string, Color)[]
            {
                ("ButtonBackground", Colors.Transparent), ("ButtonBackgroundPointerOver", SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 20)),
                ("ButtonBackgroundPressed", SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 30)), ("ButtonBackgroundDisabled", Colors.Transparent),
                ("ButtonForeground", SidPlayerPalette.Control), ("ButtonForegroundPointerOver", SidPlayerPalette.ControlHover),
                ("ButtonForegroundPressed", SidPlayerPalette.ControlHover), ("ButtonForegroundDisabled", SidPlayerPalette.ControlDisabled),
                ("ButtonBorderBrush", Colors.Transparent), ("ButtonBorderBrushPointerOver", Colors.Transparent),
                ("ButtonBorderBrushPressed", Colors.Transparent), ("ButtonBorderBrushDisabled", Colors.Transparent),
            })
                button.Resources[key] = SidPlayerPalette.Brush(colour);
        }
        _songBack.Click += (_, _) => PlaySong(_song - 1);
        _songOn.Click += (_, _) => PlaySong(_song + 1);
        ToolTip.SetTip(_songBack, "Song before (-)");
        ToolTip.SetTip(_songOn, "Next song (+)");

        _songRow.Margin = new Thickness(12, 10, 0, 0);
        _songRow.Padding = new Thickness(2);
        _songRow.CornerRadius = new CornerRadius(18);
        _songRow.BorderThickness = new Thickness(1);
        _songRow.BorderBrush = SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder);
        _songRow.Background = SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground);
        _songRow.VerticalAlignment = VerticalAlignment.Center;
        _songRow.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _songLabel, _songBack, _songScroller, _songOn },
        };
        return _songRow;
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
            // Off, still the player's: dark and dimmed, never the theme's grey.
            ("ButtonBackgroundDisabled", panel), ("ButtonBorderBrushDisabled", border),
            ("ButtonForegroundDisabled", SidPlayerPalette.Brush(SidPlayerPalette.ControlDisabled)),
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

        // The song playing's notes, as the rows read them - once the whole of it is transcribed.
        _exportNotes.Header = "Export notes";
        var asText = new MenuItem { Header = "As text..." };
        asText.Click += (_, _) => _ = ExportNotesAsync(pdf: false);
        var asSheet = new MenuItem { Header = "As sheet music (PDF)..." };
        asSheet.Click += (_, _) => _ = ExportNotesAsync(pdf: true);
        _exportNotes.Items.Add(asText);
        _exportNotes.Items.Add(asSheet);
        _exportNotes.IsEnabled = false;

        // The song playing's sound, once its length is known - or known not to be.
        _exportSound.Header = "Export sound";
        foreach (var format in new[] { SoundFormat.Wav, SoundFormat.Flac, SoundFormat.Mp3 })
        {
            var item = new MenuItem { Header = $"As {SoundExport.Name(format)}..." };
            item.Click += (_, _) => _ = ExportSoundAsync(format);
            _exportSound.Items.Add(item);
        }
        _exportSound.IsEnabled = false;

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

        var openFolder = new MenuItem { Header = "Open folder as playlist..." };
        openFolder.Click += (_, _) => _ = PickFolderAsync();

        var meter = new MenuItem { Header = "Meter" };
        foreach (var (item, kind) in new[] { (_spectrumMeter, MeterKind.Spectrum), (_landscapeMeter, MeterKind.Landscape), (_neonMeter, MeterKind.Neon), (_ninjaMeter, MeterKind.LastNinja), (_direMeter, MeterKind.DireGuys) })
        {
            item.ToggleType = MenuItemToggleType.Radio;
            item.GroupName = "Meter";
            item.Click += (_, _) => ChooseMeter(kind);
            meter.Items.Add(item);
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
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, Items = { openFolder, _showPlaylists, _everySong, new Separator(), _exportNotes, _exportSound, new Separator(), meter, new Separator(), _checkForUpdates, new Separator(), _smallFont, _bigFont } },
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
    private void ChooseMeter(MeterKind kind)
    {
        _settings.Meter = kind;
        _settings.Save();
        ShowMeter();
    }

    /// <summary>The chosen meter, the others hidden: they go on following the sound, unpainted, so
    /// one chosen while the tune plays or stands still shows it as it is.</summary>
    private void ShowMeter()
    {
        _landscape.IsVisible = _landscapeMeter.IsChecked = _settings.Meter == MeterKind.Landscape;
        _neon.IsVisible = _neonMeter.IsChecked = _settings.Meter == MeterKind.Neon;
        _ninja.IsVisible = _ninjaMeter.IsChecked = _settings.Meter == MeterKind.LastNinja;
        _dire.IsVisible = _direMeter.IsChecked = _settings.Meter == MeterKind.DireGuys;
        _spectrum.IsVisible = _spectrumMeter.IsChecked = !_landscape.IsVisible && !_neon.IsVisible && !_ninja.IsVisible && !_dire.IsVisible;
    }

    private void ShowFontSize()
    {
        double scale = TextScale;
        _title.FontSize = 20 * scale;
        _details.FontSize = 11.5 * scale;
        _open.FontSize = 12 * scale;
        _favouriteText.FontSize = 12 * scale;
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
    /// <summary>A scope for each voice - and one more for a digi, when the tune turns out to play
    /// one, put beside the voices' without making them anew, so their muting stays.</summary>
    private void BuildScopes(int voices, bool digi = false)
    {
        int count = voices + (digi ? 1 : 0);
        if (_scopes.Length == count)
            return;

        if (_scopes.Length != voices)
        {
            Array.Clear(_muted);
            _scopeRow.Children.Clear();
            _scopes = new VoiceScopeView[voices];
            for (int i = 0; i < voices; i++)
            {
                var scope = new VoiceScopeView(i, voices > 3 ? $"SID{i / 3 + 1} V{i % 3 + 1}" : null) { TextScale = TextScale };
                scope.MuteToggled += (voice, muted) =>
                {
                    _muted[voice] = muted;
                    _engine?.SetVoiceMuted(voice, muted);
                };
                _scopes[i] = scope;
                _scopeRow.Children.Add(scope);
            }
        }
        if (digi)
        {
            var scope = new VoiceScopeView(voices, "DIGI", SidPlayerPalette.Digi, mutable: false) { TextScale = TextScale };
            _scopes = [.. _scopes, scope];
            _scopeRow.Children.Add(scope);
        }

        _scopeRow.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", count)));
        for (int i = 0; i < count; i++)
        {
            _scopes[i].Margin = new Thickness(i == 0 ? 0 : 5, 0, i == count - 1 ? 0 : 5, 0);
            Grid.SetColumn(_scopes[i], i);
        }
    }

    private void ShowEmpty()
    {
        bool loaded = _file is not null;
        _empty.IsVisible = !loaded;
        _previous.IsEnabled = _playPause.IsEnabled = _fastForward.IsEnabled = _repeat.IsEnabled = loaded;
        // Only with a tune to add: an empty window offers Open alone, not a button that cannot be used.
        _favourite.IsVisible = loaded;
        ShowNextTune();
        _songRow.IsVisible = loaded && _file!.Songs > 1;
        if (!loaded)
        {
            _title.Text = UpdateCheck.Product;
            _details.Text = "Plays a .sid and shows it as tracker rows";
            _retroStatus.Text = "";
        }
    }

    /// <summary>A key struck: while no tune plays - none opened, or paused - a piano's note, the key
    /// glowing, and a puff of magic dust from it. While one plays the keys only show it.</summary>
    private void StrikeKey(int key, Point top)
    {
        if (_engine is { Paused: false })
            return;
        try
        {
            _pianoOutput ??= AudioOutput.Start(_pianoSynth, latencyMs: 40);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            // No sound card to play it on: the glow and the dust still show.
        }
        _pianoSynth.Strike(key);
        _piano.Glow(key);
        if (_piano.TranslatePoint(top, _dust) is { } at)
            _dust.Burst(at);
    }

    private void StopPiano()
    {
        var output = _pianoOutput;
        _pianoOutput = null;
        output?.Stop();
        output?.Dispose();
    }

    /// <summary>The song playing's transcription saved where the user says: its rows as text, or
    /// engraved as sheet music in a PDF. Made off the UI thread - a long tune is many pages.</summary>
    private async Task ExportNotesAsync(bool pdf)
    {
        if (_file is not { } file || _transcription is not { } transcription || !_wholeTranscribed)
            return;
        var sheet = NoteSheet.Of(file, _song, transcription.Score, transcription.Timeline);
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = pdf ? "Export the notes as sheet music" : "Export the notes as text",
            SuggestedFileName = sheet.FileName,
            DefaultExtension = pdf ? "pdf" : "txt",
            FileTypeChoices = [pdf ? new FilePickerFileType("PDF") { Patterns = ["*.pdf"] } : new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
            ShowOverwritePrompt = true,
        });
        if (target is null)
            return;

        try
        {
            await using var stream = await target.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            string product = UpdateCheck.Product;
            await Task.Run(() =>
            {
                if (pdf)
                    SheetMusic.WritePdf(sheet, stream, product);
                else
                {
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                    writer.Write(sheet.ToText(product));
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot export the notes", ex.Message);
        }
    }

    /// <summary>The song playing's sound saved where the user says, as WAV, FLAC or MP3: played
    /// apart from what is heard, as fast as it goes, a window showing how far with a Cancel.</summary>
    private async Task ExportSoundAsync(SoundFormat format)
    {
        if (_file is not { } file || !(_wholeTranscribed || _lengthUnknown))
            return;
        int song = _song;
        var length = _length?.Total ?? UnmeasuredLength;
        bool fadeOut = _length is not { EndsQuiet: true };
        var muted = _muted.ToArray();
        string name = SoundExport.Name(format), extension = SoundExport.Extension(format);
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export the sound as {name}",
            SuggestedFileName = SoundExport.FileName(file, song),
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(name) { Patterns = [$"*.{extension}"] }],
            ShowOverwritePrompt = true,
        });
        if (target is null)
            return;

        using var cancel = new CancellationTokenSource();
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 };
        var status = new TextBlock { Text = $"Playing {Clock(length)} of {SoundExport.FileName(file, song)}...", TextWrapping = TextWrapping.Wrap };
        var stop = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        bool done = false;
        var dialog = new Window
        {
            Title = $"Export the sound as {name}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children = { status, bar, stop } },
        };
        stop.Click += (_, _) => dialog.Close();
        dialog.Closing += (_, _) =>
        {
            if (!done)
                cancel.Cancel();
        };
        _ = dialog.ShowDialog(this);

        string? failure = null;
        try
        {
            var progress = new Progress<double>(part => bar.Value = part);
            var samples = await Task.Run(() => SoundExport.Render(file, song, length, fadeOut, muted, progress, cancel.Token));
            status.Text = $"Writing the {name} file...";
            bar.IsIndeterminate = true;
            stop.IsEnabled = false;
            await using var stream = await target.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await Task.Run(() => SoundExport.Write(stream, format, samples, file, song));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SidPlaybackException or InvalidOperationException or ArgumentException)
        {
            failure = ex.Message;
        }
        finally
        {
            done = true;
            dialog.Close();
        }
        if (failure is not null)
            await MessageWindow.ShowAsync(this, "Cannot export the sound", failure);
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

    private async Task PickFolderAsync()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open a folder of .sid tunes as a playlist",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
                return;
            if (folders[0].TryGetLocalPath() is { } path)
                await OpenFolderAsync([path]);
            else
                await MessageWindow.ShowAsync(this, "Cannot open the folder", $"{folders[0].Name} is not a folder on this computer.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await MessageWindow.ShowAsync(this, "Cannot open the folder", ex.Message);
        }
    }

    /// <summary>The tunes in folders - and any tunes - dropped or chosen, as a playlist kept with
    /// the others, played from the first.</summary>
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

        var playlist = _strip.AddFolder(name, tunes);
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
        return end is { } length && at / (double)C64PlaybackEngine.SampleRate >= Math.Max(ShortestSongSeconds, length.TotalSeconds);
    }

    /// <summary>Plays the tune at <paramref name="path"/> - from <paramref name="firstSong"/>, or the
    /// song the user chose to play first of it, or the song it starts on: false, and nothing changed,
    /// when it cannot be - said in a message unless
    /// <paramref name="quiet"/>, as a playlist passes it over.</summary>
    private bool Load(string path, bool quiet = false, int? firstSong = null)
    {
        SidFile file;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
            file = SidFile.Parse(bytes);
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
        _tuneKey = TuneHistory.KeyOf(bytes);
        _playCounted = false;
        _played = 0;
        _song = firstSong ?? ChosenFirstSong(file, path) ?? OwnFirstSong(file);
        Title = $"{(string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(path) : file.Name)} - {UpdateCheck.Product}";
        _title.Text = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(path) : file.Name;
        BuildSongPills();
        ShowEmpty();
        Restart();
        ShowRepeat();
        return true;
    }

    /// <summary>The song a tune starts on by itself.</summary>
    private static int OwnFirstSong(SidFile file) => Math.Clamp(file.StartSong, 1, Math.Max(1, file.Songs));

    /// <summary>The song the user chose to play first; null when they have not, or the file no
    /// longer has it.</summary>
    private static int? ChosenFirstSong(SidFile file, string path) =>
        Favourites.FirstSong(path) is { } song && song >= 1 && song <= file.Songs ? song : null;

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

    /// <summary>Where the user left off: the tune last played, at the song it was on, paused at
    /// its start - in its playlist, so the tunes after it follow - and the playlist that was shown.
    /// Nothing, when the tune is no longer there.</summary>
    private void Resume()
    {
        if (_settings.LastTune is { } path && File.Exists(path))
        {
            var playlist = _strip.Find(_settings.LastQueue);
            var tune = playlist?.Tunes.FirstOrDefault(t => Playlist.SamePath(t.Path, path));
            if (playlist is not null && tune is not null)
            {
                (_queue, _queueTune, _finished) = (playlist, tune, false);
                _strip.ShowPlaying(playlist, tune);
            }
            if (Load(path, quiet: true, firstSong: _settings.LastSong > 0 ? _settings.LastSong : null))
            {
                if (_engine is { } engine)
                {
                    engine.Paused = true;
                    _playPause.Glyph = TransportGlyph.Play;
                }
            }
            else
                (_queue, _queueTune) = (null, null);
        }
        _strip.Show(_settings.LastShown);
    }

    /// <summary>The tune and song playing kept, to start from next time.</summary>
    private void RememberPlace()
    {
        _settings.LastTune = _path;
        _settings.LastSong = _song;
        _settings.LastQueue = _queue?.Name;
        _settings.Save();
    }

    private void Restart()
    {
        _metersAt = -1;
        _spectrum.Clear();
        _landscape.Clear();
        _neon.Clear();
        _ninja.Clear();
        _dire.Clear();
        foreach (var scope in _scopes)
            scope.Clear();
        _piano.Clear();
        Start(_song);
        Transcribe(_song);
        RememberPlace();
    }

    private void Start(int song)
    {
        StopPlayback();
        StopPiano();
        if (_file is not { } file)
            return;

        try
        {
            _engine = new C64PlaybackEngine(file, song) { Volume = _volumeKnob.Gain };
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
        _engine.Speed = FastForwardingSpeed();
        _fastForward.Lit = _fastForwarding;
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
    /// the C64 it plays on, so the length is in the time it plays in, and an RSID has one. The whole
    /// tune can read the same notes otherwise: its rows come in below what is on screen. On a
    /// thread of its own below the others' priority: it is the background's work, and the sound and
    /// the window come first - on the thread pool it held the sound thread back by up to 40 ms.</summary>
    private void Transcribe(int song)
    {
        _transcribing?.Cancel();
        var cancel = new CancellationTokenSource();
        _transcribing = cancel;
        _transcription = null;
        _wholeTranscribed = false;
        _exportNotes.IsEnabled = false;
        _exportSound.IsEnabled = false;
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

        var thread = new Thread(() =>
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
                    _view.ShowSamples(opening.Samples);
                    _retroStatus.Text = $"The first {OpeningSeconds} seconds - the whole tune is on its way";
                    // At once where the tune has got to, not a frame at its start first.
                    Refresh();
                });

                var (whole, length) = Transcription.Whole(file, song, opening.Rows, cancel.Token);
                Show(() =>
                {
                    (_transcription, _length) = (whole, length);
                    _wholeTranscribed = true;
                    _exportNotes.IsEnabled = true;
                    _exportSound.IsEnabled = true;
                    _lengthUnknown = length is null;
                    if (length is not null && _tuneKey is { } key)
                    {
                        TuneHistory.Measured(key, song, length.Total);
                        _strip.Measured(key);
                    }
                    _view.ContinueWith(whole.Score, whole.Timeline);
                    _view.ShowSamples(whole.Samples);
                    _retroStatus.Text = whole.Describe();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Show(() => _exportSound.IsEnabled = _lengthUnknown = true);
                Show(() => _retroStatus.Text = _transcription is null
                    ? $"No rows to show: {ex.Message}"
                    : $"Only the first {OpeningSeconds} seconds: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Transcription",
        };
        thread.Start();
    }

    private void TogglePause()
    {
        if (_engine is null)
            return;

        _engine.Paused = !_engine.Paused;
        _playPause.Glyph = _engine.Paused ? TransportGlyph.Play : TransportGlyph.Pause;
    }

    /// <summary>Back a tune in the playlist, at one press - or, with no tune before it, this one
    /// from its start.</summary>
    private void PreviousTune()
    {
        if (_queue is { } playlist && _queueTune is { } tune && playlist.Before(tune) is { } before)
            PlayFromList(playlist, before);
        else
            Restart();
    }

    /// <summary>Next tune on only where there is one: off on the playlist's last - wherever a drag
    /// has put it - and with no playlist.</summary>
    private void ShowNextTune() =>
        _next.IsEnabled = _file is not null && !_finished
                          && _queue is { } playlist && _queueTune is { } tune && playlist.After(tune) is not null;

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

    /// <summary>Fast-forward on while it is held, off when it is let go - held on, as a key held
    /// down says so again and again, it keeps the speed it has climbed to.</summary>
    private void FastForward(bool on)
    {
        bool was = _fastForwarding;
        _fastForwarding = on && _file is not null;
        if (_fastForwarding && !was)
            _fastForwardHeld.Restart();
        if (_engine is { } engine)
            engine.Speed = FastForwardingSpeed();
        _fastForward.Lit = _fastForwarding;
        if (!_fastForwarding)
            _fastForward.Badge = null;
    }

    /// <summary>The speed asked of the tune: 1, or the fast-forward's, doubled for each
    /// <see cref="FastForwardDoubling"/> seconds it has been held.</summary>
    private int FastForwardingSpeed()
    {
        if (!_fastForwarding)
            return 1;
        int doublings = Math.Min(8, (int)(_fastForwardHeld.Elapsed.TotalSeconds / FastForwardDoubling));
        return Math.Min(C64PlaybackEngine.MaxSpeed, FastForwardSpeed << doublings);
    }

    /// <summary>The keys without a modifier: 1-9 a song, + and - the next or last song, the arrows
    /// the next or previous tune. F, held, is fast-forward's, beside these.</summary>
    private Action? Shortcut(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => () => PlaySong(key - Key.D0),
        >= Key.NumPad1 and <= Key.NumPad9 => () => PlaySong(key - Key.NumPad0),
        Key.OemPlus or Key.Add => () => PlaySong(_song + 1),
        Key.OemMinus or Key.Subtract => () => PlaySong(_song - 1),
        Key.Left => PreviousTune,
        Key.Right => NextTune,
        _ => null,
    };

    /// <summary>A pill for each of the tune's songs, at the text's size.</summary>
    private void BuildSongPills()
    {
        _songPills.Children.Clear();
        if (_file is not { Songs: > 1 } file)
            return;
        double scale = TextScale;
        _songLabel.FontSize = 10 * scale;
        // About nine pills, then the rest scroll.
        _songScroller.MaxWidth = 9 * (26 + 4) * scale;
        for (int song = 1; song <= file.Songs; song++)
        {
            var pill = new SongPill(song, scale);
            pill.Click += PlaySong;
            pill.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                OpenFirstSongMenu(pill);
            };
            _songPills.Children.Add(pill);
        }
        MarkFirstSong();
        LightSongPills();
    }

    /// <summary>The song's right-click menu: Play first, ticked on the song that does. Taken off
    /// again, the tune starts where it does by itself - which has nothing to take off.</summary>
    private void OpenFirstSongMenu(SongPill pill)
    {
        if (_file is not { } file || _path is not { } path)
            return;
        int own = OwnFirstSong(file);
        int? chosen = ChosenFirstSong(file, path);
        bool first = pill.Song == (chosen ?? own);
        var item = new MenuItem
        {
            Header = "Play first",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = first,
            IsEnabled = !first || chosen is not null,
        };
        item.Click += (_, _) =>
        {
            _strip.ChooseFirstSong(path, first || pill.Song == own ? null : pill.Song);
            MarkFirstSong();
        };
        ToolTip.SetTip(item, "Start this tune on this song whenever it is played");
        var menu = new ContextMenu();
        menu.Items.Add(item);
        menu.Open(pill);
    }

    /// <summary>The song chosen to play first marked, and every pill's tip saying what it is.</summary>
    private void MarkFirstSong()
    {
        if (_file is not { } file || _path is not { } path)
            return;
        int? chosen = ChosenFirstSong(file, path);
        foreach (SongPill pill in _songPills.Children)
        {
            pill.PlaysFirst = pill.Song == chosen;
            string tip = pill.Song <= 9 ? $"Song {pill.Song} of {file.Songs} (key {pill.Song})" : $"Song {pill.Song} of {file.Songs}";
            ToolTip.SetTip(pill, pill.PlaysFirst ? tip + " - plays first" : tip);
        }
    }

    /// <summary>The song playing's pill lit - and scrolled to, when there are more than fit - and
    /// back and on a song only where there is one.</summary>
    private void LightSongPills()
    {
        foreach (SongPill pill in _songPills.Children)
        {
            pill.Lit = pill.Song == _song;
            pill.Progress = 0;
            if (pill.Lit)
                pill.BringIntoView();
        }
        _songBack.IsEnabled = _song > 1;
        _songOn.IsEnabled = _file is { } file && _song < file.Songs;
    }

    private void Refresh()
    {
        if (_engine is not { } engine || _output is not { } output)
            return;

        long at = engine.TuneSampleAt(output.SamplesPlayed);
        ShowNextTune();
        if (engine.PlaysDigi)
            BuildScopes(3 * engine.Chips, digi: true);
        if (_fastForwarding)
        {
            engine.Speed = FastForwardingSpeed();
            _fastForward.Badge = engine.PlayingSpeed > 1 ? $"{engine.PlayingSpeed}x" : null;
        }
        if ((_queueTune is not null || RepeatsAgain()) && !_finished && !engine.Paused && TuneEnded(engine, at))
        {
            if (_settings.PlayEverySong && engine.Failure is null && _file is { } file && _song < file.Songs)
                PlaySong(_song + 1);
            else if (RepeatsAgain() && engine.Failure is null)
                PlayAgain();
            else
                PlayNextInList();
            return;
        }

        // The meters only when the tune has moved and the window can be seen: paused, or minimised,
        // they would only work out and paint the same frame again, sixty times a second.
        if (at != _metersAt && WindowState != WindowState.Minimized)
        {
            _metersAt = at;
            _spectrum.Update(engine, at);
            _landscape.Update(_spectrum.Relative, at);
            _neon.Update(_spectrum.Relative, at);
            _ninja.Update(engine, at);
            _dire.Update(engine, at);
            foreach (var scope in _scopes)
                scope.Update(engine, at);
            _piano.Update(engine, at, _muted);
        }

        var position = PositionWithinSong(at);
        if (!_playCounted && position.TotalSeconds >= PlayCountsAfter && _tuneKey is { } tuneKey)
        {
            _playCounted = true;
            TuneHistory.Played(tuneKey);
        }
        _elapsed.Text = $"{Clock(position)} - {(_length is { } length ? Clock(length.Total) : "--:--")}";
        if (_songPills.Children.ElementAtOrDefault(_song - 1) is SongPill playing)
            playing.Progress = _length is { Total.TotalSeconds: > 0 } measured ? position / measured.Total : 0;
        if (_transcription is not null)
            _view.SetCycle(engine.CycleAt(position.TotalSeconds * C64PlaybackEngine.SampleRate));
        if (engine.Failure is { } failure && !_details.Text!.StartsWith("The tune stopped", StringComparison.Ordinal))
            _details.Text = $"The tune stopped: {failure}";
    }

    // ------------------------------------------------------------------ repeat

    /// <summary>How many times in all the tune playing is to play; <see cref="Favourites.Forever"/>
    /// on and on, null once.</summary>
    private int? RepeatTimes => _path is { } path ? Favourites.Repeat(path) : null;

    /// <summary>Whether the tune is to play again when it ends.</summary>
    private bool RepeatsAgain() => RepeatTimes is { } times && (times == Favourites.Forever || _played + 1 < times);

    /// <summary>The tune from its start again - from its first song, when every song of it plays -
    /// its transcription and length kept, as they are the same.</summary>
    private void PlayAgain()
    {
        _played++;
        if (_settings.PlayEverySong && _file is { Songs: > 1 } && _song != 1)
        {
            PlaySong(1);
        }
        else
        {
            _metersAt = -1;
            _spectrum.Clear();
            _landscape.Clear();
            _neon.Clear();
            _ninja.Clear();
            _dire.Clear();
            foreach (var scope in _scopes)
                scope.Clear();
            _piano.Clear();
            Start(_song);
            RememberPlace();
        }
        ShowRepeat();
    }

    /// <summary>The repeat button lit while the tune repeats, saying which time of how many plays.</summary>
    private void ShowRepeat()
    {
        var times = RepeatTimes;
        _repeat.Lit = times is not null;
        _repeat.Badge = times switch
        {
            null => null,
            Favourites.Forever => "∞",
            { } n => $"{Math.Min(_played + 1, n)}/{n}",
        };
    }

    /// <summary>The choices of how many times the tune plays, the one it has ticked: kept for the
    /// tune, wherever it is played from.</summary>
    private void OpenRepeatMenu()
    {
        if (_path is not { } path)
            return;
        var current = RepeatTimes;
        var menu = new ContextMenu { Placement = PlacementMode.TopEdgeAlignedRight, PlacementTarget = _repeat };
        foreach (var (times, name) in RepeatChoices)
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio, GroupName = "Repeat", IsChecked = times == current };
            item.Click += (_, _) =>
            {
                // Counted from the time playing now: the first of however many.
                _played = 0;
                _strip.ChooseRepeat(path, times);
                ShowRepeat();
            };
            menu.Items.Add(item);
        }
        menu.Open(_repeat);
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
