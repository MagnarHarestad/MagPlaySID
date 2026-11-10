using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MagPlay.Amiga;
using MagPlaySid.Player;
using Avalonia.Rendering;

namespace MagPlaySid;

/// <summary>
/// A tracker module as its tracker shows it - ProTracker, Scream Tracker, FastTracker or Impulse
/// Tracker: the pattern playing, a row a line, scrolling smoothly
/// through the middle line as it is heard - each channel's note, sample and effect, the patterns
/// before and after it in the order dimmed beyond its ends - with where the song is in its order,
/// and beside it the samples or instruments, each lit while a channel plays it. The module is its own score: nothing
/// here is worked out, all of it is read from the file.
/// </summary>
internal sealed class ModuleView : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private double _scale = 1;

    private double RowHeight => 17 * _scale;
    private double FontSize => 13 * _scale;
    private double HeaderHeight => 26 * _scale;
    private double Gap => 10 * _scale;

    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface Bold = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.Bold);

    private static readonly string[] RowNumbers2 = Enumerable.Range(0, 256).Select(r => $"{r:00}").ToArray();
    private static readonly string[] RowNumbers3 = Enumerable.Range(0, 256).Select(r => $"{r:000}").ToArray();

    /// <summary>The pattern's name and the place in the song, made when they change, not each frame.</summary>
    private string _patternText = "", _placeText = "";

    /// <summary>Text laid out once and drawn again each frame it shows - see ScoreView.</summary>
    private readonly Dictionary<(string Text, Color Ink, bool Bold), TextLayout> _laidOut = [];

    private double _charWidth;
    private ITrackerModule? _module;

    /// <summary>Each pattern's cells as text, and whether each is empty - made when the pattern is
    /// first shown: a big Impulse Tracker module has millions.</summary>
    private string[]?[] _cells = [];
    private bool[]?[] _empties = [];
    private int _rowDigits = 2;
    private string[] _sampleLines = [];

    /// <summary>The samples or instruments listed - empty slots left out - and the first shown: a
    /// module with more than the panel holds scrolls, by the wheel or its bar, and follows the one
    /// playing when it is out of sight and the list has not been moved for a while.</summary>
    private int[] _listed = [];
    private int _listFirst;
    private long _listMoved = long.MinValue / 2;
    private const double FollowAfterSeconds = 4;

    /// <summary>Where the list was last drawn: its left, its top, and how many lines it holds.</summary>
    private double _listLeft = double.MaxValue, _listTop;
    private int _listShown = int.MaxValue;
    private bool _draggingBar;
    private int _order = -1, _row = -1;

    /// <summary>How far into the row heard, in pixels: the rows are drawn again when it moves.</summary>
    private double _into;
    private readonly HashSet<int> _playing = [];

    public ModuleView() => _charWidth = Measure("0").WidthIncludingTrailingWhitespace;

    private int Channels => _module?.Channels ?? 4;

    /// <summary>Characters a channel's column has: "C-2 1F A0F" on the Amiga, "C-5 01 64 A0F" on the PC.</summary>
    private int CellChars => _module?.CellChars ?? 10;

    private double ChannelWidth => (CellChars + 1) * _charWidth;

    private double RowNumberWidth => (_rowDigits + 1) * _charWidth;

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

    /// <summary>The module shown - each pattern's text made when it is first drawn.</summary>
    public void SetModule(ITrackerModule? module)
    {
        _module = module;
        Forget();
        _order = _row = -1;
        _into = 0;
        _playing.Clear();
        if (module is null)
        {
            _cells = [];
            _empties = [];
            _sampleLines = [];
            _listed = [];
        }
        else
        {
            _cells = new string[module.PatternCount][];
            _empties = new bool[module.PatternCount][];
            _rowDigits = Enumerable.Range(0, module.PatternCount).Any(p => module.RowsIn(p) > 100) ? 3 : 2;
            _sampleLines = module.Samples.Select((s, i) => SampleLine(i + 1, s)).ToArray();
            // An empty slot - ProTracker keeps it two bytes long - and no name: nothing to list.
            _listed = Enumerable.Range(0, module.Samples.Count)
                .Where(s => module.Samples[s].Bytes > 2 || !string.IsNullOrEmpty(module.Samples[s].Name)).ToArray();
        }
        _listFirst = 0;
        _listMoved = long.MinValue / 2;
        Width = ContentWidth;
        InvalidateVisual();
    }

    /// <summary>Where the song is heard, and the samples its channels play there - drawn again only
    /// when that changes: playing, as often as the rows move a pixel; paused, not at all.</summary>
    public void Show(ModuleTune tune, long at)
    {
        var (order, row, into) = tune.PlaceAt(at);
        double pixels = Math.Round(into * RowHeight * 2) / 2;
        bool changed = order != _order || row != _row || pixels != _into;
        _into = pixels;
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
            Follow();
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
        if (!Playable(module, order))
            return;
        double line = Math.Round((bounds.Height + HeaderHeight - RowHeight) / 2);
        // The row heard starts above the line by as far as it has played.
        double top = line - _into;

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
            // The rows on screen, counted from the heard one: those past its pattern's ends are the
            // patterns before and after it in the order, as the song goes on if nothing jumps.
            int above = (int)Math.Ceiling((top - HeaderHeight) / RowHeight);
            int below = (int)Math.Ceiling((bounds.Height - top) / RowHeight);
            for (int i = -above; i < below; i++)
            {
                int place = order, r = row + i;
                while (r < 0 && Neighbour(module, place, -1) is { } before)
                    (place, r) = (before, r + module.RowsIn(module.Order[before]));
                while (r >= module.RowsIn(module.Order[place]) && Neighbour(module, place, 1) is { } after)
                    (place, r) = (after, r - module.RowsIn(module.Order[place]));
                if (r < 0 || r >= module.RowsIn(module.Order[place]))
                    continue;
                double y = top + i * RowHeight;
                if (r == 0 && place > 0)
                    context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder), new Rect(0, y - 1, TrackerWidth, 1));
                int pattern = module.Order[place];
                if (place == order)
                    DrawRow(context, module, pattern, r, y, heard: i == 0);
                else
                {
                    // Faded as a whole, so its text is the same laid out text as when it plays.
                    using (context.PushOpacity(0.4))
                        DrawRow(context, module, pattern, r, y, heard: false);
                }
            }
        }

        DrawPanel(context, module, TrackerWidth + Gap, bounds.Height);
    }

    /// <summary>Whether an order position names a pattern there is: not a marker.</summary>
    private static bool Playable(ITrackerModule module, int place) => module.Order[place] < module.PatternCount;

    /// <summary>The order position before or after <paramref name="place"/> that names a pattern -
    /// markers passed over, the end marker the end; null at either end.</summary>
    private static int? Neighbour(ITrackerModule module, int place, int step)
    {
        for (int p = place + step; p >= 0 && p < module.Order.Count; p += step)
        {
            if (module.Order[p] == 255)
                return null;
            if (Playable(module, p))
                return p;
        }
        return null;
    }

    /// <summary>A pattern's cells as text, made the first time it is drawn.</summary>
    private (string[] Cells, bool[] Empty) PatternText(ITrackerModule module, int pattern)
    {
        if (_cells[pattern] is { } made)
            return (made, _empties[pattern]!);
        int rows = module.RowsIn(pattern), channels = module.Channels;
        var cells = new string[rows * channels];
        var empty = new bool[rows * channels];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < channels; c++)
            {
                cells[r * channels + c] = module.CellText(pattern, r, c);
                empty[r * channels + c] = module.CellEmpty(pattern, r, c);
            }
        (_cells[pattern], _empties[pattern]) = (cells, empty);
        return (cells, empty);
    }

    /// <summary>One row: its number and each channel's cell, the heard one bright.</summary>
    private void DrawRow(DrawingContext context, ITrackerModule module, int pattern, int r, double y, bool heard)
    {
        var (cells, empties) = PatternText(module, pattern);
        Text(context, (_rowDigits == 3 ? RowNumbers3 : RowNumbers2)[r], 0, y + 1, heard ? SidPlayerPalette.Title
            : SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, r % 4 == 0 ? 200 : 110));
        for (int c = 0; c < module.Channels; c++)
        {
            string text = cells[r * module.Channels + c];
            bool empty = empties[r * module.Channels + c];
            var ink = heard ? SidPlayerPalette.Title
                : empty ? SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 110)
                : SidPlayerPalette.WithAlpha(SidPlayerPalette.VoiceColour(c % 3), 220);
            Text(context, text, RowNumberWidth + Gap + c * (ChannelWidth + Gap), y + 1, ink, bold: heard);
        }
    }

    private void DrawPanel(DrawingContext context, ITrackerModule module, double left, double height)
    {
        double y = 5 * _scale;
        Text(context, module.SamplesHeading, left, y, SidPlayerPalette.Title, bold: true);
        y += RowHeight;
        Text(context, _placeText, left, y, SidPlayerPalette.Subtitle);
        y += RowHeight * 1.5;
        _listLeft = left;
        _listTop = y;
        _listShown = Math.Max(1, (int)((height - RowHeight - y) / RowHeight) + 1);
        _listFirst = Math.Clamp(_listFirst, 0, Math.Max(0, _listed.Length - _listShown));
        for (int i = _listFirst; i < _listed.Length && i < _listFirst + _listShown; i++)
        {
            int s = _listed[i];
            bool lit = _playing.Contains(s + 1);
            if (lit)
                context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 40)), new Rect(left - 4, y, BarLeft - left + 2, RowHeight));
            Text(context, _sampleLines[s], left, y + 1, lit ? SidPlayerPalette.Title : SidPlayerPalette.Control, bold: lit);
            y += RowHeight;
        }

        // The scroll bar, when there are more than it holds: its track, and the thumb where the list is.
        if (Scrolls)
        {
            double track = _listShown * RowHeight, width = BarWidth;
            context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 40)), new Rect(BarLeft, _listTop, width, track), (float)(width / 2));
            double thumb = Thumb;
            double at = (track - thumb) * _listFirst / (_listed.Length - _listShown);
            var ink = _draggingBar ? SidPlayerPalette.Title : SidPlayerPalette.WithAlpha(SidPlayerPalette.Subtitle, 200);
            context.FillRectangle(SidPlayerPalette.Brush(ink), new Rect(BarLeft, _listTop + at, width, thumb), (float)(width / 2));
        }
    }

    /// <summary>The bar's left edge and width: in the panel's last characters, past the sizes.</summary>
    private double BarLeft => _listLeft + (28.6 * _charWidth);
    private double BarWidth => 5 * _scale;

    private double Thumb => Math.Max(RowHeight, _listShown * RowHeight * _listShown / Math.Max(1, _listed.Length));

    private bool Scrolls => _listed.Length > _listShown;

    private bool OverList(Point p) => p.X >= _listLeft - 4 && p.Y >= _listTop - RowHeight;

    private bool OverBar(Point p) => Scrolls && p.X >= BarLeft - (6 * _scale) && p.X <= BarLeft + BarWidth + (6 * _scale)
        && p.Y >= _listTop && p.Y <= _listTop + (_listShown * RowHeight);

    private void ScrollTo(int first)
    {
        first = Math.Clamp(first, 0, Math.Max(0, _listed.Length - _listShown));
        _listMoved = Environment.TickCount64;
        if (first == _listFirst)
            return;
        _listFirst = first;
        InvalidateVisual();
    }

    /// <summary>The list scrolled to a sample or instrument playing, when none playing is in sight -
    /// unless the listener moved it a moment ago.</summary>
    private void Follow()
    {
        if (!Scrolls || _playing.Count == 0 || Environment.TickCount64 - _listMoved < FollowAfterSeconds * 1000)
            return;
        int nearest = -1;
        for (int i = 0; i < _listed.Length; i++)
        {
            if (!_playing.Contains(_listed[i] + 1))
                continue;
            if (i >= _listFirst && i < _listFirst + _listShown)
                return;
            if (nearest < 0)
                nearest = i;
        }
        if (nearest >= 0)
            _listFirst = Math.Clamp(nearest - (_listShown / 3), 0, _listed.Length - _listShown);
    }

    /// <summary>The list moved to where the bar is pressed or dragged: the thumb's middle there.</summary>
    private void DragBar(double y)
    {
        double track = _listShown * RowHeight, thumb = Thumb;
        double share = Math.Clamp((y - _listTop - (thumb / 2)) / Math.Max(1, track - thumb), 0, 1);
        ScrollTo((int)Math.Round(share * (_listed.Length - _listShown)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (Scrolls && OverList(e.GetPosition(this)))
        {
            ScrollTo(_listFirst - (int)Math.Round(e.Delta.Y * 3));
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed && OverBar(point.Position))
        {
            _draggingBar = true;
            e.Pointer.Capture(this);
            DragBar(point.Position.Y);
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_draggingBar)
            DragBar(p.Y);
        Cursor = _draggingBar || OverBar(p) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_draggingBar)
        {
            _draggingBar = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        base.OnPointerReleased(e);
    }

    private static string SampleLine(int number, ModuleSample sample)
    {
        string name = sample.Name.Length > 18 ? sample.Name[..18] : sample.Name;
        string size = sample.Bytes >= 10000 ? $"{sample.Bytes / 1024}K" : $"{sample.Bytes}";
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
