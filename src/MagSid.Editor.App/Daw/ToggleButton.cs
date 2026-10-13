namespace MagSid.Editor.App.Daw;

/// <summary>A latching button - CONT, NO HR - drawn so the lit state is unmistakable at a glance,
/// which a checkbox on a dark panel is not.</summary>
internal sealed class ToggleButton : Control
{
    private bool _checked;
    private bool _hovered;

    public ToggleButton(string caption)
    {
        Text = caption;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = DawPalette.Panel;
        Size = new Size(76, 26);
        Cursor = Cursors.Hand;
    }

    public event Action<bool>? CheckedChanged;

    public Color Accent { get; set; } = DawPalette.Envelope;

    /// <summary>A REC button: red when off too, a dot before the caption, and filled red when on.</summary>
    public bool IsRecord { get; set; }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;

            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(_checked);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        Checked = !Checked;
        base.OnMouseClick(e);
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
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var box = new Rectangle(0, 0, Width - 1, Height - 1);

        if (IsRecord)
        {
            PaintRecord(g, box);
            return;
        }

        using (var fill = new SolidBrush(_checked
                   ? DawPalette.WithAlpha(Accent, 38)
                   : _hovered ? Color.FromArgb(0x1D, 0x26, 0x38) : Color.FromArgb(0x16, 0x1D, 0x2B)))
            g.FillRectangle(fill, box);

        using (var border = new Pen(_checked ? Accent : DawPalette.Border))
            g.DrawRectangle(border, box);

        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var ink = new SolidBrush(_checked ? Accent : DawPalette.TextDim);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, font, ink, box, centred);
    }

    private static readonly Color RecRed = Color.FromArgb(0xF0, 0x3E, 0x4E);

    private void PaintRecord(Graphics g, Rectangle box)
    {
        using (var fill = new SolidBrush(_checked ? RecRed : _hovered ? Color.FromArgb(0x1D, 0x26, 0x38) : Color.FromArgb(0x10, 0x14, 0x1E)))
            g.FillRectangle(fill, box);
        using (var border = new Pen(_checked ? RecRed : DawPalette.WithAlpha(RecRed, 110)))
            g.DrawRectangle(border, box);

        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        var size = g.MeasureString(Text, font);
        float width = 8 + 4 + size.Width;
        float left = box.X + ((box.Width - width) / 2f);
        float middle = box.Y + (box.Height / 2f);

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var dot = new SolidBrush(_checked ? Color.White : RecRed))
            g.FillEllipse(dot, left, middle - 4, 8, 8);
        g.SmoothingMode = smooth;

        using var ink = new SolidBrush(_checked ? Color.White : DawPalette.WithAlpha(RecRed, 220));
        g.DrawString(Text, font, ink, left + 12, middle - (size.Height / 2f));
    }
}
