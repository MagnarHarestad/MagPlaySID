using MagSid.Playback;
using NAudio.Wave;

namespace MagSid.Editor.App.Player;

/// <summary>
/// Plays a <c>.sid</c> file and shows what it is doing: one spectrum across the whole audible range,
/// and one scope per SID voice.
///
/// Everything drawn here comes from <see cref="SidFilePlaybackEngine"/> actually executing the
/// tune's 6502 code, not from the tracker's model of it. So the window is also the answer to "is the
/// file I exported right" - the scopes show the voices the exported bytes are really driving.
/// </summary>
public sealed class SidPlayerForm : Form
{
    /// <summary>60Hz. The engine's scope buffers hold 46ms, so nothing is missed between frames, and
    /// a slower refresh makes the peak marks visibly step.</summary>
    private const int RefreshMilliseconds = 16;

    private readonly string _path;
    private readonly SidFile _file;

    private readonly SpectrumView _spectrum = new() { Dock = DockStyle.Fill };
    private readonly VoiceScopeView[] _scopes =
        [new VoiceScopeView(0), new VoiceScopeView(1), new VoiceScopeView(2)];

    private readonly TransportButton _previous = new(TransportGlyph.Previous);
    private readonly TransportButton _playPause = new(TransportGlyph.Pause);
    private readonly TransportButton _next = new(TransportGlyph.Next);

    private readonly Label _title = new();
    private readonly Label _details = new();
    private readonly Label _elapsed = new();
    private readonly Button _export = new();

    /// <summary>How long the tune runs before it repeats, found by running it. Null while the
    /// search is still going, or when the tune shows no repeating state at all.</summary>
    private SidSongLength.Result? _length;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = RefreshMilliseconds };

    private readonly bool[] _muted = new bool[3];

    private WaveOutEvent? _output;
    private SidFilePlaybackEngine? _engine;
    private int _song;

    public SidPlayerForm(string path)
    {
        _path = path;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new SidPlaybackException($"Could not read {path}: {ex.Message}");
        }

        _file = SidFile.Parse(bytes);
        if (_file.UnsupportedReason() is { } reason)
            throw new SidPlaybackException(reason);

        _song = _file.StartSong;

        BuildLayout();
        Start(_song);
    }

    private void BuildLayout()
    {
        Text = string.IsNullOrWhiteSpace(_file.Name) ? Path.GetFileName(_path) : _file.Name;
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 460);
        ClientSize = new Size(880, 560);
        BackColor = SidPlayerPalette.Background;
        ForeColor = SidPlayerPalette.Title;
        DarkTheme.ApplyTitleBar(this);
        KeyPreview = true;

        _title.Text = string.IsNullOrWhiteSpace(_file.Name) ? Path.GetFileNameWithoutExtension(_path) : _file.Name;
        _title.Font = new Font("Segoe UI", 15f, FontStyle.Regular);
        _title.ForeColor = SidPlayerPalette.Title;
        _title.AutoSize = true;
        _title.Margin = new Padding(4, 0, 0, 0);

        _details.Text = DescribeFile();
        _details.Font = new Font("Segoe UI", 8.5f);
        _details.ForeColor = SidPlayerPalette.Subtitle;
        _details.AutoSize = true;
        _details.Margin = new Padding(4, 2, 0, 0);

        // Played and total, in the title's own white - it is the one number a listener keeps
        // glancing at, and a dim one beside bright transport glyphs reads as disabled.
        _elapsed.Text = "00:00 - --:--";
        _elapsed.Font = new Font("Consolas", 12f);
        _elapsed.ForeColor = SidPlayerPalette.Title;
        _elapsed.AutoSize = true;
        _elapsed.Anchor = AnchorStyles.None;
        _elapsed.AutoSize = true;
        _elapsed.TextAlign = ContentAlignment.MiddleCenter;
        _elapsed.Margin = new Padding(14, 8, 14, 0);

        var titleBlock = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SidPlayerPalette.Background,
            Margin = Padding.Empty,
        };
        titleBlock.Controls.Add(_title);
        titleBlock.Controls.Add(_details);

        // The export button lives in the top right, opposite the title. A fixed-height strip with
        // the button anchored to its corner, rather than anything that sizes itself - the previous
        // attempt let the button decide its own height inside a shorter strip and the window's edge
        // cut it in half.
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(18, 12, 18, 10),
            BackColor = SidPlayerPalette.Background,
        };
        header.Controls.Add(titleBlock);
        header.Controls.Add(_export);

        var scopes = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 132,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = SidPlayerPalette.Background,
            Padding = new Padding(18, 8, 18, 4),
        };
        for (int i = 0; i < 3; i++)
        {
            scopes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            _scopes[i].Dock = DockStyle.Fill;
            _scopes[i].Margin = new Padding(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            _scopes[i].MuteToggled += OnMuteToggled;
            scopes.Controls.Add(_scopes[i], i, 0);
        }

        _previous.Click += (_, _) => Restart();
        _playPause.Click += (_, _) => TogglePause();
        _next.Click += (_, _) => NextSong();
        _next.Enabled = _file.Songs > 1;
        _previous.Enabled = true;

        // A table rather than a FlowLayoutPanel. A flow panel wraps by default, and this one sits
        // in an auto-sized column squeezed between two half-width ones - so it wrapped, and every
        // row after the first fell outside the strip's height and simply vanished. Only the restart
        // button was ever visible. A table does not wrap.
        var group = new TableLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = SidPlayerPalette.Background,
            Margin = Padding.Empty,
        };
        for (int i = 0; i < 4; i++)
            group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // The time is the centred element and the restart button sits immediately left of it, as
        // asked; pause and next keep their order on the other side.
        group.Controls.Add(_previous, 0, 0);
        group.Controls.Add(_elapsed, 1, 0);
        group.Controls.Add(_playPause, 2, 0);
        group.Controls.Add(_next, 3, 0);

        // One centred cell now that the export button has moved to the header, so the transport
        // sits in the middle of the window rather than the middle of a column.
        var transport = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = SidPlayerPalette.Background,
            Padding = new Padding(18, 6, 18, 8),
        };
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        transport.Controls.Add(group, 0, 0);

        var spectrumHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 4, 18, 4),
            BackColor = SidPlayerPalette.Background,
        };
        spectrumHost.Controls.Add(_spectrum);

        Controls.Add(spectrumHost);
        Controls.Add(scopes);
        Controls.Add(transport);
        Controls.Add(header);

        _export.Text = "Export audio...";
        // Pinned to the header's top-right corner, at a size it does not get to choose - a button
        // that sizes itself can grow taller than the strip holding it and be clipped away.
        _export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _export.AutoSize = false;
        _export.Size = new Size(126, 30);
        _export.Location = new Point(header.ClientSize.Width - 126 - 18, 12);
        _export.Click += (_, _) => DoExport();
        DarkTheme.Apply(_export);

        // After the theme, not before: it sets its own colours, and this window wants the title's
        // white so the button reads as part of the same header rather than as editor chrome.
        _export.ForeColor = SidPlayerPalette.Title;
        _export.FlatAppearance.BorderColor = SidPlayerPalette.PanelBorder;
        _export.BackColor = SidPlayerPalette.PanelBackground;

        _timer.Tick += (_, _) => Refresh_();
        _timer.Start();

        // Finding the length means running the whole tune, which takes a second or two - off the
        // UI thread, so the window opens and plays at once and the number appears when it is known.
        var file = _file;
        Task.Run(() => SidSongLength.Measure(file)).ContinueWith(
            task =>
            {
                if (!IsDisposed && task.IsCompletedSuccessfully)
                    BeginInvoke(() => _length = task.Result);
            },
            TaskScheduler.Default);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                TogglePause();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        };
    }

    private string DescribeFile()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_file.Author))
            parts.Add(_file.Author);
        if (!string.IsNullOrWhiteSpace(_file.Released))
            parts.Add(_file.Released);

        parts.Add($"${_file.LoadAddress:X4}-${_file.LoadAddress + _file.Data.Length - 1:X4}");
        parts.Add($"{_file.Data.Length} bytes");
        parts.Add(_file.Is8580 ? "8580" : "6581");

        if (_file.Songs > 1)
            parts.Add($"tune {_song} of {_file.Songs}");

        return string.Join("   ·   ", parts);
    }

    private void Start(int song)
    {
        Stop();

        _engine = new SidFilePlaybackEngine(_file, chipModel: null, song: song);
        for (int voice = 0; voice < 3; voice++)
            _engine.SetVoiceMuted(voice, _muted[voice]);

        // 100ms in four buffers, so a refill lands every ~25ms. The visuals no longer depend on
        // refill granularity - they follow the device's play position - but a shallower buffer also
        // shortens the gap between pressing pause and hearing it.
        _output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 4 };
        _output.Init(_engine);
        _output.Play();

        _playPause.Glyph = TransportGlyph.Pause;
        _details.Text = DescribeFile();
    }

    private void Stop()
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
        if (_file.Songs <= 1)
            return;

        _song = _song >= _file.Songs ? 1 : _song + 1;
        Restart();
    }

    private void OnMuteToggled(int voice, bool muted)
    {
        _muted[voice] = muted;
        _engine?.SetVoiceMuted(voice, muted);
    }

    /// <summary>Named with a trailing underscore so it cannot be mistaken for Control.Refresh, which
    /// this is not - it advances the visualisations from the engine's own buffers.</summary>
    private void Refresh_()
    {
        if (_engine is not { } engine)
            return;

        // The device's own play position, not SamplesProduced. Read() runs a whole buffer ahead, so
        // produced samples are ones the sound card has but nobody has heard yet - drawing those made
        // the scopes lead the music and step forward a buffer at a time, about ten times a second,
        // however often this timer ticked. See AuditionClock.
        long at = _output is { } output
            ? AuditionClock.SamplesPlayed(output, engine) ?? engine.SamplesProduced
            : engine.SamplesProduced;

        _spectrum.Update(engine, at);
        foreach (var scope in _scopes)
            scope.Update(engine, at);

        _elapsed.Text = $"{Clock(PositionWithinSong(at))} - {(_length is { } length ? Clock(length.Total) : "--:--")}";
    }

    /// <summary>
    /// Where the listener is inside the song rather than how long they have sat there.
    ///
    /// A tune loops forever, so once playback passes the end it folds back into the repeating part
    /// - otherwise the played time would climb past the total and read as nonsense. Before the
    /// length is known, or for a tune that never repeats, it simply counts up.
    /// </summary>
    private TimeSpan PositionWithinSong(long samples)
    {
        var elapsed = TimeSpan.FromSeconds(samples / (double)SidFilePlaybackEngine.SampleRate);

        if (_length is not { } length || length.LoopFrames <= 0 || elapsed <= length.Total)
            return elapsed;

        double intoLoop = (elapsed - length.LoopStart).TotalSeconds % (length.LoopFrames / 50.0);
        return length.LoopStart + TimeSpan.FromSeconds(intoLoop);
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    /// <summary>
    /// Writes the tune to a file by rendering it, faster than real time.
    ///
    /// One pass through the song by default - the length that was found - because a tune loops
    /// forever and something has to decide where to stop. A tune with no findable length gets three
    /// minutes, which is a guess, and the dialog says so by opening on that number.
    /// </summary>
    private void DoExport()
    {
        bool mp3 = SidAudioExport.IsMp3Available();

        using var save = new SaveFileDialog
        {
            Title = "Export audio",
            Filter = mp3
                ? "WAV audio (*.wav)|*.wav|MP3 audio (*.mp3)|*.mp3"
                : "WAV audio (*.wav)|*.wav",
            FileName = (string.IsNullOrWhiteSpace(_file.Name)
                ? Path.GetFileNameWithoutExtension(_path)
                : _file.Name.Trim()) + ".wav",
        };

        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        var format = Path.GetExtension(save.FileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            ? SidAudioExport.Format.Mp3
            : SidAudioExport.Format.Wav;

        int frames = _length?.TotalFrames ?? (50 * 60 * 3);

        string previous = _export.Text;
        _export.Enabled = false;
        _export.Text = "Rendering...";

        var file = _file;
        string path = save.FileName;

        Task.Run(() => SidAudioExport.Render(file, path, format, frames)).ContinueWith(task =>
        {
            if (IsDisposed)
                return;

            BeginInvoke(() =>
            {
                _export.Enabled = true;
                _export.Text = previous;

                if (task.Exception?.GetBaseException() is { } error)
                {
                    MessageBox.Show(this, error.Message, "Cannot export audio",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }, TaskScheduler.Default);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        Stop();
        base.OnFormClosed(e);
    }
}
