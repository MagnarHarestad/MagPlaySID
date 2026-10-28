using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MagSid.Editor.Avalonia;
using MagPlay.C64;
using MagSid.Editor.Avalonia.Player;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>
/// MagPlaySID's window: a .sid plays - opened, dropped on the window from Explorer, Finder or the
/// desktop, or named on the command line - with the MagSID player's spectrum and a scope per
/// voice, below them the tune transcribed into a score whose rows scroll with the music, and at
/// the bottom a keyboard lit by the notes the voices play.
/// The sound is always the tune's own 6502 code, run on the C64 - any tune, RSIDs and digis too;
/// the score is only a view of it.
/// </summary>
internal sealed class PlayerWindow : Window
{
    private static readonly FontFamily Mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    /// <summary>Seconds of a tune transcribed first, to show while the whole of it is.</summary>
    private const int OpeningSeconds = 30;

    private readonly SpectrumView _spectrum = new();
    private VoiceScopeView[] _scopes = [];
    private readonly Grid _scopeRow = new() { Height = 110, Margin = new Thickness(18, 6, 18, 4) };
    private readonly TransportButton _previous = new(TransportGlyph.Previous);
    private readonly TransportButton _playPause = new(TransportGlyph.Pause);
    private readonly TransportButton _next = new(TransportGlyph.Next);
    private readonly TextBlock _title = new();
    private readonly TextBlock _details = new();
    private readonly TextBlock _elapsed = new();
    private readonly TextBlock _empty = new();
    private readonly TextBlock _retroStatus = new();
    private readonly Button _open = new();
    private readonly ScoreView _view = new();
    private readonly PianoView _piano = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool[] _muted = new bool[9];

    private SidFile? _file;
    private C64PlaybackEngine? _engine;
    private IAudioOutput? _output;
    private SidSongLength.Result? _length;
    private int _song;

    private Transcription? _transcription;
    private CancellationTokenSource? _transcribing;

    public PlayerWindow(string? path = null)
    {
        Title = "MagPlaySID";
        if (typeof(PlayerWindow).Assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) is { } icon)
            Icon = new WindowIcon(typeof(PlayerWindow).Assembly.GetManifestResourceStream(icon)!);
        Width = 1180;
        Height = 900;
        MinWidth = 900;
        MinHeight = 600;
        Background = SidPlayerPalette.Brush(SidPlayerPalette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildLayout();

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

        // A .sid dropped anywhere on the window loads and plays.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.TryGetFile() is not null ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } dropped)
                Load(dropped);
        });

        Closed += (_, _) =>
        {
            _timer.Stop();
            _transcribing?.Cancel();
            StopPlayback();
        };

        ShowEmpty();
        if (path is not null)
            Opened += (_, _) => Load(path);
    }

    private Control BuildLayout()
    {
        _title.FontSize = 20;
        _title.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        _details.FontSize = 11.5;
        _details.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _details.Margin = new Thickness(0, 2, 0, 0);

        // Under the tune's name and details, in the player's own colours rather than the theme's.
        _open.Content = "Open .sid...";
        _open.Click += (_, _) => _ = OpenAsync();
        _open.HorizontalAlignment = HorizontalAlignment.Left;
        _open.Margin = new Thickness(0, 10, 0, 0);
        _open.Padding = new Thickness(14, 5);
        _open.FontSize = 12;
        _open.CornerRadius = new CornerRadius(4);
        _open.BorderThickness = new Thickness(1);
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
            _open.Resources[key] = value;

        var header = new StackPanel { Margin = new Thickness(18, 12, 18, 6), Children = { _title, _details, _open } };

        BuildScopes(3);

        _elapsed.FontFamily = Mono;
        _elapsed.FontSize = 16;
        _elapsed.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Title);
        _elapsed.VerticalAlignment = VerticalAlignment.Center;
        _elapsed.Margin = new Thickness(14, 0);
        _elapsed.Text = "00:00 - --:--";

        _previous.Click += Restart;
        _playPause.Click += TogglePause;
        _next.Click += NextSong;

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(18, 4, 18, 6),
            Children = { _previous, _elapsed, _playPause, _next },
        };

        _empty.Text = "Open a .sid file - or drop one here";
        _empty.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _empty.FontSize = 14;
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
        _retroStatus.FontSize = 12;
        _retroStatus.Margin = new Thickness(0, 0, 0, 4);
        var retro = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 4, 18, 8) };
        DockPanel.SetDock(_retroStatus, Dock.Top);
        retro.Children.Add(_retroStatus);
        retro.Children.Add(_view);

        var root = new DockPanel();
        foreach (var top in new Control[] { header, spectrumHost, _scopeRow, transport })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }
        _piano.Margin = new Thickness(18, 0, 18, 12);
        DockPanel.SetDock(_piano, Dock.Bottom);
        root.Children.Add(_piano);
        root.Children.Add(retro);
        return root;
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
        _previous.IsEnabled = _playPause.IsEnabled = loaded;
        _next.IsEnabled = loaded && _file!.Songs > 1;
        if (!loaded)
        {
            _title.Text = "MagPlaySID";
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
            Load(path);
    }

    private void Load(string path)
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
            _ = MessageWindow.ShowAsync(this, "Cannot play this .sid", ex.Message);
            return;
        }

        _file = file;
        _song = file.StartSong;
        Title = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(path) : $"{file.Name} - MagPlaySID";
        _title.Text = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(path) : file.Name;
        ShowEmpty();
        Restart();
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
            parts.Add($"tune {_song} of {file.Songs}");
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
        _details.Text = DescribeFile();
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
                    _view.SetScore(whole.Score, whole.Timeline);
                    _retroStatus.Text = whole.Describe();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
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

    private void NextSong()
    {
        if (_file is not { Songs: > 1 } file)
            return;

        _song = _song >= file.Songs ? 1 : _song + 1;
        Restart();
    }

    private void Refresh()
    {
        if (_engine is not { } engine || _output is not { } output)
            return;

        long at = engine.TuneSampleAt(output.SamplesPlayed);
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
