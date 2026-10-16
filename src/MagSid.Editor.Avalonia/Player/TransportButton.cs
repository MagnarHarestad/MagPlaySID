using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MagSid.Editor.Avalonia.Player;

internal enum TransportGlyph
{
    Previous,
    Play,
    Pause,
    Next,
}

/// <summary>One transport control, drawn rather than imaged, so it is sharp at any scale.</summary>
internal sealed class TransportButton : Control
{
    private TransportGlyph _glyph;
    private bool _hovered;
    private bool _pressed;

    public TransportButton(TransportGlyph glyph)
    {
        _glyph = glyph;
        Width = 46;
        Height = 40;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event Action? Click;

    public TransportGlyph Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
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
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        bool click = _pressed && _hovered && IsEnabled;
        _pressed = false;
        InvalidateVisual();
        base.OnPointerReleased(e);
        if (click)
            Click?.Invoke();
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
            : _hovered ? SidPlayerPalette.ControlHover : SidPlayerPalette.Control;
        var brush = new SolidColorBrush(colour);

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
