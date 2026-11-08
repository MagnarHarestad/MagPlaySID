using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagPlay.Amiga;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// An Amiga module as ProTracker shows it: the pattern playing, a row a line, its row heard on the
/// middle line - each channel's note, sample and effect - with where the song is in its order, and
/// beside it the samples, each lit while a channel plays it. The module is its own score: nothing
/// here is worked out, all of it is read from the file.
/// </summary>
internal sealed class ModuleView : Control
{
    private double _scale = 1;

    private double RowHeight => 17 * _scale;
    private double FontSize => 13 * _scale;
    private double HeaderHeight => 26 * _scale;
    private double Gap => 10 * _scale;

    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface Bold = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);

    private static readonly string[] NoteNames = ["C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-"];

    private static readonly string[] RowNumbers = Enumerable.Range(0, ModFile.Rows).Select(r => $"{r:00}").ToArray();

    /// <summary>The pattern's name and the place in the song, made when they change, not each frame.</summary>
    private string _patternText = "", _placeText = "";

    /// <summary>Text laid out once and drawn again each frame it shows - see ScoreView.</summary>
    private readonly Dictionary<(string Text, Color Ink, bool Bold), TextLayout> _laidOut = [];

    private double _charWidth;
    private ModFile? _module;
    private string[][] _cells = [];
    private string[] _sampleLines = [];
    private int _order = -1, _row = -1;
    private readonly HashSet<int> _playing = [];

    public ModuleView() => _charWidth = Measure("0").WidthIncludingTrailingWhitespace;

    private int Channels => _module?.Channels ?? 4;

    /// <summary>Characters a channel's column has: "C-2 1F A0F".</summary>
    private const int CellChars = 10;

    private double ChannelWidth => (CellChars + 1) * _charWidth;

    private double RowNumberWidth => 3 * _charWidth;

    private double TrackerWidth => RowNumberWidth + Channels * (ChannelWidth + Gap);

    private double PanelWidth => 30 * _charWidth;

    /// <summary>The width the rows and the samples take.</summary>
    public double ContentWidth => TrackerWidth + Gap + PanelWidth;

    public double TextScale
    {
        get => _scale;
        set
        {
            _scale = value;
            Forget();
            _charWidth = Measure("0").WidthIncludingTrailingWhitespace;
            Width = ContentWidth;
            InvalidateVisual();
        }
    }

    /// <summary>The module shown - every row's text made now, once.</summary>
    public void SetModule(ModFile? module)
    {
        _module = module;
        Forget();
        _order = _row = -1;
        _playing.Clear();
        if (module is null)
        {
            _cells = [];
            _sampleLines = [];
        }
        else
        {
            _cells = new string[module.Patterns.Count][];
            for (int p = 0; p < module.Patterns.Count; p++)
            {
                _cells[p] = new string[ModFile.Rows * module.Channels];
                for (int i = 0; i < _cells[p].Length; i++)
                    _cells[p][i] = CellText(module.Patterns[p][i]);
            }
            _sampleLines = module.Samples.Select((s, i) => SampleLine(i + 1, s)).ToArray();
        }
        Width = ContentWidth;
        InvalidateVisual();
    }

    /// <summary>Where the song is heard, and the samples its channels play there - drawn again only
    /// when that changes.</summary>
    public void Show(ModuleTune tune, long at)
    {
        var (order, row) = tune.RowAt(at);
        bool changed = order != _order || row != _row;
        if (changed && _module is { } module)
        {
            (_order, _row) = (order, row);
            _patternText = $"P{module.Order[Math.Clamp(order, 0, module.Order.Count - 1)]:X2}";
            _placeText = $"Position {order:00} of {module.Order.Count:00}, row {row:00}";
        }
        Span<int> now = stackalloc int[32];
        int count = 0;
        for (int c = 0; c < tune.Voices && count < now.Length; c++)
        {
            if (tune.SampleAt(c, at) is > 0 and var sample)
                now[count++] = sample;
        }
        var playing = now[..count];
        if (playing.Length != _playing.Count || !AllIn(playing))
        {
            _playing.Clear();
            foreach (int sample in playing)
                _playing.Add(sample);
            changed = true;
        }
        if (changed)
            InvalidateVisual();
    }

    private bool AllIn(Span<int> samples)
    {
        foreach (int sample in samples)
            if (!_playing.Contains(sample))
                return false;
        return true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground), bounds);
        if (_module is not { } module)
            return;

        int order = Math.Clamp(_order, 0, module.Order.Count - 1), row = Math.Max(0, _row);
        int pattern = module.Order[order];
        double line = Math.Round((bounds.Height + HeaderHeight - RowHeight) / 2);

        for (int c = 0; c < module.Channels; c++)
        {
            double left = RowNumberWidth + Gap + c * (ChannelWidth + Gap);
            Text(context, $"CH {c + 1}", left, 5 * _scale, SidPlayerPalette.VoiceColour(c % 3), bold: true);
        }
        Text(context, _patternText, 0, 5 * _scale, SidPlayerPalette.Subtitle);

        using (context.PushClip(new Rect(0, HeaderHeight, TrackerWidth, bounds.Height - HeaderHeight)))
        {
            context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 18)),
                                  new Rect(0, line, TrackerWidth, RowHeight));
            if (pattern < _cells.Length)
            {
                var cells = _cells[pattern];
                int first = Math.Max(0, row - (int)Math.Ceiling((line - HeaderHeight) / RowHeight));
                for (int r = first; r < ModFile.Rows; r++)
                {
                    double y = line + (r - row) * RowHeight;
                    if (y > bounds.Height)
                        break;
                    bool heard = r == row;
                    Text(context, RowNumbers[r], 0, y + 1, heard ? SidPlayerPalette.Title : SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, r % 4 == 0 ? 200 : 110));
                    for (int c = 0; c < module.Channels; c++)
                    {
                        string text = cells[r * module.Channels + c];
                        bool empty = text[0] == '-' && text[4] == '.' && text[7] == '.';
                        var colour = SidPlayerPalette.VoiceColour(c % 3);
                        var ink = heard ? SidPlayerPalette.Title : empty ? SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 110)
                            : SidPlayerPalette.WithAlpha(colour, 220);
                        Text(context, text, RowNumberWidth + Gap + c * (ChannelWidth + Gap), y + 1, ink, bold: heard);
                    }
                }
            }
        }

        DrawPanel(context, module, TrackerWidth + Gap, bounds.Height);
    }

    private void DrawPanel(DrawingContext context, ModFile module, double left, double height)
    {
        double y = 5 * _scale;
        Text(context, "SAMPLES", left, y, SidPlayerPalette.Title, bold: true);
        y += RowHeight;
        Text(context, _placeText, left, y, SidPlayerPalette.Subtitle);
        y += RowHeight * 1.5;
        for (int s = 0; s < _sampleLines.Length && y < height - RowHeight; s++)
        {
            // An empty slot - ProTracker keeps it two bytes long - and no name: nothing to list.
            if (module.Samples[s].Data.Length <= 2 && string.IsNullOrEmpty(module.Samples[s].Name))
                continue;
            bool lit = _playing.Contains(s + 1);
            if (lit)
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 40)), new Rect(left - 4, y, PanelWidth, RowHeight));
            Text(context, _sampleLines[s], left, y + 1, lit ? SidPlayerPalette.Title : SidPlayerPalette.Control, bold: lit);
            y += RowHeight;
        }
    }

    /// <summary>A cell as ProTracker writes it: the note, the sample in hex, the effect and its
    /// parameter - dots and dashes where there is none.</summary>
    private static string CellText(ModCell cell)
    {
        string note = cell.Period > 0 ? NoteName(cell.Period) : "---";
        string sample = cell.Sample > 0 ? $"{cell.Sample:X2}" : "..";
        string effect = cell.Effect == 0 && cell.Parameter == 0 ? "..." : $"{cell.Effect:X}{cell.Parameter:X2}";
        return $"{note} {sample} {effect}";
    }

    /// <summary>ProTracker's name for a period's note: C-1 at 856 to B-3 at 113, and on past them.</summary>
    private static string NoteName(int period)
    {
        int note = (int)Math.Round(12 * Math.Log2(856.0 / period));
        int octave = 1 + (int)Math.Floor(note / 12.0);
        return $"{NoteNames[((note % 12) + 12) % 12]}{Math.Clamp(octave, 0, 9)}";
    }

    private static string SampleLine(int number, ModSample sample)
    {
        string name = sample.Name.Length > 18 ? sample.Name[..18] : sample.Name;
        string size = sample.Data.Length >= 10000 ? $"{sample.Data.Length / 1024}K" : $"{sample.Data.Length}";
        return $"{number:X2} {name,-18} {size,6}";
    }

    private TextLayout Measure(string text, Color? ink = null, bool bold = false)
    {
        var key = (text, ink ?? SidPlayerPalette.Control, bold);
        if (_laidOut.TryGetValue(key, out var laidOut))
            return laidOut;
        if (_laidOut.Count >= 4096)
            Forget();
        laidOut = new TextLayout(text, bold ? Bold : Mono, FontSize, SidPlayerPalette.Brush(key.Item2));
        _laidOut[key] = laidOut;
        return laidOut;
    }

    private void Forget()
    {
        foreach (var laidOut in _laidOut.Values)
            laidOut.Dispose();
        _laidOut.Clear();
    }

    private void Text(DrawingContext context, string text, double x, double y, Color ink, bool bold = false) =>
        Measure(text, ink, bold).Draw(context, new Point(x, y));
}
