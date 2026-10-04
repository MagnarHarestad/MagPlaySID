using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>What a click in the piano roll's grid does - the Cubase toolbox, one tool at a time.
/// </summary>
internal enum RollTool
{
    Select,
    Range,
    Draw,
    Erase,
    Trim,
    Split,
    Glue,
    Line,
}

/// <summary>
/// The piano roll's tools as a row of icons, Cubase-style: one is always chosen, and it decides
/// what a click in the grid means. Painted rather than taken from image files, so the icons follow
/// the view's own colours and stay sharp at any scale.
///
/// Each button names itself in a tooltip, since an icon is a reminder of something you already
/// know and a name is how you learn it.
/// </summary>
internal sealed class RollToolbar : Control
{
    private const int ButtonWidth = 32;
    private const int ButtonHeight = 24;
    private const int Gap = 3;
    private const int Left0 = 10;

    private static readonly (RollTool Tool, string Name, string Help)[] Tools =
    [
        (RollTool.Select, "Object Selection", "Click a note to select it, Ctrl+click to add to the selection, drag across the grid to select several. Drag selected notes to move them."),
        (RollTool.Range, "Range Selection", "Drag across beats to select a range. Delete silences it, Shift+C copies it, Shift+V pastes it."),
        (RollTool.Draw, "Draw", "Click to write a note, drag to give it a length. Drag a note's right edge to resize it."),
        (RollTool.Erase, "Erase", "Click or drag across notes to delete them."),
        (RollTool.Trim, "Trim", "Click inside a note to cut off everything after that point."),
        (RollTool.Split, "Split", "Click inside a note to cut it in two there."),
        (RollTool.Glue, "Glue", "Click a note to join it to the next one."),
        (RollTool.Line, "Line", "Drag from one note to another to draw a run of notes between them."),
    ];

    private readonly ToolTip _tips = new() { InitialDelay = 400, ReshowDelay = 100 };
    private RollTool _tool = RollTool.Draw;
    private int _hovered = -1;

    public RollToolbar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.PanelHeader;
        Height = ButtonHeight + 8;
        Cursor = Cursors.Hand;
    }

    public event Action<RollTool>? ToolChanged;

    public RollTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;

            _tool = value;
            Invalidate();
            ToolChanged?.Invoke(value);
        }
    }

    private Rectangle ButtonBounds(int index) =>
        new(Left0 + (index * (ButtonWidth + Gap)), (Height - ButtonHeight) / 2, ButtonWidth, ButtonHeight);

    private int ButtonAt(Point point)
    {
        for (int i = 0; i < Tools.Length; i++)
        {
            if (ButtonBounds(i).Contains(point))
                return i;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = ButtonAt(e.Location);
        if (at != _hovered)
        {
            _hovered = at;
            Invalidate();
            _tips.SetToolTip(this, at >= 0 ? $"{Tools[at].Name}\n{Tools[at].Help}" : null);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int at = ButtonAt(e.Location);
        if (at >= 0)
            Tool = Tools[at].Tool;

        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using (var line = new Pen(DawPalette.Border))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (int i = 0; i < Tools.Length; i++)
        {
            var box = ButtonBounds(i);
            bool on = Tools[i].Tool == _tool;
            bool hover = i == _hovered;

            using (var fill = new SolidBrush(on
                       ? DawPalette.WithAlpha(DawPalette.Oscillator, 60)
                       : hover ? Color.FromArgb(0x22, 0x2B, 0x3E) : Color.FromArgb(0x12, 0x17, 0x22)))
                g.FillRectangle(fill, box);

            using (var edge = new Pen(on ? DawPalette.Oscillator : DawPalette.BorderSoft))
                g.DrawRectangle(edge, box.X, box.Y, box.Width - 1, box.Height - 1);

            Color ink = on ? DawPalette.Oscillator : hover ? DawPalette.Text : DawPalette.TextDim;
            var glyph = new RectangleF(box.X + ((box.Width - 16) / 2f), box.Y + ((box.Height - 16) / 2f), 16, 16);
            DrawIcon(g, Tools[i].Tool, glyph, ink);
        }

        // The chosen tool's name beside the row, so it is never a guess which one is live.
        var current = Tools.First(t => t.Tool == _tool);
        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var nameInk = new SolidBrush(DawPalette.TextDim);
        var after = ButtonBounds(Tools.Length - 1);
        g.DrawString(current.Name.ToUpperInvariant(), font, nameInk, after.Right + 14, (Height - 12) / 2f);
    }

    private static void DrawIcon(Graphics g, RollTool tool, RectangleF r, Color ink)
    {
        using var pen = new Pen(ink, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(ink);
        float x = r.X, y = r.Y;

        switch (tool)
        {
            case RollTool.Select:
                // An arrow pointer.
                g.FillPolygon(brush, new PointF[]
                {
                    new(x + 3, y + 1), new(x + 3, y + 14), new(x + 6.5f, y + 10.5f),
                    new(x + 9, y + 15), new(x + 11, y + 14), new(x + 8.5f, y + 9.5f), new(x + 13, y + 9.5f),
                });
                break;

            case RollTool.Range:
                // Two brackets with the span between them.
                g.DrawLines(pen, new PointF[] { new(x + 4, y + 2), new(x + 1.5f, y + 2), new(x + 1.5f, y + 14), new(x + 4, y + 14) });
                g.DrawLines(pen, new PointF[] { new(x + 12, y + 2), new(x + 14.5f, y + 2), new(x + 14.5f, y + 14), new(x + 12, y + 14) });
                using (var span = new SolidBrush(Color.FromArgb(120, ink)))
                    g.FillRectangle(span, x + 4, y + 6, 8, 4);
                break;

            case RollTool.Draw:
                // A pencil, point down to the left.
                g.DrawPolygon(pen, new PointF[] { new(x + 12, y + 1.5f), new(x + 14.5f, y + 4), new(x + 5, y + 13.5f), new(x + 2.5f, y + 11) });
                g.FillPolygon(brush, new PointF[] { new(x + 2.5f, y + 11), new(x + 5, y + 13.5f), new(x + 1.5f, y + 14.5f) });
                g.DrawLine(pen, x + 10, y + 3.5f, x + 12.5f, y + 6);
                break;

            case RollTool.Erase:
                // An eraser on its side, the rubbing end shaded.
                var body = new PointF[] { new(x + 6, y + 2.5f), new(x + 14.5f, y + 9), new(x + 9.5f, y + 14.5f), new(x + 1.5f, y + 8) };
                g.DrawPolygon(pen, body);
                g.FillPolygon(brush, new PointF[] { new(x + 1.5f, y + 8), new(x + 5, y + 5.2f), new(x + 13, y + 11.5f), new(x + 9.5f, y + 14.5f) });
                break;

            case RollTool.Trim:
                // A blade cutting the end off a bar.
                g.DrawRectangle(pen, x + 1.5f, y + 9, 9, 4);
                g.DrawLine(pen, x + 11, y + 1.5f, x + 11, y + 15);
                g.FillPolygon(brush, new PointF[] { new(x + 11, y + 1.5f), new(x + 15, y + 4), new(x + 11, y + 6.5f) });
                break;

            case RollTool.Split:
                // Scissors.
                g.DrawEllipse(pen, x + 1.5f, y + 9.5f, 4.5f, 4.5f);
                g.DrawEllipse(pen, x + 10, y + 9.5f, 4.5f, 4.5f);
                g.DrawLine(pen, x + 5.3f, y + 10, x + 12.5f, y + 1.5f);
                g.DrawLine(pen, x + 10.7f, y + 10, x + 3.5f, y + 1.5f);
                break;

            case RollTool.Glue:
                // A tube of glue with a drop at the nozzle.
                g.DrawRectangle(pen, x + 1.5f, y + 6, 9, 6);
                g.DrawLines(pen, new PointF[] { new(x + 10.5f, y + 7.5f), new(x + 13, y + 8.5f), new(x + 10.5f, y + 10.5f) });
                g.FillEllipse(brush, x + 12.5f, y + 11.5f, 3, 3.5f);
                g.DrawLine(pen, x + 4, y + 6, x + 4, y + 12);
                break;

            case RollTool.Line:
                // A rising line through three notes.
                g.DrawLine(pen, x + 2, y + 13.5f, x + 14, y + 2.5f);
                g.FillRectangle(brush, x + 0.5f, y + 12, 3.5f, 3);
                g.FillRectangle(brush, x + 6.3f, y + 6.5f, 3.5f, 3);
                g.FillRectangle(brush, x + 12, y + 1, 3.5f, 3);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();

        base.Dispose(disposing);
    }
}
