using MagSid.Core.SidImport;
using MagSid.Playback;

namespace MagSid.Editor.App;

/// <summary>
/// Import SID: which subtune and which stretch of it, the row grid, and what the song will be -
/// how many of the tune's notes it plays, and what it costs - before anything is replaced. Every
/// change re-runs the import (SidTranscriber), so the report is always the song Import would make.
/// </summary>
public sealed class SidImportForm : Form
{
    private readonly SidFile _file;
    private readonly NumericUpDown _subtune;
    private readonly NumericUpDown _from = Seconds();
    private readonly NumericUpDown _length = Seconds();
    private readonly Label _tuneLength = new() { AutoSize = true, Margin = new Padding(6, 8, 18, 3), ForeColor = EditorPalette.Subtitle };
    private readonly NumericUpDown _framesPerRow = Number(1, 32);

    // Every second row's length - funk speed. The same as the first: one speed.
    private readonly NumericUpDown _funkFramesPerRow = Number(1, 32);
    private readonly Label _speed = new() { AutoSize = true, Margin = new Padding(6, 8, 18, 3), ForeColor = EditorPalette.Subtitle };
    private readonly CheckBox _filter = new() { Text = "Filter", Checked = true, AutoSize = true, Margin = new Padding(18, 6, 3, 3) };
    private readonly CheckBox _vibrato = new() { Text = "Vibrato", Checked = true, AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
    private readonly CheckBox _slides = new() { Text = "Slides", Checked = true, AutoSize = true, Margin = new Padding(6, 6, 3, 3) };
    private readonly Label _report = new() { AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(3, 10, 3, 3) };
    private readonly Button _import = new() { Text = "Import", DialogResult = DialogResult.OK, AutoSize = true };

    private SidTranscriber.Analysis? _analysis;
    private (int FramesPerRow, int? Funk) _suggested = (6, null);
    private bool _updating;

    /// <summary>The song as the report describes it - set while the choices make one.</summary>
    public SidImportResult? Result { get; private set; }

    public SidImportForm(SidFile file, string fileName)
    {
        _file = file;
        _subtune = Number(1, Math.Max(1, file.Songs));
        _subtune.Value = Math.Clamp(file.StartSong, 1, Math.Max(1, file.Songs));
        _subtune.Enabled = file.Songs > 1;

        Text = "Import SID";
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        DarkTheme.ApplyTitleBar(this);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        root.Controls.Add(Heading(SidImportText.Heading(file, fileName)));
        root.Controls.Add(Row(Caption("Subtune"), _subtune, Caption($"of {Math.Max(1, file.Songs)}"),
                              Caption("From"), _from, Caption("s,"), _length, Caption("s long"), _tuneLength));
        root.Controls.Add(Row(Caption("Frames a row"), _framesPerRow, Caption("then"), _funkFramesPerRow, _speed, _filter, _vibrato, _slides));
        root.Controls.Add(_report);
        root.Controls.Add(Hint(SidImportText.Hint));
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        _subtune.ValueChanged += (_, _) => Trace();
        foreach (var number in new[] { _from, _length, _framesPerRow, _funkFramesPerRow })
            number.ValueChanged += (_, _) => Recalculate();
        _filter.CheckedChanged += (_, _) => Recalculate();
        _vibrato.CheckedChanged += (_, _) => Recalculate();
        _slides.CheckedChanged += (_, _) => Recalculate();

        Trace();
    }

    /// <summary>Runs the chosen subtune - as long as it plays before repeating, up to ten minutes -
    /// and starts from the choices the tune suggests.</summary>
    private void Trace()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            int song = (int)_subtune.Value;
            var length = SidImportText.Length(_file, song);
            _analysis = SidTranscriber.Analyze(SidImportTrace.Record(_file, song, length.Frames));
            _tuneLength.Text = length.Describe;

            // The grid that sounds most of the tune, of those the note starts suggest.
            var best = SidImportChoice.Best(_analysis, 0, _analysis.FrameCount);
            _suggested = best is null
                ? SidTranscriber.SuggestGrid(_analysis)
                : (best.Options.FramesPerRow, best.Options.FunkFramesPerRow);

            _updating = true;
            _from.Maximum = _length.Maximum = (decimal)(_analysis.FrameCount / 50.0);
            _from.Value = 0;
            _framesPerRow.Value = _suggested.FramesPerRow;
            _funkFramesPerRow.Value = _suggested.Funk ?? _suggested.FramesPerRow;
            int fits = best?.Options.FrameCount ?? _analysis.FrameCount;
            _length.Value = Math.Min(_length.Maximum, (decimal)(fits / 50.0));
            _updating = false;

            Recalculate();
        }
        catch (Exception ex) when (ex is SidPlaybackException or InvalidOperationException)
        {
            _analysis = null;
            Result = null;
            _report.Text = ex.Message;
            _report.ForeColor = EditorPalette.Warning;
            _import.Enabled = false;
        }
        finally
        {
            _updating = false;
            Cursor = Cursors.Default;
        }
    }

    private SidImportOptions Options(int frames) => new(
        (int)_framesPerRow.Value, (int)Math.Round(_from.Value * 50), frames, _filter.Checked, _vibrato.Checked,
        FunkFramesPerRow: _funkFramesPerRow.Value == _framesPerRow.Value ? null : (int)_funkFramesPerRow.Value,
        Slides: _slides.Checked);

    private void Recalculate()
    {
        if (_updating || _analysis is not { } analysis)
            return;

        int a = (int)_framesPerRow.Value, b = (int)_funkFramesPerRow.Value;
        _speed.Text = SidImportText.Grid(a, a == b ? null : b, _suggested);

        var options = Options(Math.Max(1, (int)Math.Round(_length.Value * 50)));
        try
        {
            Result = SidTranscriber.Import(analysis, options);
            var replayed = SidTranscriber.Analyze(SidImportTrace.Replay(Result.Song, Result.Report.Frames));
            var heard = SidTranscriber.Heard(analysis, options, replayed);
            _report.Text = SidImportText.Report(Result.Report, heard, SidImportTrace.CallsPerFrame(_file, (int)_subtune.Value));
            _report.ForeColor = EditorPalette.Body;
            _import.Enabled = true;
        }
        catch (SidImportException ex)
        {
            Result = null;
            _report.Text = ex.Message;
            _report.ForeColor = EditorPalette.Warning;
            _import.Enabled = false;
        }
    }

    private Control BuildButtons()
    {
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        DarkTheme.Apply(_import);
        DarkTheme.Apply(cancel);
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0),
        };
        panel.Controls.Add(cancel);
        panel.Controls.Add(_import);
        AcceptButton = _import;
        CancelButton = cancel;
        return panel;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 56,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(3, 4, 3, 3),
    };

    private static NumericUpDown Seconds() => new()
    {
        Minimum = 0,
        Maximum = 600,
        DecimalPlaces = 1,
        Increment = 1,
        Width = 64,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(3, 4, 3, 3),
    };

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = EditorPalette.Heading,
        Margin = new Padding(3, 4, 3, 8),
    };

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(3, 8, 3, 3),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(720, 0),
        ForeColor = EditorPalette.Subtitle,
        Margin = new Padding(3, 8, 3, 3),
    };
}
