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

        _elapsed.Text = "0:00";
        _elapsed.Font = new Font("Consolas", 10.5f);
        _elapsed.ForeColor = SidPlayerPalette.Subtitle;
        _elapsed.AutoSize = true;
        _elapsed.Anchor = AnchorStyles.Right;
        _elapsed.TextAlign = ContentAlignment.MiddleRight;

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 14, 18, 10),
            BackColor = SidPlayerPalette.Background,
        };
        header.Controls.Add(_title);
        header.Controls.Add(_details);

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

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.None,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = SidPlayerPalette.Background,
            Margin = Padding.Empty,
        };
        buttons.Controls.AddRange([_previous, _playPause, _next]);

        // A three-column strip with the buttons in the middle one, so they stay centred on the
        // window however wide the elapsed time on the right gets.
        var transport = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = SidPlayerPalette.Background,
            Padding = new Padding(18, 4, 18, 10),
        };
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        transport.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = SidPlayerPalette.Background }, 0, 0);
        transport.Controls.Add(buttons, 1, 0);
        transport.Controls.Add(_elapsed, 2, 0);

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

        _timer.Tick += (_, _) => Refresh_();
        _timer.Start();

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

        long seconds = at / SidFilePlaybackEngine.SampleRate;
        _elapsed.Text = $"{seconds / 60}:{seconds % 60:00}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        Stop();
        base.OnFormClosed(e);
    }
}
