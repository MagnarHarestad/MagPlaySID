namespace MagSid.Editor.App.Daw;

/// <summary>A titled panel with an accent strip, so a screen full of knobs still says which knobs
/// belong together. <see cref="Body"/> is where content goes; the header paints itself.</summary>
internal sealed class SectionPanel : Panel
{
    private const int HeaderHeight = 28;

    private readonly string _title;
    private readonly Color _accent;

    public SectionPanel(string title, Color accent)
    {
        _title = title;
        _accent = accent;

        BackColor = DawPalette.Panel;
        Padding = new Padding(1, HeaderHeight, 1, 1);
        DoubleBuffered = true;

        Body = new Panel { Dock = DockStyle.Fill, BackColor = DawPalette.Panel };
        Controls.Add(Body);
    }

    public Panel Body { get; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(DawPalette.Panel);

        using (var header = new SolidBrush(DawPalette.PanelHeader))
            g.FillRectangle(header, 0, 0, Width, HeaderHeight);
        using (var rule = new Pen(DawPalette.Border))
        {
            g.DrawLine(rule, 0, HeaderHeight, Width, HeaderHeight);
            g.DrawRectangle(rule, 0, 0, Width - 1, Height - 1);
        }

        using (var font = new Font("Segoe UI", 7.5f, FontStyle.Bold))
        using (var ink = new SolidBrush(DawPalette.HeaderInk(_accent)))
            g.DrawString(_title, font, ink, 11, 8);

        // The lamp, as on the reference: the section is present and running.
        using var lamp = new SolidBrush(_accent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillEllipse(lamp, Width - 20, (HeaderHeight / 2) - 4, 8, 8);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;

        base.OnPaint(e);
    }
}
