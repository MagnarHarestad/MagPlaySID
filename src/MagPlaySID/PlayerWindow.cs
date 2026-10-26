using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MagSid.Editor.App.Controls;
using MagSid.Editor.Avalonia;
using MagSid.Editor.Avalonia.Player;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>
/// MagPlaySID's window: a .sid plays - opened, dropped on the window from Explorer, Finder or the
/// desktop, or named on the command line - with the MagSID player's spectrum and a scope per
/// voice, and below them the tune transcribed into tracker rows that scroll with the music.
/// The sound is always the tune's own 6502 code; the rows are only a view of it.
/// </summary>
internal sealed class PlayerWindow : Window
{
    private static readonly FontFamily Mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    /// <summary>Rows handed to the view this many frames before they sound, so it never waits.</summary>
    private const int LeadFrames = 25;

    private readonly SpectrumView _spectrum = new();
    private readonly VoiceScopeView[] _scopes = [new(0), new(1), new(2)];
    private readonly TransportButton _previous = new(TransportGlyph.Previous);
    private readonly TransportButton _playPause = new(TransportGlyph.Pause);
    private readonly TransportButton _next = new(TransportGlyph.Next);
    private readonly TextBlock _title = new();
    private readonly TextBlock _details = new();
    private readonly TextBlock _elapsed = new();
    private readonly TextBlock _empty = new();
    private readonly TextBlock _retroStatus = new();
    private readonly Button _open = new();
    private readonly TrackerGridControl _grid = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool[] _muted = new bool[3];

    private SidFile? _file;
    private SidFilePlaybackEngine? _engine;
    private IAudioOutput? _output;
    private SidSongLength.Result? _length;
    private int _song;

    private Transcription? _transcription;
    private CancellationTokenSource? _transcribing;
    private int _nextRow;
    private double _lastFrame;
    private long _viewClock;

    public PlayerWindow(string? path = null)
    {
        Title = "MagPlaySID";
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

        _open.Content = "Open .sid...";
        _open.Click += (_, _) => _ = OpenAsync();

        var header = new DockPanel { Margin = new Thickness(18, 12, 18, 6) };
        DockPanel.SetDock(_open, Dock.Right);
        header.Children.Add(_open);
        header.Children.Add(new StackPanel { Children = { _title, _details } });

        var scopes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Height = 110, Margin = new Thickness(18, 6, 18, 4) };
        for (int i = 0; i < 3; i++)
        {
            _scopes[i].Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            _scopes[i].MuteToggled += (voice, muted) =>
            {
                _muted[voice] = muted;
                _engine?.SetVoiceMuted(voice, muted);
            };
            Grid.SetColumn(_scopes[i], i);
            scopes.Children.Add(_scopes[i]);
        }

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

        // The Retro view: read-only - it follows the music and takes no keys or clicks.
        _grid.Width = TrackerGridControl.ContentWidth;
        _grid.IsHitTestVisible = false;
        _grid.Focusable = false;
        _grid.AudioClockSamples = () => _viewClock;
        _retroStatus.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _retroStatus.FontSize = 12;
        _retroStatus.Margin = new Thickness(0, 0, 0, 4);
        var retro = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 4, 18, 12) };
        DockPanel.SetDock(_retroStatus, Dock.Top);
        retro.Children.Add(_retroStatus);
        retro.Children.Add(_grid);

        var root = new DockPanel();
        foreach (var top in new Control[] { header, spectrumHost, scopes, transport })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }
        root.Children.Add(retro);
        return root;
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
            if (file.UnsupportedReason() is { } reason)
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
            _engine = new SidFilePlaybackEngine(file, chipModel: null, song: song);
            for (int voice = 0; voice < 3; voice++)
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

    /// <summary>Measures the tune and transcribes it, off the UI thread, while it already plays;
    /// the view starts following when it is done.</summary>
    private void Transcribe(int song)
    {
        _transcribing?.Cancel();
        var cancel = new CancellationTokenSource();
        _transcribing = cancel;
        _transcription = null;
        _length = null;
        _grid.SetPlaybackActive(false);
        _retroStatus.Text = "Transcribing...";
        var file = _file!;

        Task.Run(() =>
        {
            var length = SidSongLength.Measure(file, song: song);
            double seconds = length is null ? 180 : length.TotalFrames / length.CallsPerSecond;
            int frames = Math.Clamp((int)Math.Ceiling(seconds * 50), 50, 50 * 600);
            return (length, Transcription.Make(file, song, frames, cancel.Token));
        }, cancel.Token).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (cancel.IsCancellationRequested || _file != file || _song != song)
                return;

            if (!task.IsCompletedSuccessfully)
            {
                _retroStatus.Text = task.Exception?.GetBaseException() is { } ex
                    ? $"No rows to show: {ex.Message}"
                    : "No rows to show.";
                return;
            }

            (_length, _transcription) = task.Result;
            var transcription = _transcription;
            _grid.SetSong(transcription.Song);
            _retroStatus.Text = $"Transcribed by MagSID's Import SID - {transcription.Song.Instruments.Count} instruments, " +
                                $"speed {transcription.Song.Speed + 1}{(transcription.Song.HasFunk ? $"/{transcription.Song.FunkSpeed + 1}" : "")}";
            FollowFrom(0);
        }), TaskScheduler.Default);
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

        long at = output.SamplesPlayed;
        _spectrum.Update(engine, at);
        foreach (var scope in _scopes)
            scope.Update(engine, at);

        var position = PositionWithinSong(at);
        _elapsed.Text = $"{Clock(position)} - {(_length is { } length ? Clock(length.Total) : "--:--")}";
        Follow(position.TotalSeconds * 50);
    }

    /// <summary>Hands the view the rows starting up to just past the frame being heard, and sets
    /// its clock there. A step back - the tune looping - starts the view again from there.</summary>
    private void Follow(double frame)
    {
        if (_transcription is not { } transcription)
            return;

        if (frame < _lastFrame - 2)
            FollowFrom(frame);
        _lastFrame = frame;

        var rows = transcription.Rows;
        while (_nextRow < rows.Count && rows[_nextRow].Frame <= frame + LeadFrames)
        {
            var row = rows[_nextRow++];
            _grid.SetChannelPlaybackAnchor(row.Channel, row.Pattern, row.Row, row.OrderEntrySequence, SampleOf(row.Frame));
        }

        _viewClock = SampleOf(frame);
    }

    /// <summary>The view started again at a frame: each voice from the row it is on there.</summary>
    private void FollowFrom(double frame)
    {
        var rows = _transcription!.Rows;
        _grid.SetPlaybackActive(true);
        _lastFrame = frame;

        int first = 0;
        while (first < rows.Count && rows[first].Frame <= frame)
            first++;

        // The row each voice is sounding at that frame, then everything after it.
        for (int channel = 0; channel < 3; channel++)
        {
            for (int i = first - 1; i >= 0; i--)
            {
                if (rows[i].Channel != channel)
                    continue;
                var row = rows[i];
                _grid.SetChannelPlaybackAnchor(row.Channel, row.Pattern, row.Row, row.OrderEntrySequence, SampleOf(row.Frame));
                break;
            }
        }

        _nextRow = first;
    }

    private static long SampleOf(double frame) => (long)(frame * SongPlaybackEngine.SamplesPerFrame);

    /// <summary>Where the listener is inside the song: past the end it folds back into the loop.</summary>
    private TimeSpan PositionWithinSong(long samples)
    {
        var elapsed = TimeSpan.FromSeconds(samples / (double)SidFilePlaybackEngine.SampleRate);
        if (_length is not { } length || length.LoopFrames <= 0 || elapsed <= length.Total)
            return elapsed;

        double intoLoop = (elapsed - length.LoopStart).TotalSeconds % length.Loop.TotalSeconds;
        return length.LoopStart + TimeSpan.FromSeconds(intoLoop);
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
}
