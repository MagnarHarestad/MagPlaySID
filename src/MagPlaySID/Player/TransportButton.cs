using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MagPlaySid.Player;

internal enum TransportGlyph
{
    Previous,
    Play,
    Pause,
    FastForward,
    Next,
}

/// <summary>One transport control, drawn rather than imaged, so it is sharp at any scale.</summary>
internal sealed class TransportButton : Control
{
    private TransportGlyph _glyph;
    private bool _hovered;
    private bool _pressed;
    private bool _lit;

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
            : _hovered ? SidPlayerPalette.ControlHover : SidPlayerPalette.Control;
        var brush = SidPlayerPalette.Brush(colour);

        double w = Bounds.Width, h = Bounds.Height;
        double inset = _pressed ? 2 : 0;
        var box = new Rect((w * 0.22) + inset, (h * 0.22) + inset, (w * 0.56) - (inset * 2), (h * 0.56) - (inset * 2));

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
        }
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
