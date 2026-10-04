using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>What a step draws. The glyph is the point: a wavetable should read as noise-then-pulse
/// at a glance, without anyone decoding $09 $41.</summary>
internal enum StepGlyph
{
    Triangle,
    Saw,
    Pulse,
    Noise,
    Flat,
    Rise,
    Fall,
    Hold,
    Root,
    Setup,
}

/// <summary>One cell of a program. <paramref name="Marker"/> separates a real step from something
/// that only says where the program goes next - LOOP, HOLD, ROOT - which costs bytes differently and
/// should not look like a frame of sound.</summary>
internal sealed record ProgramStep(string Label, StepGlyph Glyph, Color Accent, bool Marker = false);

/// <summary>
/// One of an instrument's three programs - wavetable, note program, filter - as a row of steps
/// rather than a column of hex.
///
/// Each cell carries its own glyph, so the shape of the program is visible before any value is read.
/// The hex stays printed under the glyph: this view is not trying to hide the format, only to stop
/// it being the single way in.
/// </summary>
internal sealed class ProgramLaneControl : Control
{
    private const int CellWidth = 54;
    private const int CellGap = 3;

    private IReadOnlyList<ProgramStep> _steps = [];
    private int _selected = -1;
    private int _playing = -1;
    private int _hovered = -1;

    public ProgramLaneControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.Panel;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    /// <summary>The step clicked, or -1 for the trailing add cell.</summary>
    public event Action<int>? StepSelected;

    /// <summary>A real step was double-clicked, with where its cell is - for a host that wants to
    /// put an editor over it. Not raised for a marker cell, which has no value to type.</summary>
    public event Action<int, Rectangle>? StepDoubleClicked;

    /// <summary>Nudge a step's value. Arrow keys and the wheel, never typing - the same choice the
    /// knobs make, so the whole view is edited one way.</summary>
    public event Action<int, int>? StepNudged;

    /// <summary>The trailing "+" was used.</summary>
    public event Action? StepAppended;

    public event Action<int>? StepRemoved;

    public Color Accent { get; set; } = DawPalette.Envelope;

    public string Title { get; set; } = string.Empty;

    /// <summary>A sentence saying what the program's bytes mean, since the format is the thing a
    /// glyph cannot say on its own.</summary>
    public string Hint { get; set; } = string.Empty;

    public void SetSteps(IReadOnlyList<ProgramStep> steps)
    {
        _steps = steps;
        if (_selected >= steps.Count)
            _selected = -1;
        Invalidate();
    }

    /// <summary>Which step playback has reached, or -1. The same idea as the retro view's tint,
    /// which is how an unreachable tail of a program becomes visible rather than guessed at.</summary>
    public int PlayingStep
    {
        get => _playing;
        set
        {
            if (_playing == value)
                return;

            _playing = value;
            Invalidate();
        }
    }

    public int SelectedStep => _selected;

    /// <summary>Lets go of the selected step without announcing it - for when the lane is given a
    /// different instrument's program, whose step of the same number is a different step.</summary>
    public void ClearSelection()
    {
        _selected = -1;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = CellAt(e.X, e.Y);
        if (at != _hovered)
        {
            _hovered = at;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        int at = CellAt(e.X, e.Y);
        if (at < -1)
            return;

        Focus();

        if (at == -1)
        {
            StepAppended?.Invoke();
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            StepRemoved?.Invoke(at);
            return;
        }

        // A second click on the selected step lets go of it - the way back to editing the
        // instrument rather than one step of it.
        _selected = at == _selected ? -1 : at;
        Invalidate();
        StepSelected?.Invoke(_selected);

        base.OnMouseClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Delete or Keys.Insert or Keys.Escape;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            // Moving the selection with the arrows is a selection like any other, so it is
            // announced the same way a click is - whatever edits the selected step follows it.
            case Keys.Left:
                _selected = Math.Max(0, _selected - 1);
                Invalidate();
                StepSelected?.Invoke(_selected);
                e.Handled = true;
                break;

            case Keys.Right:
                _selected = Math.Min(_steps.Count - 1, _selected + 1);
                Invalidate();
                StepSelected?.Invoke(_selected);
                e.Handled = true;
                break;

            case Keys.Escape when _selected >= 0:
                _selected = -1;
                Invalidate();
                StepSelected?.Invoke(-1);
                e.Handled = true;
                break;

            case Keys.Up when _selected >= 0:
                StepNudged?.Invoke(_selected, e.Shift ? 16 : 1);
                e.Handled = true;
                break;

            case Keys.Down when _selected >= 0:
                StepNudged?.Invoke(_selected, e.Shift ? -16 : -1);
                e.Handled = true;
                break;

            case Keys.Delete when _selected >= 0:
                StepRemoved?.Invoke(_selected);
                e.Handled = true;
                break;

            case Keys.Insert:
                StepAppended?.Invoke();
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_selected >= 0)
            StepNudged?.Invoke(_selected, Math.Sign(e.Delta) * (ModifierKeys.HasFlag(Keys.Shift) ? 16 : 1));
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int at = CellAt(e.X, e.Y);
        if (e.Button == MouseButtons.Left && at >= 0 && !_steps[at].Marker)
        {
            // The first click of the pair let go of the step if it was already selected; the
            // step being typed into is the selected one.
            if (_selected != at)
            {
                _selected = at;
                Invalidate();
                StepSelected?.Invoke(at);
            }

            StepDoubleClicked?.Invoke(at, CellBounds(at));
        }

        base.OnMouseDoubleClick(e);
    }

    /// <summary>Where a step's cell is drawn.</summary>
    public Rectangle CellBounds(int step)
    {
        int top = HeaderHeight + CellGap;
        return new Rectangle(CellGap + (step * (CellWidth + CellGap)), top, CellWidth, Math.Max(0, Height - top - CellGap));
    }

    /// <summary>The step under a point, -1 for the add cell, -2 for neither.</summary>
    private int CellAt(int x, int y)
    {
        if (y < HeaderHeight)
            return -2;

        int index = (x - CellGap) / (CellWidth + CellGap);
        if (index < 0)
            return -2;

        return index < _steps.Count ? index : index == _steps.Count ? -1 : -2;
    }

    private const int HeaderHeight = 26;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using (var border = new Pen(DawPalette.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var rule = new Pen(DawPalette.Border))
            g.DrawLine(rule, 1, HeaderHeight, Width - 2, HeaderHeight);

        using (var titleFont = new Font("Segoe UI", 7f, FontStyle.Bold))
        using (var hintFont = new Font("Segoe UI", 7f))
        using (var titleInk = new SolidBrush(DawPalette.HeaderInk(Accent)))
        using (var hintInk = new SolidBrush(DawPalette.TextFaint))
        {
            g.DrawString(Title, titleFont, titleInk, 10, 7);
            float titleWidth = g.MeasureString(Title, titleFont).Width;
            g.DrawString(Hint, hintFont, hintInk, 14 + titleWidth, 7);

            string count = Focused
                ? "↑↓ value  ·  Del removes  ·  Ins adds"
                : _steps.Count == 1 ? "1 step" : $"{_steps.Count} steps";
            var far = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString(count, hintFont, hintInk, new RectangleF(0, 7, Width - 10, 14), far);
        }

        int top = HeaderHeight + CellGap;
        int height = Height - top - CellGap;
        if (height <= 0)
            return;

        for (int i = 0; i < _steps.Count; i++)
        {
            int x = CellGap + (i * (CellWidth + CellGap));
            if (x > Width)
                break;

            DrawCell(g, _steps[i], i, new Rectangle(x, top, CellWidth, height));
        }

        int addX = CellGap + (_steps.Count * (CellWidth + CellGap));
        if (addX + CellWidth <= Width)
            DrawAddCell(g, new Rectangle(addX, top, CellWidth, height));
    }

    private void DrawCell(Graphics g, ProgramStep step, int index, Rectangle cell)
    {
        bool selected = index == _selected;
        bool playing = index == _playing;

        Color fill = playing
            ? DawPalette.WithAlpha(step.Accent, 38)
            : selected ? Color.FromArgb(0x1D, 0x26, 0x38) : Color.FromArgb(0x11, 0x16, 0x1F);

        using (var background = new SolidBrush(fill))
            g.FillRectangle(background, cell);

        Color edge = selected ? step.Accent : playing ? DawPalette.WithAlpha(step.Accent, 120) : DawPalette.BorderSoft;
        using (var border = new Pen(edge))
            g.DrawRectangle(border, cell);

        using (var indexFont = new Font("Consolas", 6.5f))
        using (var indexInk = new SolidBrush(DawPalette.TextFaint))
            g.DrawString(index.ToString("00"), indexFont, indexInk, cell.X + 3, cell.Y + 2);

        var glyphBox = new Rectangle(cell.X + 8, cell.Y + 16, cell.Width - 16, 16);
        DrawGlyph(g, step, glyphBox);

        using var labelFont = new Font("Consolas", 7f);
        using var labelInk = new SolidBrush(step.Marker ? step.Accent : DawPalette.Text);
        var centred = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(step.Label, labelFont, labelInk,
            new RectangleF(cell.X, cell.Bottom - 15, cell.Width, 14), centred);
    }

    private static void DrawGlyph(Graphics g, ProgramStep step, Rectangle box)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(step.Accent, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        float l = box.Left, r = box.Right, t = box.Top, b = box.Bottom;
        float mid = (t + b) / 2f;
        float w = r - l;

        switch (step.Glyph)
        {
            case StepGlyph.Triangle:
                g.DrawLines(pen, [new PointF(l, b), new PointF(l + (w / 4), t), new PointF(l + (w / 2), b), new PointF(l + (3 * w / 4), t), new PointF(r, b)]);
                break;

            case StepGlyph.Saw:
                g.DrawLines(pen, [new PointF(l, b), new PointF(l + (w / 2) - 1, t), new PointF(l + (w / 2) - 1, b), new PointF(r, t)]);
                break;

            case StepGlyph.Pulse:
                g.DrawLines(pen, [new PointF(l, b), new PointF(l, t), new PointF(l + (w / 2), t), new PointF(l + (w / 2), b), new PointF(r, b)]);
                break;

            case StepGlyph.Noise:
                g.DrawLines(pen, [
                    new PointF(l, mid), new PointF(l + (w * 0.15f), t), new PointF(l + (w * 0.3f), b),
                    new PointF(l + (w * 0.45f), t + 2), new PointF(l + (w * 0.6f), b - 2),
                    new PointF(l + (w * 0.8f), t), new PointF(r, mid)]);
                break;

            case StepGlyph.Flat:
                g.DrawLine(pen, l, mid, r, mid);
                break;

            case StepGlyph.Rise:
                g.DrawLine(pen, l, b, r, t);
                break;

            case StepGlyph.Fall:
                g.DrawLine(pen, l, t, r, b);
                break;

            case StepGlyph.Hold:
                g.DrawLine(pen, l, mid, l + (w * 0.6f), mid);
                g.DrawLine(pen, l + (w * 0.6f), t, l + (w * 0.6f), b);
                break;

            case StepGlyph.Root:
                g.DrawLine(pen, l, mid, r, mid);
                g.DrawLine(pen, (l + r) / 2f, t, (l + r) / 2f, b);
                break;

            case StepGlyph.Setup:
                g.DrawRectangle(pen, l, t + 2, w, (b - t) - 4);
                g.DrawLine(pen, l + (w / 2), t + 2, l + (w / 2), b - 2);
                break;
        }

        g.SmoothingMode = SmoothingMode.Default;
    }

    private void DrawAddCell(Graphics g, Rectangle cell)
    {
        bool hovered = _hovered == -1;

        using (var border = new Pen(hovered ? Accent : DawPalette.BorderSoft) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(border, cell);

        using var font = new Font("Segoe UI", 12f);
        using var ink = new SolidBrush(hovered ? Accent : DawPalette.TextFaint);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("+", font, ink, cell, centred);
    }
}
