using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>What a click in the piano roll's grid does - the Cubase toolbox, one tool at a time.
/// </summary>
internal enum RollTool
{
    Select,
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
///
/// After the tools come the take's own switches: REC, red as a record button is, and CLICK,
/// PUNCH and PRE-ROLL in the view's cyan - the same shape, so they read as one group.
///
/// The arrange uses the same row for its own four tools, without the switches.
/// </summary>
internal sealed class RollToolbar : Control
{
    private const int ButtonWidth = 32;
    private const int ButtonHeight = 24;
    private const int Gap = 3;
    private const int Left0 = 10;
    private const int SwitchGap = 18;

    private enum Switch { Rec, Click, Punch, PreRoll }

    private static readonly (Switch Switch, string Label, int Width, string Help)[] Switches =
    [
        (Switch.Rec, "REC", 52, "Record into the selected clip's pattern from a MIDI or computer keyboard, while the song plays from there."),
        (Switch.Click, "CLICK", 62, "A click on every beat while recording - over the pre-roll bar when PRE-ROLL is on, otherwise a one-bar count-in first."),
        (Switch.Punch, "PUNCH", 66, "Record only between the locators: the left one is the punch-in, the right one the punch-out."),
        (Switch.PreRoll, "PRE-ROLL", 80, "Start the song a bar before the recording starts, so you are in time when it does. Nothing is written in that bar."),
    ];

    private static readonly Color RecRed = Color.FromArgb(0xF0, 0x3E, 0x4E);

    private static readonly (RollTool Tool, string Name, string Help)[] RollTools =
    [
        (RollTool.Select, "Object Selection", "Click a note to select it, Ctrl+click to add, drag a box to select several. Drag them to move, drag an end to resize. Ctrl+X cuts, Ctrl+C copies, Ctrl+V pastes at the row picked in the beat bar."),
        (RollTool.Draw, "Draw", "Click to write a note, drag to give it a length. Drag a note's right edge to resize it."),
        (RollTool.Erase, "Erase", "Click or drag across notes to delete them."),
        (RollTool.Trim, "Trim", "Click inside a note to cut off everything after that point."),
        (RollTool.Split, "Split", "Click inside a note to cut it in two there."),
        (RollTool.Glue, "Glue", "Click a note to join it to the next one."),
        (RollTool.Line, "Line", "Drag from one note to another to draw a run of notes between them."),
    ];

    private readonly (RollTool Tool, string Name, string Help)[] Tools;
    private readonly bool _switches;
    private readonly ToolTip _tips = new() { InitialDelay = 400, ReshowDelay = 100 };
    private RollTool _tool;
    private int _hovered = -1;
    private bool _recording, _click, _punch, _preRoll = true;

    /// <summary>REC pressed: true to start a take, false to stop it.</summary>
    public event Action<bool>? RecordToggled;

    /// <summary>Lit while a take runs - the host says when one starts and stops.</summary>
    public bool Recording
    {
        get => _recording;
        set
        {
            _recording = value;
            Invalidate();
        }
    }

    public bool ClickOn
    {
        get => _click;
        set
        {
            _click = value;
            Invalidate();
        }
    }

    public bool PunchOn
    {
        get => _punch;
        set
        {
            _punch = value;
            Invalidate();
        }
    }

    public bool PreRollOn
    {
        get => _preRoll;
        set
        {
            _preRoll = value;
            Invalidate();
        }
    }

    public RollToolbar()
        : this(RollTools, switches: true)
    {
    }

    /// <summary>A row of <paramref name="tools"/> - Draw chosen when it is one of them, else the
    /// first - with the take's switches after them when <paramref name="switches"/> is set. Without
    /// them it is only as wide and tall as its icons, to sit in a row of other buttons.</summary>
    public RollToolbar((RollTool Tool, string Name, string Help)[] tools, bool switches)
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Tools = tools;
        _switches = switches;
        _tool = tools.Any(t => t.Tool == RollTool.Draw) ? RollTool.Draw : tools[0].Tool;
        BackColor = DawPalette.PanelHeader;
        Height = ButtonHeight + (switches ? 8 : 2);
        if (!switches)
            Width = ButtonBounds(tools.Length - 1).Right + 2;
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

    /// <summary>The switches after the tools: index Tools.Length and on.</summary>
    private Rectangle SwitchBounds(int index)
    {
        int x = ButtonBounds(Tools.Length - 1).Right + SwitchGap;
        for (int i = 0; i < index; i++)
            x += Switches[i].Width + Gap + 3;
        return new Rectangle(x, (Height - ButtonHeight) / 2, Switches[index].Width, ButtonHeight);
    }

    private int ButtonAt(Point point)
    {
        for (int i = 0; i < Tools.Length; i++)
        {
            if (ButtonBounds(i).Contains(point))
                return i;
        }

        for (int i = 0; _switches && i < Switches.Length; i++)
        {
            if (SwitchBounds(i).Contains(point))
                return Tools.Length + i;
        }

        return -1;
    }

    private bool IsOn(Switch which) => which switch
    {
        Switch.Rec => _recording,
        Switch.Click => _click,
        Switch.Punch => _punch,
        _ => _preRoll,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = ButtonAt(e.Location);
        if (at != _hovered)
        {
            _hovered = at;
            Invalidate();
            _tips.SetToolTip(this, at < 0 ? null
                : at < Tools.Length ? $"{Tools[at].Name}\n{Tools[at].Help}"
                : $"{Switches[at - Tools.Length].Label}\n{Switches[at - Tools.Length].Help}");
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
        if (at >= 0 && at < Tools.Length)
        {
            Tool = Tools[at].Tool;
        }
        else if (at >= Tools.Length && e.Button == MouseButtons.Left)
        {
            switch (Switches[at - Tools.Length].Switch)
            {
                case Switch.Rec: RecordToggled?.Invoke(!_recording); break;
                case Switch.Click: ClickOn = !_click; break;
                case Switch.Punch: PunchOn = !_punch; break;
                case Switch.PreRoll: PreRollOn = !_preRoll; break;
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_switches)
        {
            using var line = new Pen(DawPalette.Border);
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }

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

        for (int i = 0; _switches && i < Switches.Length; i++)
            DrawSwitch(g, i);
    }

    /// <summary>A dim outline with its mark in the accent while off; filled with the accent, the
    /// mark and label white, while on.</summary>
    private void DrawSwitch(Graphics g, int index)
    {
        var (which, label, _, _) = Switches[index];
        var box = SwitchBounds(index);
        bool on = IsOn(which), hover = Tools.Length + index == _hovered;
        var accent = which == Switch.Rec ? RecRed : DawPalette.Oscillator;

        using (var fill = new SolidBrush(on ? accent : hover ? Color.FromArgb(0x22, 0x2B, 0x3E) : Color.FromArgb(0x10, 0x14, 0x1E)))
            g.FillRectangle(fill, box);
        using (var border = new Pen(on ? accent : DawPalette.WithAlpha(accent, hover ? 200 : 110)))
            g.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);

        var ink = on ? (which == Switch.Rec ? Color.White : DawPalette.ActiveInk) : accent;
        float cx = box.X + 11, cy = box.Y + box.Height / 2f;
        using (var mark = new SolidBrush(ink))
        using (var pen = new Pen(ink, 1.5f))
        {
            switch (which)
            {
                case Switch.Rec:
                    g.FillEllipse(mark, cx - 4, cy - 4, 8, 8);
                    break;
                case Switch.Click:
                    // A metronome: its case and the arm.
                    g.DrawPolygon(pen, new PointF[] { new(cx - 4, cy + 4.5f), new(cx - 1.5f, cy - 4.5f), new(cx + 1.5f, cy - 4.5f), new(cx + 4, cy + 4.5f) });
                    g.DrawLine(pen, cx, cy + 2.5f, cx + 3.5f, cy - 5);
                    break;
                case Switch.PreRoll:
                    // A bar line with the play arrow coming up to it.
                    g.DrawLine(pen, cx + 4.5f, cy - 5, cx + 4.5f, cy + 5);
                    g.FillPolygon(mark, new PointF[] { new(cx - 4.5f, cy - 4), new(cx + 2, cy), new(cx - 4.5f, cy + 4) });
                    break;
                default:
                    // Brackets round the stretch that is written.
                    g.DrawLines(pen, new PointF[] { new(cx - 2, cy - 4.5f), new(cx - 4.5f, cy - 4.5f), new(cx - 4.5f, cy + 4.5f), new(cx - 2, cy + 4.5f) });
                    g.DrawLines(pen, new PointF[] { new(cx + 2, cy - 4.5f), new(cx + 4.5f, cy - 4.5f), new(cx + 4.5f, cy + 4.5f), new(cx + 2, cy + 4.5f) });
                    break;
            }
        }

        using var font = new Font("Segoe UI", 7f, FontStyle.Bold);
        TextRenderer.DrawText(g, label, font, new Rectangle(box.X + 19, box.Y, box.Width - 21, box.Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
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
