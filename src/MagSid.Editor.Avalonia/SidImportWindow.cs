using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MagSid.Core.SidImport;
using MagSid.Playback;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// File → Import SID: which subtune and which stretch of it, the row grid, and what the song will
/// be - how many of the tune's notes it plays, and what it costs - before anything is replaced.
/// The choices, the import and the words are the Windows dialog's (SidImportForm, SidTranscriber,
/// SidImportText); only the controls are Avalonia's.
/// </summary>
internal sealed class SidImportWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Body = new SolidColorBrush(AvColor.FromRgb(0xD2, 0xD2, 0xDE));
    private static readonly IBrush Heading = new SolidColorBrush(AvColor.FromRgb(0x66, 0xCC, 0xFF));
    private static readonly IBrush Warning = new SolidColorBrush(AvColor.FromRgb(0xFF, 0xB0, 0x5C));

    private readonly SidFile _file;
    private readonly NumericUpDown _subtune;
    private readonly NumericUpDown _from = Seconds();
    private readonly NumericUpDown _length = Seconds();
    private readonly TextBlock _tuneLength = new() { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown _framesPerRow = Number(1, 32);

    // Every second row's length - funk speed. The same as the first: one speed.
    private readonly NumericUpDown _funkFramesPerRow = Number(1, 32);
    private readonly TextBlock _speed = new() { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _filter = new() { Content = "Filter", IsChecked = true };
    private readonly CheckBox _vibrato = new() { Content = "Vibrato", IsChecked = true };
    private readonly CheckBox _slides = new() { Content = "Slides", IsChecked = true };
    private readonly TextBlock _report = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _import = new() { Content = "Import", MinWidth = 90, IsDefault = true };

    private SidTranscriber.Analysis? _analysis;
    private (int FramesPerRow, int? Funk) _suggested = (6, null);
    private bool _updating;

    /// <summary>The song as the report describes it - set while the choices make one.</summary>
    public SidImportResult? Result { get; private set; }

    public SidImportWindow(SidFile file, string fileName)
    {
        _file = file;
        _subtune = Number(1, Math.Max(1, file.Songs));
        _subtune.Value = Math.Clamp(file.StartSong, 1, Math.Max(1, file.Songs));
        _subtune.IsEnabled = file.Songs > 1;

        Title = "Import SID";
        Width = 780;
        FontSize = 13;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _import.Click += (_, _) => Close(Result);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = SidImportText.Heading(file, fileName), Foreground = Heading, TextWrapping = TextWrapping.Wrap },
                Row(Caption("Subtune"), _subtune, Caption($"of {Math.Max(1, file.Songs)}"),
                    Caption("From"), _from, Caption("s,"), _length, Caption("s long"), _tuneLength),
                Row(Caption("Frames a row"), _framesPerRow, Caption("then"), _funkFramesPerRow, _speed, _filter, _vibrato, _slides),
                _report,
                new TextBlock
                {
                    Text = SidImportText.Hint,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Dim,
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 0),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children = { _import, cancel },
                },
            },
        };

        _subtune.ValueChanged += (_, _) => Trace();
        foreach (var number in new[] { _from, _length, _framesPerRow, _funkFramesPerRow })
            number.ValueChanged += (_, _) => Recalculate();
        _filter.IsCheckedChanged += (_, _) => Recalculate();
        _vibrato.IsCheckedChanged += (_, _) => Recalculate();
        _slides.IsCheckedChanged += (_, _) => Recalculate();

        Trace();
    }

    /// <summary>Runs the chosen subtune - as long as it plays before repeating, up to ten minutes -
    /// and starts from the choices the tune suggests.</summary>
    private void Trace()
    {
        _updating = true;
        try
        {
            int song = (int)(_subtune.Value ?? 1);
            var length = SidImportText.Length(_file, song);
            _analysis = SidTranscriber.Analyze(SidImportTrace.Record(_file, song, length.Frames));
            _tuneLength.Text = length.Describe;

            // The grid that sounds most of the tune, of those the note starts suggest.
            var best = SidImportChoice.Best(_analysis, 0, _analysis.FrameCount);
            _suggested = best is null
                ? SidTranscriber.SuggestGrid(_analysis)
                : (best.Options.FramesPerRow, best.Options.FunkFramesPerRow);

            decimal seconds = (decimal)(_analysis.FrameCount / 50.0);
            _from.Maximum = seconds;
            _length.Maximum = seconds;
            _from.Value = 0;
            _framesPerRow.Value = _suggested.FramesPerRow;
            _funkFramesPerRow.Value = _suggested.Funk ?? _suggested.FramesPerRow;
            int fits = best?.Options.FrameCount ?? _analysis.FrameCount;
            _length.Value = Math.Min(seconds, (decimal)(fits / 50.0));
        }
        catch (Exception ex) when (ex is SidPlaybackException or InvalidOperationException)
        {
            _analysis = null;
            Result = null;
            _report.Text = ex.Message;
            _report.Foreground = Warning;
            _import.IsEnabled = false;
            return;
        }
        finally
        {
            _updating = false;
        }

        Recalculate();
    }

    private static int Value(NumericUpDown number) => (int)Math.Round(number.Value ?? number.Minimum);

    private SidImportOptions Options(int frames) => new(
        Value(_framesPerRow), (int)Math.Round((_from.Value ?? 0) * 50), frames,
        _filter.IsChecked == true, _vibrato.IsChecked == true,
        FunkFramesPerRow: Value(_funkFramesPerRow) == Value(_framesPerRow) ? null : Value(_funkFramesPerRow),
        Slides: _slides.IsChecked == true);

    private void Recalculate()
    {
        if (_updating || _analysis is not { } analysis)
            return;

        int a = Value(_framesPerRow), b = Value(_funkFramesPerRow);
        _speed.Text = SidImportText.Grid(a, a == b ? null : b, _suggested);

        var options = Options(Math.Max(1, (int)Math.Round((_length.Value ?? 0) * 50)));
        try
        {
            Result = SidTranscriber.Import(analysis, options);
            var replayed = SidTranscriber.Analyze(SidImportTrace.Replay(Result.Song, Result.Report.Frames));
            var heard = SidTranscriber.Heard(analysis, options, replayed);
            _report.Text = SidImportText.Report(Result.Report, heard, SidImportTrace.CallsPerFrame(_file, Value(_subtune)));
            _report.Foreground = Body;
            _import.IsEnabled = true;
        }
        catch (SidImportException ex)
        {
            Result = null;
            _report.Text = ex.Message;
            _report.Foreground = Warning;
            _import.IsEnabled = false;
        }
    }

    private static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls)
        {
            control.Margin = new Thickness(0, 2, 8, 2);
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
        }
        return row;
    }

    private static NumericUpDown Number(int minimum, int maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = minimum,
        Increment = 1,
        FormatString = "0",
        Width = 110,
    };

    private static NumericUpDown Seconds() => new()
    {
        Minimum = 0,
        Maximum = 600,
        Value = 0,
        Increment = 1,
        FormatString = "0.0",
        Width = 120,
    };

    private static TextBlock Caption(string text) => new() { Text = text, Foreground = Body };
}
