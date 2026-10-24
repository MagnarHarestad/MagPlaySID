using MagSid.Core.Model;
using MagSid.Playback;
using MagSid.Sid;
using NAudio.Wave;
using static MagSid.Editor.App.DigiEncoder;

namespace MagSid.Editor.App;

/// <summary>
/// The Digi to SID-osc window: load a .wav, adjust the settings, Audition, then Add to Song. The
/// analysis and the encoding - a modulator instrument plus a small library of short ring-modulated
/// carrier instruments, picked per segment by rendering candidates through the real chip - are
/// <see cref="DigiEncoder"/>'s, the graph <see cref="DigiCanvas"/>'s; this is the audition loop
/// around them.
/// </summary>
public sealed class DigiToSidOscForm : Form
{
    private readonly EditorSession _session;

    private readonly Button _loadButton = new()
    {
        Text = "Load .wav...",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 110,
        Height = 24,
    };

    private readonly Label _fileLabel = new()
    {
        AutoSize = true,
        ForeColor = EditorPalette.Subtitle,
        Margin = new Padding(8, 22, 0, 0),
    };

    private readonly NumericUpDown _voicedThreshold = new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = 30,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly NumericUpDown _cutoffMin = new()
    {
        Minimum = FilterStep.MinCutoff,
        Maximum = FilterStep.MaxCutoff,
        Value = 0x20,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly NumericUpDown _cutoffMax = new()
    {
        Minimum = FilterStep.MinCutoff,
        Maximum = FilterStep.MaxCutoff,
        Value = 0xE0,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    /// <summary>How often the carrier retriggers with a fresh instrument, in milliseconds - the
    /// real GoatTracker speech tricks this tool is modeled on retrigger every couple of rows
    /// (roughly 100-150ms at their tempo), not once per whole clip: each retrigger is what gives
    /// a "syllable" its own attack transient and lets a different formant-shaped instrument take
    /// over. Too short and it buzzes as pure noise; too long and formant changes within one
    /// segment get averaged away - a slider because the right value depends on the source and is
    /// only found by listening.</summary>
    private readonly NumericUpDown _segmentMs = new()
    {
        Minimum = 40,
        Maximum = 400,
        Value = 120,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly ComboBox _carrierWaveform = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 230,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
    };

    private readonly Button _reanalyzeButton = new()
    {
        Text = "Re-analyze",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 90,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Button _auditionButton = new()
    {
        Text = "Audition",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 90,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Button _stopButton = new()
    {
        Text = "Stop",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 70,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Button _addToSongButton = new()
    {
        Text = "Add to Song",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 100,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Label _summary = new()
    {
        AutoSize = true,
        ForeColor = EditorPalette.Subtitle,
        Padding = new Padding(0, 8, 0, 0),
    };

    /// <summary>Only visible while a search is actually running (see BuildInstrumentsAsync) -
    /// rendering hundreds to thousands of candidates through the real chip is real work, and this
    /// is what tells the user how far through it the app is, rather than just a spinning
    /// wait cursor with no sense of whether it's stuck.</summary>
    private readonly ProgressBar _searchProgress = new()
    {
        Minimum = 0,
        Maximum = 100,
        Width = 220,
        Height = 16,
        Visible = false,
        Margin = new Padding(12, 10, 0, 0),
    };

    /// <summary>Scrolls the graph sideways; the graph is as wide as the clip at its zoom.</summary>
    private readonly Panel _host = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        BackColor = EditorPalette.Background,
    };

    private readonly DigiCanvas _canvas = new() { Location = Point.Empty };

    private AnalysisResult? _analysis;
    private WaveOutEvent? _auditionOutput;
    private SongPlaybackEngine? _auditionEngine;

    // Polls SongPlaybackEngine.SamplesProduced - a monotonic count driven by the same Read()
    // calls that produce the actual sound (see that property's own doc comment on why this is
    // safer than a wall-clock timestamp) - and turns it into a frame position for the playhead.
    private readonly System.Windows.Forms.Timer _playheadTimer = new() { Interval = 30 };

    public DigiToSidOscForm(EditorSession session)
    {
        _session = session;

        Text = "Digi to SID-osc";
        StartPosition = FormStartPosition.CenterScreen;
        DarkTheme.ApplyTitleBar(this);
        ClientSize = new Size(1280, 640);
        // The settings row is a non-wrapping FlowLayoutPanel, so anything narrower than its
        // own content simply hides the buttons at the right-hand end - Add to Song first.
        MinimumSize = new Size(1000, 520);
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        _carrierWaveform.Items.AddRange(DigiEncoder.CarrierWaveforms);
        _carrierWaveform.SelectedIndex = 0;

        _loadButton.Click += async (_, _) => await LoadWavAsync();
        _reanalyzeButton.Click += async (_, _) => { Analyze(); await RenderFullClipAsync(); };
        _auditionButton.Click += async (_, _) => await AuditionAsync();
        _stopButton.Click += (_, _) => StopAudition();
        _addToSongButton.Click += async (_, _) => await AddToSongAsync();
        _canvas.WheelZoom += OnWheelZoom;
        _host.ClientSizeChanged += (_, _) => _canvas.SetView(_host.ClientSize.Width, _host.ClientSize.Height);
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
        FormClosed += (_, _) => StopAudition();

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 0, 0),
            BackColor = EditorPalette.Background,
        };
        row1.Controls.Add(_loadButton);
        row1.Controls.Add(_fileLabel);

        // Height generous on purpose: each Labelled() field stacks a caption above the control -
        // a row sized just to the control's own height clips the caption. Same trap as the old
        // InstrumentEditorForm dialog's GroupBox rows (removed 2026-08-31) and the first cut of
        // SignalPathForm's toolbar.
        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 62,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 0, 0),
            BackColor = EditorPalette.Background,
        };
        row2.Controls.Add(Labelled("Voiced threshold %", _voicedThreshold));
        row2.Controls.Add(Labelled("Cutoff min", _cutoffMin));
        row2.Controls.Add(Labelled("Cutoff max", _cutoffMax));
        row2.Controls.Add(Labelled("Segment ms", _segmentMs));
        row2.Controls.Add(Labelled("Carrier waveform", _carrierWaveform));
        row2.Controls.Add(_reanalyzeButton);
        row2.Controls.Add(new Panel { Width = 16, Height = 1 });
        row2.Controls.Add(_auditionButton);
        row2.Controls.Add(_stopButton);
        row2.Controls.Add(new Panel { Width = 16, Height = 1 });
        row2.Controls.Add(_addToSongButton);

        var summaryRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            BackColor = EditorPalette.Background,
        };
        summaryRow.Controls.Add(_summary);
        summaryRow.Controls.Add(_searchProgress);

        _host.Controls.Add(_canvas);
        Controls.Add(_host);
        Controls.Add(summaryRow);
        Controls.Add(row2);
        Controls.Add(row1);

        UpdateSummary();
    }

    private static Control Labelled(string caption, Control field)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Margin = new Padding(0, 0, 16, 0),
        };
        panel.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = EditorPalette.Subtitle });
        panel.Controls.Add(field);
        return panel;
    }

    // ---------------------------------------------------------------- wav loading + analysis

    private async Task LoadWavAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "WAV files (*.wav)|*.wav" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _fileLabel.Text = Path.GetFileName(dialog.FileName);
        var (samples, sampleRate) = ReadWav(dialog.FileName);
        _rawSamples = samples;
        _sampleRate = sampleRate;
        Analyze();
        await RenderFullClipAsync();
    }

    private float[] _rawSamples = [];
    private int _sampleRate = 44100;

    private static (float[] Samples, int SampleRate) ReadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;

        var all = new List<float>();
        var chunk = new float[sampleRate * Math.Max(1, channels)];
        int read;
        while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                all.Add(chunk[i]);
            }
        }

        if (channels <= 1)
        {
            return (all.ToArray(), sampleRate);
        }

        var mono = new float[all.Count / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += all[i * channels + c];
            }
            mono[i] = (float)(sum / channels);
        }
        return (mono, sampleRate);
    }

    private void Analyze()
    {
        _analysis = DigiEncoder.Analyze(_rawSamples, _sampleRate, (float)_voicedThreshold.Value / 100f);
        _canvas.ShowAnalysis(_analysis, _rawSamples, _sampleRate);
        _host.AutoScrollPosition = new Point(0, 0);
        UpdateSummary();
    }

    private void UpdateSummary() => _summary.Text = DigiEncoder.Summary(_analysis);

    // ---------------------------------------------------------------- encoding

    /// <summary>Guards against two searches running at once (e.g. Audition clicked again before
    /// the last one finished) and lets Stop cancel an in-flight one instead of only stopping
    /// playback.</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>The result of the last completed search, plus every input it depended on - so a
    /// second call with nothing actually changed (Audition right after Load/Re-analyze already
    /// ran one, for instance) can hand back the same answer instead of rendering every candidate
    /// against the chip all over again. Keyed on the AnalysisResult's own identity rather than a
    /// deep comparison: Analyze() always allocates a fresh one, so a reference match already
    /// means nothing about the source or the segmentation changed.</summary>
    private EncodeResult? _cachedResult;
    private (AnalysisResult Analysis, byte CutoffMin, byte CutoffMax, int SegmentMs, int ForcedIndex)? _cachedKey;

    /// <summary>
    /// Reads everything the search needs off the UI thread (NumericUpDown/ComboBox values can
    /// only be touched there), then - unless <see cref="_cachedResult"/> already answers this
    /// exact question - hands the actual work off to a background thread via Task.Run, so the UI
    /// keeps pumping messages throughout instead of Windows marking the window "not responding"
    /// partway through a longer search. DigiEncoder.BuildInstruments is where all of that work
    /// actually happens, and it touches nothing but plain data for exactly this reason.
    /// </summary>
    private async Task<EncodeResult> BuildInstrumentsAsync()
    {
        var a = _analysis ?? throw new InvalidOperationException("No analysis to encode.");

        byte cutoffMinV = (byte)_cutoffMin.Value;
        byte cutoffMaxV = (byte)_cutoffMax.Value;
        int segmentMs = (int)_segmentMs.Value;
        int forcedIndex = _carrierWaveform.SelectedIndex;

        var key = (a, cutoffMinV, cutoffMaxV, segmentMs, forcedIndex);
        if (_cachedResult is not null && _cachedKey == key)
        {
            return _cachedResult;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var progress = new Progress<int>(percent =>
        {
            _summary.Text = "Searching candidates against the SID chip...";
            _searchProgress.Value = percent;
        });

        string previousSummary = _summary.Text;
        UseWaitCursor = true;
        SetSearchControlsEnabled(false);
        _searchProgress.Value = 0;
        _searchProgress.Visible = true;
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
            UseWaitCursor = false;
            SetSearchControlsEnabled(true);
            _searchProgress.Visible = false;
            _summary.Text = previousSummary;
            if (_searchCts == cts)
            {
                _searchCts = null;
            }
        }
    }

    private void SetSearchControlsEnabled(bool enabled)
    {
        _loadButton.Enabled = enabled;
        _reanalyzeButton.Enabled = enabled;
        _auditionButton.Enabled = enabled;
        _addToSongButton.Enabled = enabled;
    }

    // ---------------------------------------------------------------- audition

    private async Task AuditionAsync()
    {
        if (_analysis is null)
        {
            return;
        }
        StopAudition();

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search (Re-analyze, another Audition click, ...)
        }
        DigiEncoder.NumberForPreview(result);

        var song = DigiEncoder.BuildPreviewSong(result, _analysis.FrameCount);

        _auditionEngine = new SongPlaybackEngine(song, ChipModel.Mos8580);
        _auditionOutput = new WaveOutEvent { DesiredLatency = 150 };
        _auditionOutput.Init(_auditionEngine);
        _auditionOutput.Play();

        _canvas.PlayheadFrame = 0f;
        _playheadTimer.Start();
    }

    /// <summary>Renders the whole clip through the real reSID port - not Audition's own live
    /// engine, a throwaway one, so scrubbing the graph never needs the sound device to be
    /// playing - purely so the graph can show what the chip will actually output next to the
    /// source waveform. Runs the same brute-force search Audition/Add to Song do, so this is
    /// exactly the analysis-quality preview for whatever the current sliders would produce, not
    /// a cheaper approximation of it.</summary>
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

        _auditionOutput?.Stop();
        _auditionOutput?.Dispose();
        _auditionOutput = null;
        _auditionEngine = null;

        _playheadTimer.Stop();
        _canvas.PlayheadFrame = null;
    }

    /// <summary>Converts the engine's own sample-accurate elapsed count into a frame position and
    /// nudges the graph to follow it once zoomed in far enough that the playhead would otherwise
    /// scroll off the visible edge.</summary>
    private void UpdatePlayhead()
    {
        if (_auditionEngine is null || _analysis is null)
        {
            return;
        }

        // See AuditionClock.SamplesPlayed for why this reads the device's play position
        // rather than the engine's own produced count when an output device is attached.
        long samples;
        if (_auditionOutput is not null)
        {
            long? played = AuditionClock.SamplesPlayed(_auditionOutput, _auditionEngine);
            if (played is null)
            {
                return;
            }
            samples = played.Value;
        }
        else
        {
            samples = _auditionEngine.SamplesProduced;
        }

        double seconds = samples / (double)SongPlaybackEngine.SampleRate;
        float frame = (float)(seconds * DigiEncoder.FrameRate);

        if (frame >= _analysis.FrameCount + 8)
        {
            StopAudition();
            return;
        }

        _canvas.PlayheadFrame = frame;

        float playheadX = _canvas.XOfFrame(frame);
        int scrollX = -_host.AutoScrollPosition.X;
        int visibleWidth = _host.ClientSize.Width;
        if (playheadX > scrollX + visibleWidth - 40)
        {
            int newScroll = (int)(playheadX - visibleWidth + 60);
            newScroll = Math.Max(0, Math.Min(newScroll, Math.Max(0, _canvas.ContentWidth - visibleWidth)));
            _host.AutoScrollPosition = new Point(newScroll, 0);
        }
    }

    // ---------------------------------------------------------------- add to song

    private async Task AddToSongAsync()
    {
        if (_analysis is null)
        {
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

        var added = DigiEncoder.AddToSong(_session.Song, result, _analysis.FrameCount);
        _session.NotifyStructureChanged();
        MessageBox.Show(this, added.Message, "Digi to SID-osc", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>Mouse-wheel zoom, keeping the frame under the cursor stationary - so zooming in
    /// on a spot in the middle of a long clip doesn't fling the view somewhere else.</summary>
    private void OnWheelZoom(int x, int delta)
    {
        if (_analysis is null || _analysis.FrameCount == 0)
        {
            return;
        }

        int scrollX = -_host.AutoScrollPosition.X;
        int pointer = x - scrollX;
        float frameUnderMouse = _canvas.FrameAt(x);
        _canvas.Zoom(delta > 0 ? 1.2f : 1f / 1.2f);

        int newScrollX = (int)(_canvas.XOfFrame(frameUnderMouse) - pointer);
        newScrollX = Math.Max(0, Math.Min(newScrollX, Math.Max(0, _canvas.ContentWidth - _host.ClientSize.Width)));
        _host.AutoScrollPosition = new Point(newScrollX, 0);
    }
}
