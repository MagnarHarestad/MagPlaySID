using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MagSid.Playback;

namespace MagSid.Editor.Avalonia.Player;

/// <summary>
/// Plays a <c>.sid</c> file and shows what it is doing - one spectrum across the audible range and
/// one scope per SID voice - by running the tune's own 6502 code. The Windows editor's player,
/// rebuilt in Avalonia so it runs on a Mac and on Linux as well; for now it is also the whole app,
/// with a file to open rather than one handed over by the editor.
/// </summary>
public sealed class SidPlayerWindow : Window
{
    private static readonly FontFamily Mono = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly SpectrumView _spectrum = new();
    private readonly VoiceScopeView[] _scopes = [new(0), new(1), new(2)];
    private readonly TransportButton _previous = new(TransportGlyph.Previous);
    private readonly TransportButton _playPause = new(TransportGlyph.Pause);
    private readonly TransportButton _next = new(TransportGlyph.Next);

    private readonly TextBlock _title = new();
    private readonly TextBlock _details = new();
    private readonly TextBlock _elapsed = new();
    private readonly TextBlock _empty = new();
    private readonly Button _open = new();
    private readonly Button _export = new();
    private readonly TextBox _exportLength = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool[] _muted = new bool[3];

    private string? _path;
    private SidFile? _file;
    private SidFilePlaybackEngine? _engine;
    private IAudioOutput? _output;
    private SidSongLength.Result? _length;
    private int _song;
    /// <summary>What the length field was last filled with by the window rather than typed - a
    /// measured length replaces it only while it still says that.</summary>
    private string _exportLengthSet = "";
    private bool _exporting;

    public SidPlayerWindow(string? path = null)
    {
        Title = "MagSID";
        Width = 880;
        Height = 560;
        MinWidth = 640;
        MinHeight = 460;
        Background = SidPlayerPalette.Brush(SidPlayerPalette.Background);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildLayout();

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && !_exportLength.IsFocused)
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

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        Closed += (_, _) =>
        {
            _timer.Stop();
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
        _export.Content = "Export audio";
        _export.Width = 126;
        _export.Click += (_, _) => _ = ExportAsync();
        _exportLength.Width = 88;
        _exportLength.FontFamily = Mono;
        _exportLength.TextAlignment = TextAlignment.Center;
        SetExportLength(TimeSpan.FromMinutes(3));
        ToolTip.SetTip(_exportLength, "How much audio to write - mm:ss:cc, as 02:35:01");
        ToolTip.SetTip(_export, "Render this tune to a WAV file");

        var titleBlock = new StackPanel { Children = { _title, _details } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { _open, _export, _exportLength },
        };
        var header = new DockPanel { Margin = new Thickness(18, 12, 18, 6) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(titleBlock);

        var scopes = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Height = 124,
            Margin = new Thickness(18, 8, 18, 4),
        };
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
            Margin = new Thickness(18, 6, 18, 10),
            Children = { _previous, _elapsed, _playPause, _next },
        };

        _empty.Text = "Open a .sid file - or drop one here";
        _empty.Foreground = SidPlayerPalette.Brush(SidPlayerPalette.Subtitle);
        _empty.FontSize = 14;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;

        var spectrumHost = new Grid { Margin = new Thickness(18, 4) };
        spectrumHost.Children.Add(_spectrum);
        spectrumHost.Children.Add(_empty);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(transport, Dock.Bottom);
        DockPanel.SetDock(scopes, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(transport);
        root.Children.Add(scopes);
        root.Children.Add(spectrumHost);
        return root;
    }

    private void SetExportLength(TimeSpan length)
    {
        _exportLengthSet = SidAudioExport.FormatLength(length);
        _exportLength.Text = _exportLengthSet;
    }

    private void ShowEmpty()
    {
        bool loaded = _file is not null;
        _empty.IsVisible = !loaded;
        _export.IsEnabled = _exportLength.IsEnabled = loaded;
        _previous.IsEnabled = _playPause.IsEnabled = loaded;
        _next.IsEnabled = loaded && _file!.Songs > 1;
        if (!loaded)
        {
            _title.Text = "MagSID";
            _details.Text = "SID player";
        }
    }

    private async Task OpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load and play .sid",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SID tunes") { Patterns = ["*.sid"] }, FilePickerFileTypes.All],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Load(path);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path)
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

        _path = path;
        _file = file;
        _song = file.StartSong;
        _length = null;
        SetExportLength(TimeSpan.FromMinutes(3));

        Title = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(path) : file.Name;
        _title.Text = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(path) : file.Name;
        ShowEmpty();
        Restart();

        // Finding the length means running the whole tune - off the UI thread.
        Task.Run(() => SidSongLength.Measure(file)).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_file != file)
                    return;

                _length = task.Result;
                if (_length is { } found && _exportLength.Text == _exportLengthSet)
                    SetExportLength(found.Total);
            });
        }, TaskScheduler.Default);
    }

    private string DescribeFile()
    {
        var file = _file!;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(file.Author))
            parts.Add(file.Author);
        if (!string.IsNullOrWhiteSpace(file.Released))
            parts.Add(file.Released);
        parts.Add($"${file.LoadAddress:X4}-${file.LoadAddress + file.Data.Length - 1:X4}");
        parts.Add($"{file.Data.Length} bytes");
        parts.Add(file.Is8580 ? "8580" : "6581");
        if (file.UsesCiaTimer(_song))
            parts.Add("CIA timer");
        if (file.Songs > 1)
            parts.Add($"tune {_song} of {file.Songs}");
        return string.Join("   ·   ", parts);
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

    private void Restart()
    {
        _spectrum.Clear();
        foreach (var scope in _scopes)
            scope.Clear();
        Start(_song);
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

        _elapsed.Text = $"{Clock(PositionWithinSong(at))} - {(_length is { } length ? Clock(length.Total) : "--:--")}";
    }

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

    /// <summary>Renders the tune to a file, faster than real time, for as long as the field says.</summary>
    private async Task ExportAsync()
    {
        if (_exporting || _file is not { } file)
            return;

        if (SidAudioExport.ParseLength(_exportLength.Text ?? "") is not { } duration)
        {
            await MessageWindow.ShowAsync(this, "Export audio",
                $"\"{_exportLength.Text}\" is not a length. Write it as minutes, seconds and hundredths - "
                + "02:35:01 - or leave the hundredths off, or give plain seconds.");
            return;
        }

        var formats = SidAudioExport.AvailableFormats();
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export audio",
            SuggestedFileName = (string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(_path) : file.Name.Trim()) + ".wav",
            DefaultExtension = "wav",
            FileTypeChoices = formats.Select(format => new FilePickerFileType($"{format.ToString().ToUpperInvariant()} audio")
            {
                Patterns = [$"*{SidAudioExport.ExtensionOf(format)}"],
            }).ToList(),
        });

        if (target?.TryGetLocalPath() is not { } path)
            return;

        _exporting = true;
        _export.Content = "Rendering...";
        _export.IsEnabled = false;
        try
        {
            var format = SidAudioExport.FormatOf(path);
            await Task.Run(() => SidAudioExport.Render(file, path, format, duration));
        }
        catch (Exception ex)
        {
            await MessageWindow.ShowAsync(this, "Cannot export audio", ex.GetBaseException().Message);
        }
        finally
        {
            _exporting = false;
            _export.Content = "Export audio";
            _export.IsEnabled = true;
        }
    }
}
