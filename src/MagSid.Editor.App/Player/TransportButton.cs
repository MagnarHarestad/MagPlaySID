using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Player;

internal enum TransportGlyph
{
    Previous,
    Play,
    Pause,
    Next,
}

/// <summary>
/// One transport control, drawn rather than imaged: three filled glyphs at any size, with no assets
/// to ship and nothing to go blurry on a scaled display.
/// </summary>
internal sealed class TransportButton : Control
{
    private TransportGlyph _glyph;
    private bool _hovered;
    private bool _pressed;

    public TransportButton(TransportGlyph glyph)
    {
        _glyph = glyph;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, false);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = SidPlayerPalette.Background;
        Cursor = Cursors.Hand;
        Size = new Size(46, 40);
    }

    public TransportGlyph Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color colour = !Enabled
            ? SidPlayerPalette.ControlDisabled
            : _hovered ? SidPlayerPalette.ControlHover : SidPlayerPalette.Control;

        // A pressed glyph shrinks a touch rather than moving, which at this size reads better than
        // a one-pixel nudge.
        float inset = _pressed ? 2f : 0f;
        var box = new RectangleF(
            (Width * 0.22f) + inset, (Height * 0.22f) + inset,
            (Width * 0.56f) - (inset * 2), (Height * 0.56f) - (inset * 2));

        using var brush = new SolidBrush(colour);
        switch (_glyph)
        {
            case TransportGlyph.Previous:
                FillTriangle(g, brush, box, pointingLeft: true);
                g.FillRectangle(brush, box.Left - (box.Width * 0.22f), box.Top, box.Width * 0.16f, box.Height);
                break;

            case TransportGlyph.Next:
                FillTriangle(g, brush, box, pointingLeft: false);
                g.FillRectangle(brush, box.Right + (box.Width * 0.06f), box.Top, box.Width * 0.16f, box.Height);
                break;

            case TransportGlyph.Play:
                FillTriangle(g, brush, box, pointingLeft: false);
                break;

            case TransportGlyph.Pause:
                float barWidth = box.Width * 0.3f;
                g.FillRectangle(brush, box.Left, box.Top, barWidth, box.Height);
                g.FillRectangle(brush, box.Right - barWidth, box.Top, barWidth, box.Height);
                break;
        }
    }

    private static void FillTriangle(Graphics g, Brush brush, RectangleF box, bool pointingLeft)
    {
        PointF[] points = pointingLeft
            ?
            [
                new PointF(box.Right, box.Top),
                new PointF(box.Right, box.Bottom),
                new PointF(box.Left, box.Top + (box.Height / 2f)),
            ]
            :
            [
                new PointF(box.Left, box.Top),
                new PointF(box.Left, box.Bottom),
                new PointF(box.Right, box.Top + (box.Height / 2f)),
            ];

        g.FillPolygon(brush, points);
    }
}
