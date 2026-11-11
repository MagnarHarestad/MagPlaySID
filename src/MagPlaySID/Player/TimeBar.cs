using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;

namespace MagPlaySid.Player;

/// <summary>
/// Where the song is: the time played at the left, its length at the right, and between them a line
/// filled as far as it has got - drawn again only when a second or a pixel of it changes.
/// </summary>
internal sealed class TimeBar : Control, ICustomHitTest
{
    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    private const double TextWidth = 6; // characters each time takes: "00:00", "123:45" fits too

    private TimeSpan _played;
    private TimeSpan? _length;
    private double _filled = -1;
    private TextLayout? _left, _right;
    private (int Played, int Length, double Scale) _texts = (-1, -2, 0);

    public TimeBar()
    {
        Height = 22;
    }

    /// <summary>Whether a click on the line jumps there: a recording at once, a .sid or a module
    /// once it is ready - its length known, and a .sid's notes all read.</summary>
    public bool CanJump
    {
        get => _canJump;
        set
        {
            if (_canJump == value)
                return;
            _canJump = value;
            Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            ToolTip.SetTip(this, value ? "Click or drag to go to a place in the song" : null);
            InvalidateVisual();
        }
    }

    private bool _canJump;

    /// <summary>A click or drag let go on the line: the share of the song to go to, 0-1.</summary>
    public event Action<double>? JumpAsked;

    /// <summary>Where the pointer is over the line, 0-1, while it is - or being dragged; -1 when not.</summary>
    private double _hover = -1;
    private bool _dragging;

    /// <summary>Set while a jump is on its way: the place it goes to, 0-1, drawn till it is there.</summary>
    public double Jumping
    {
        get => _jumping;
        set
        {
            _jumping = value;
            InvalidateVisual();
        }
    }

    private double _jumping = -1;

    private (double From, double To) Line()
    {
        double size = 12 * TextScale, column = TextWidth * size * 0.62, gap = 12 * TextScale;
        return (column + gap, Bounds.Width - column - gap);
    }

    private double ShareAt(Point point)
    {
        var (from, to) = Line();
        return to > from ? Math.Clamp((point.X - from) / (to - from), 0, 1) : 0;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_canJump)
            return;
        _hover = ShareAt(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_canJump || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragging = true;
        e.Pointer.Capture(this);
        _hover = ShareAt(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        double share = ShareAt(e.GetPosition(this));
        _hover = IsPointerOver ? share : -1;
        InvalidateVisual();
        JumpAsked?.Invoke(share);
    }

    /// <summary>1 for the small text, more for the bigger.</summary>
    public double TextScale { get; set; } = 1;

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>The time played and the song's length - null while it is not known.</summary>
    public void Show(TimeSpan played, TimeSpan? length)
    {
        (_played, _length) = (played, length);
        double filled = length is { TotalSeconds: > 0 } total ? Math.Clamp(played / total, 0, 1) : 0;
        var texts = ((int)Math.Floor(played.TotalSeconds), length is { } l ? (int)Math.Floor(l.TotalSeconds) : -1, TextScale);
        double width = Math.Max(1, Bounds.Width);
        if (texts != _texts || Math.Abs((filled - _filled) * width) >= 0.5)
        {
            _filled = filled;
            if (texts != _texts)
            {
                _texts = texts;
                _left?.Dispose();
                _right?.Dispose();
                _left = null;
                _right = null;
            }
            InvalidateVisual();
        }
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    public override void Render(DrawingContext context)
    {
        double size = 12 * TextScale;
        _left ??= new TextLayout(Clock(_played), Mono, size, SidPlayerPalette.Brush(SidPlayerPalette.Title));
        _right ??= new TextLayout(_length is { } length ? Clock(length) : "--:--", Mono, size, SidPlayerPalette.Brush(SidPlayerPalette.Subtitle));

        double w = Bounds.Width, h = Bounds.Height, mid = h / 2;
        double column = TextWidth * size * 0.62;
        _left.Draw(context, new Point(column - _left.Width, mid - (_left.Height / 2)));
        _right.Draw(context, new Point(w - column, mid - (_right.Height / 2)));

        // The line between, rounded at its ends; filled in the accent as far as the song has got -
        // or, while a jump is on its way, as far as it goes. Where the pointer would jump to is
        // marked, and the line is a little thicker while it can be clicked.
        var (from, to) = Line();
        if (to <= from)
            return;
        double thick = _canJump && (_hover >= 0 || _dragging) ? 6 : 4;
        var track = new Rect(from, mid - (thick / 2), to - from, thick);
        context.DrawRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder), null, track, thick / 2, thick / 2);
        double filled = _jumping >= 0 ? _jumping : _dragging && _hover >= 0 ? _hover : _filled;
        if (_hover >= 0 && !_dragging && _jumping < 0)
        {
            double at = from + ((to - from) * _hover);
            context.DrawRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 70)), null,
                new Rect(Math.Min(at, from + ((to - from) * _filled)), mid - (thick / 2), Math.Abs(at - (from + ((to - from) * _filled))), thick), thick / 2, thick / 2);
        }
        if (filled > 0)
        {
            double end = from + ((to - from) * filled);
            context.DrawRectangle(SidPlayerPalette.Brush(SidPlayerPalette.VoiceColour(1)), null, new Rect(from, mid - (thick / 2), end - from, thick), thick / 2, thick / 2);
            double knob = _canJump && (_hover >= 0 || _dragging) ? 6 : 4;
            context.DrawEllipse(SidPlayerPalette.Brush(SidPlayerPalette.Title), null, new Point(end, mid), knob, knob);
        }
    }
}
