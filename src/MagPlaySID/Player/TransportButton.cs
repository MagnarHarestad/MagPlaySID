using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;

namespace MagPlaySid.Player;

internal enum TransportGlyph
{
    Previous,
    Play,
    Pause,
    FastForward,
    Next,
    Repeat,
}

/// <summary>One transport control, drawn rather than imaged, so it is sharp at any scale.</summary>
internal sealed class TransportButton : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private TransportGlyph _glyph;
    private bool _hovered;
    private bool _pressed;
    private bool _lit;
    private string? _badge;
    private TextLayout? _badgeText;

    public TransportButton(TransportGlyph glyph)
    {
        _glyph = glyph;
        Width = 46;
        Height = 40;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event Action? Click;

    /// <summary>The button pressed, and let go - or the press lost: for what lasts as long as it is
    /// held, as fast-forward does.</summary>
    public event Action? Held;

    public event Action? LetGo;

    public TransportGlyph Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            InvalidateVisual();
        }
    }

    /// <summary>The main button: a round disc in the accent with the glyph cut out of it, as a
    /// player's play button is - not a glyph alone.</summary>
    public bool Round { get; init; }

    /// <summary>Grey until it is lit: a setting beside the controls, not one of them - repeat.</summary>
    public bool Quiet { get; init; }

    /// <summary>Drawn in the accent while what it stands for is on - fast-forward, say.</summary>
    public bool Lit
    {
        get => _lit;
        set
        {
            _lit = value;
            InvalidateVisual();
        }
    }

    /// <summary>A few characters in its corner while it is lit - the speed of a fast-forward.</summary>
    public string? Badge
    {
        get => _badge;
        set
        {
            if (_badge == value)
                return;
            _badge = value;
            _badgeText = value is { Length: > 0 }
                ? new TextLayout(value, Typeface.Default, 9, SidPlayerPalette.Brush(SidPlayerPalette.VoiceColour(1)))
                : null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _hovered = true;
        InvalidateVisual();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hovered = _pressed = false;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressed = true;
        InvalidateVisual();
        base.OnPointerPressed(e);
        if (IsEnabled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _held = true;
            Held?.Invoke();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        bool click = _pressed && _hovered && IsEnabled;
        _pressed = false;
        InvalidateVisual();
        base.OnPointerReleased(e);
        EndHold();
        if (click)
            Click?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndHold();
    }

    private bool _held;

    /// <summary>Let go wherever the pointer is - a hold ends with the press, not with the pointer
    /// leaving the button.</summary>
    private void EndHold()
    {
        if (!_held)
            return;
        _held = false;
        LetGo?.Invoke();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEnabledProperty)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // Transparent fill, so the whole box takes the pointer and not only the glyph.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var colour = !IsEnabled ? SidPlayerPalette.ControlDisabled
            : _lit ? SidPlayerPalette.VoiceColour(1)
            : _hovered ? SidPlayerPalette.ControlHover
            : Quiet ? SidPlayerPalette.Subtitle : SidPlayerPalette.Control;
        double w = Bounds.Width, h = Bounds.Height;
        double inset = _pressed ? 2 : 0;
        if (Round)
        {
            // The disc, lighter under the pointer; the glyph in the window's background on it.
            var accent = SidPlayerPalette.VoiceColour(1);
            var disc = !IsEnabled ? SidPlayerPalette.ControlDisabled
                : _hovered ? Color.FromRgb((byte)Math.Min(255, accent.R + 24), (byte)Math.Min(255, accent.G + 24), (byte)Math.Min(255, accent.B + 24)) : accent;
            double radius = (Math.Min(w, h) / 2) - 1 - inset;
            context.DrawEllipse(SidPlayerPalette.Brush(disc), null, new Point(w / 2, h / 2), radius, radius);
            colour = SidPlayerPalette.Background;
        }
        var brush = SidPlayerPalette.Brush(colour);
        double glyph = Round ? 0.4 : 0.56;
        var box = new Rect((w * (1 - glyph) / 2) + inset, (h * (1 - glyph) / 2) + inset, (w * glyph) - (inset * 2), (h * glyph) - (inset * 2));
        // A play triangle sits a little right of the middle, to look centred.
        if (Round && _glyph == TransportGlyph.Play)
            box = box.Translate(new Vector(w * 0.04, 0));

        switch (_glyph)
        {
            case TransportGlyph.Previous:
                Triangle(context, brush, box, pointingLeft: true);
                context.FillRectangle(brush, new Rect(box.Left - (box.Width * 0.22), box.Top, box.Width * 0.16, box.Height));
                break;
            case TransportGlyph.Next:
                Triangle(context, brush, box, pointingLeft: false);
                context.FillRectangle(brush, new Rect(box.Right + (box.Width * 0.06), box.Top, box.Width * 0.16, box.Height));
                break;
            case TransportGlyph.Play:
                Triangle(context, brush, box, pointingLeft: false);
                break;
            case TransportGlyph.FastForward:
                // Two triangles nose to tail, as a tape deck marks it.
                var half = new Rect(box.Left - (box.Width * 0.1), box.Top + (box.Height * 0.12), box.Width * 0.6, box.Height * 0.76);
                Triangle(context, brush, half, pointingLeft: false);
                Triangle(context, brush, half.Translate(new Vector(box.Width * 0.6, 0)), pointingLeft: false);
                break;
            case TransportGlyph.Pause:
                double bar = box.Width * 0.3;
                context.FillRectangle(brush, new Rect(box.Left, box.Top, bar, box.Height));
                context.FillRectangle(brush, new Rect(box.Right - bar, box.Top, bar, box.Height));
                break;
            case TransportGlyph.Repeat:
                Loop(context, colour, brush, box);
                break;
        }

        if (_lit && _badgeText is { } badge)
            badge.Draw(context, new Point(w - badge.Width - 1, h - badge.Height + 1));
    }

    /// <summary>Two arrows round a loop, as a player marks repeat: along the top to the right, and
    /// back along the bottom to the left.</summary>
    private static void Loop(DrawingContext context, Color colour, IBrush brush, Rect box)
    {
        var loop = box.Deflate(new Thickness(0, box.Height * 0.14));
        double round = loop.Height * 0.42, head = loop.Height * 0.36;
        var pen = SidPlayerPalette.Pen(colour, Math.Max(1.6, loop.Height * 0.15));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(loop.Right - head, loop.Top), false);
            path.LineTo(new Point(loop.Left + round, loop.Top));
            path.ArcTo(new Point(loop.Left, loop.Top + round), new Size(round, round), 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(loop.Left, loop.Bottom - round));
            path.EndFigure(false);
            path.BeginFigure(new Point(loop.Left + head, loop.Bottom), false);
            path.LineTo(new Point(loop.Right - round, loop.Bottom));
            path.ArcTo(new Point(loop.Right, loop.Bottom - round), new Size(round, round), 0, false, SweepDirection.CounterClockwise);
            path.LineTo(new Point(loop.Right, loop.Top + round));
            path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
        Triangle(context, brush, new Rect(loop.Right - head, loop.Top - (head / 2), head, head), pointingLeft: false);
        Triangle(context, brush, new Rect(loop.Left, loop.Bottom - (head / 2), head, head), pointingLeft: true);
    }

    private static void Triangle(DrawingContext context, IBrush brush, Rect box, bool pointingLeft)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            if (pointingLeft)
            {
                path.BeginFigure(new Point(box.Right, box.Top), true);
                path.LineTo(new Point(box.Right, box.Bottom));
                path.LineTo(new Point(box.Left, box.Top + (box.Height / 2)));
            }
            else
            {
                path.BeginFigure(new Point(box.Left, box.Top), true);
                path.LineTo(new Point(box.Left, box.Bottom));
                path.LineTo(new Point(box.Right, box.Top + (box.Height / 2)));
            }

            path.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }
}
