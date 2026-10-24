using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Playback;
using MagSid.Sid;
using static MagSid.Editor.App.DigiEncoder;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The Digi to SID-osc window, as the Windows editor's: load a .wav, adjust the settings,
/// Audition, then Add to Song. The analysis and the encoding are the shared
/// <see cref="DigiEncoder"/>'s and the graph the shared <see cref="DigiCanvas"/>'s; this is the
/// audition loop around them, with PortAudio for the sound.
/// </summary>
internal sealed class DigiToSidOscWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));

    private readonly EditorSession _session;

    private readonly Button _loadButton = new() { Content = "Load .wav..." };
    private readonly TextBlock _fileLabel = new() { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown _voicedThreshold = Number(0, 100, 30);
    private readonly NumericUpDown _cutoffMin = Number(FilterStep.MinCutoff, FilterStep.MaxCutoff, 0x20);
    private readonly NumericUpDown _cutoffMax = Number(FilterStep.MinCutoff, FilterStep.MaxCutoff, 0xE0);

    /// <summary>How often the carrier retriggers with a fresh instrument, in milliseconds. Too
    /// short and it buzzes as pure noise; too long and formant changes within one segment get
    /// averaged away - the right value depends on the source and is only found by listening.</summary>
    private readonly NumericUpDown _segmentMs = Number(40, 400, 120);
    private readonly ComboBox _carrierWaveform = new() { ItemsSource = DigiEncoder.CarrierWaveforms, SelectedIndex = 0, Width = 250 };
    private readonly Button _reanalyzeButton = new() { Content = "Re-analyze", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _auditionButton = new() { Content = "Audition", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _stopButton = new() { Content = "Stop", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _addToSongButton = new() { Content = "Add to Song", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _summary = new() { Foreground = Dim };

    /// <summary>Shown only while a search runs: rendering hundreds of candidates through the real
    /// chip is real work, and this says how far through it is.</summary>
    private readonly ProgressBar _searchProgress = new() { Minimum = 0, Maximum = 100, Width = 220, Height = 12, IsVisible = false };

    private readonly DigiCanvas _canvas = new();
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
    };

    private float[] _rawSamples = [];
    private int _sampleRate = 44100;
    private AnalysisResult? _analysis;
    private IAudioOutput? _auditionOutput;
    private SongPlaybackEngine? _auditionEngine;
    private readonly DispatcherTimer _playheadTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

    /// <summary>Guards against two searches at once, and lets Stop cancel one in flight.</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>The last search's result and everything it depended on, so Audition straight after
    /// a Load or Re-analyze hands back the same answer instead of searching again. Keyed on the
    /// analysis's identity: Analyze always makes a fresh one.</summary>
    private EncodeResult? _cachedResult;
    private (AnalysisResult Analysis, byte CutoffMin, byte CutoffMax, int SegmentMs, int ForcedIndex)? _cachedKey;

    public DigiToSidOscWindow(EditorSession session)
    {
        _session = session;

        Title = "Digi to SID-osc";
        Width = 1280;
        Height = 640;
        MinWidth = 1000;
        MinHeight = 520;
        Background = new SolidColorBrush(AvColor.FromRgb(0x10, 0x10, 0x20));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _loadButton.Click += async (_, _) => await LoadWavAsync();
        _reanalyzeButton.Click += async (_, _) =>
        {
            Analyze();
            await RenderFullClipAsync();
        };
        _auditionButton.Click += async (_, _) => await AuditionAsync();
        _stopButton.Click += (_, _) => StopAudition();
        _addToSongButton.Click += async (_, _) => await AddToSongAsync();
        _canvas.WheelZoom += OnWheelZoom;
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
        Closed += (_, _) => StopAudition();

        _scroll.Content = _canvas;
        _scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                _canvas.SetView((int)_scroll.Viewport.Width, (int)_scroll.Viewport.Height);
        };

        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 6, 8, 0),
            Children = { _loadButton, _fileLabel },
        };
        var row2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(8, 6, 8, 0),
            Children =
            {
                Labelled("Voiced threshold %", _voicedThreshold),
                Labelled("Cutoff min", _cutoffMin),
                Labelled("Cutoff max", _cutoffMax),
                Labelled("Segment ms", _segmentMs),
                Labelled("Carrier waveform", _carrierWaveform),
                _reanalyzeButton,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom, Children = { _auditionButton, _stopButton } },
                _addToSongButton,
            },
        };
        var summaryRow = new StackPanel { Spacing = 4, Margin = new Thickness(8, 6), Children = { _summary, _searchProgress } };

        var root = new DockPanel();
        foreach (var top in new Control[] { row1, row2, summaryRow })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }
        root.Children.Add(_scroll);
        Content = root;

        UpdateSummary();
    }

    private static NumericUpDown Number(int min, int max, int value) =>
        new() { Minimum = min, Maximum = max, Value = value, Width = 110, FormatString = "0" };

    private static Control Labelled(string caption, Control field) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = caption, Foreground = Dim }, field },
    };

    // ---------------------------------------------------------------- wav loading + analysis

    private static readonly FilePickerFileType WavFiles = new("WAV files") { Patterns = ["*.wav"] };

    private async Task LoadWavAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load .wav",
            FileTypeFilter = [WavFiles],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        try
        {
            (_rawSamples, _sampleRate) = WavFile.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            await MessageWindow.ShowAsync(this, "Cannot read the .wav", ex.Message);
            return;
        }

        _fileLabel.Text = Path.GetFileName(path);
        Analyze();
        await RenderFullClipAsync();
    }

    private void Analyze()
    {
        _analysis = DigiEncoder.Analyze(_rawSamples, _sampleRate, (float)(_voicedThreshold.Value ?? 30) / 100f);
        _canvas.ShowAnalysis(_analysis, _rawSamples, _sampleRate);
        _scroll.Offset = new Vector(0, 0);
        UpdateSummary();
    }

    private void UpdateSummary() => _summary.Text = DigiEncoder.Summary(_analysis);

    // ---------------------------------------------------------------- encoding

    /// <summary>Reads the settings here, on the UI thread, then - unless the last search already
    /// answers this exact question - runs DigiEncoder.BuildInstruments on a background thread,
    /// so the window stays alive through a longer search.</summary>
    private async Task<EncodeResult> BuildInstrumentsAsync()
    {
        var a = _analysis ?? throw new InvalidOperationException("No analysis to encode.");

        byte cutoffMinV = (byte)(_cutoffMin.Value ?? 0x20);
        byte cutoffMaxV = (byte)(_cutoffMax.Value ?? 0xE0);
        int segmentMs = (int)(_segmentMs.Value ?? 120);
        int forcedIndex = Math.Max(0, _carrierWaveform.SelectedIndex);

        var key = (a, cutoffMinV, cutoffMaxV, segmentMs, forcedIndex);
        if (_cachedResult is not null && _cachedKey == key)
            return _cachedResult;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var progress = new Progress<int>(percent =>
        {
            _summary.Text = "Searching candidates against the SID chip...";
            _searchProgress.Value = percent;
        });

        string? previousSummary = _summary.Text;
        Cursor = new Cursor(StandardCursorType.Wait);
        SetSearchControlsEnabled(false);
        _searchProgress.Value = 0;
        _searchProgress.IsVisible = true;
        try
        {
            var result = await Task.Run(
                () => DigiEncoder.BuildInstruments(a, cutoffMinV, cutoffMaxV, segmentMs, forcedIndex, progress, cts.Token),
                cts.Token);
            _cachedResult = result;
            _cachedKey = key;
            return result;
        }
        finally
        {
            Cursor = Cursor.Default;
            SetSearchControlsEnabled(true);
            _searchProgress.IsVisible = false;
            _summary.Text = previousSummary;
            if (_searchCts == cts)
                _searchCts = null;
        }
    }

    private void SetSearchControlsEnabled(bool enabled)
    {
        _loadButton.IsEnabled = enabled;
        _reanalyzeButton.IsEnabled = enabled;
        _auditionButton.IsEnabled = enabled;
        _addToSongButton.IsEnabled = enabled;
    }

    // ---------------------------------------------------------------- audition

    private async Task AuditionAsync()
    {
        if (_analysis is null)
            return;
        StopAudition();

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search
        }
        DigiEncoder.NumberForPreview(result);

        var song = DigiEncoder.BuildPreviewSong(result, _analysis.FrameCount);
        try
        {
            _auditionEngine = new SongPlaybackEngine(song, ChipModel.Mos8580);
            _auditionOutput = AudioOutput.Open!(_auditionEngine, 150);
            _auditionOutput.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ArgumentException)
        {
            StopAudition();
            await MessageWindow.ShowAsync(this, "Cannot play", ex.Message);
            return;
        }

        _canvas.PlayheadFrame = 0f;
        _playheadTimer.Start();
    }

    /// <summary>Renders the whole clip through the real reSID port, so the graph shows what the
    /// chip will output next to the source - the same search Audition and Add to Song run.</summary>
    private async Task RenderFullClipAsync()
    {
        if (_analysis is null || _analysis.FrameCount == 0)
        {
            _canvas.RenderedAudio = [];
            return;
        }

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search
        }
        _canvas.RenderedAudio = DigiEncoder.RenderClip(result, _analysis.FrameCount);
    }

    private void StopAudition()
    {
        _searchCts?.Cancel(); // Stop also cancels an in-flight search, not just playback

        var output = _auditionOutput;
        _auditionOutput = null;
        _auditionEngine = null;
        output?.Stop();
        output?.Dispose();

        _playheadTimer.Stop();
        _canvas.PlayheadFrame = null;
    }

    /// <summary>The frame being heard - the device's play position - and the graph follows it
    /// once zoomed in far enough that the playhead would leave the view.</summary>
    private void UpdatePlayhead()
    {
        if (_auditionOutput is null || _analysis is null)
            return;

        float frame = (float)(_auditionOutput.SamplesPlayed / (double)SongPlaybackEngine.SampleRate * DigiEncoder.FrameRate);
        if (frame >= _analysis.FrameCount + 8)
        {
            StopAudition();
            return;
        }

        _canvas.PlayheadFrame = frame;

        double playheadX = _canvas.XOfFrame(frame);
        double scrollX = _scroll.Offset.X;
        double visible = _scroll.Viewport.Width;
        if (playheadX > scrollX + visible - 40)
        {
            double target = Math.Max(0, Math.Min(playheadX - visible + 60, Math.Max(0, _canvas.ContentWidth - visible)));
            _scroll.Offset = new Vector(target, 0);
        }
    }

    // ---------------------------------------------------------------- add to song

    private async Task AddToSongAsync()
    {
        if (_analysis is null)
            return;

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search
        }

        var added = DigiEncoder.AddToSong(_session.Song, result, _analysis.FrameCount);
        _session.NotifyStructureChanged();
        await MessageWindow.ShowAsync(this, "Digi to SID-osc", added.Message);
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>Mouse-wheel zoom, keeping the frame under the pointer where it is.</summary>
    private void OnWheelZoom(int x, int delta)
    {
        if (_analysis is null || _analysis.FrameCount == 0)
            return;

        double pointer = x - _scroll.Offset.X;
        float frameUnderMouse = _canvas.FrameAt(x);
        _canvas.Zoom(delta > 0 ? 1.2f : 1f / 1.2f);

        double target = _canvas.XOfFrame(frameUnderMouse) - pointer;
        target = Math.Max(0, Math.Min(target, Math.Max(0, _canvas.ContentWidth - _scroll.Viewport.Width)));
        Dispatcher.UIThread.Post(() => _scroll.Offset = new Vector(target, 0), DispatcherPriority.Render);
    }
}
