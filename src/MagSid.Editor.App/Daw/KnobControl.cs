using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// A rotary control: a 270-degree ring from 7 o'clock round to 5, with the value arc over it and a
/// pointer.
///
/// Drag up to raise and down to lower, the way every plugin does it - never by the angle of the
/// pointer to the cursor, which needs the mouse to travel round the knob and makes fine work
/// impossible. Hold Shift for fine steps and double-click to return to the default.
///
/// The value stays an integer throughout, because every value this edits is a byte or a nibble of
/// one. The knob's job is to make a byte visible, not to introduce a resolution the SID does not
/// have.
/// </summary>
internal sealed class KnobControl : Control
{
    private const int DragPixelsForFullRange = 180;
    private const float StartAngle = 135f;
    private const float SweepAngle = 270f;

    private int _value;
    private int _minimum;
    private int _maximum = 15;
    private bool _dragging;
    private Point _dragOrigin;
    private int _dragStartValue;
    private bool _hovered;

    public KnobControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = DawPalette.Panel;
        Size = new Size(58, 74);
        Cursor = Cursors.SizeNS;
        TabStop = true;
    }

    public event Action<int>? ValueChanged;

    /// <summary>The knob is waiting for a controller to move. The host listens for the next one and
    /// binds it here.</summary>
    public event Action<KnobControl>? MidiLearnRequested;

    public event Action<KnobControl>? MidiForgetRequested;

    /// <summary>
    /// Names this knob in the saved bindings. Stable across releases and independent of where the
    /// knob sits on screen - a binding keyed by position would move to a different parameter the
    /// first time the layout changed.
    /// </summary>
    public string BindingKey { get; set; } = string.Empty;

    /// <summary>The controller bound to this knob, or null. Shown on the knob so a surface full of
    /// encoders can be read back without opening anything.</summary>
    public int? BoundController { get; set; }

    /// <summary>Waiting for a controller to move. Drawn, because a knob that is listening and one
    /// that is not have to be told apart at a glance.</summary>
    public bool Learning { get; set; }

    public Color Accent { get; set; } = DawPalette.Oscillator;

    /// <summary>The caption under the ring - ATT, CUTOFF, and so on.</summary>
    public string Caption { get; set; } = string.Empty;

    /// <summary>Formats the number printed under the caption. Hex for a raw byte, decimal for a
    /// count, signed for a sweep - the retro view's own spelling in every case, so the two views
    /// never disagree about what a value is called.</summary>
    public Func<int, string> Format { get; set; } = value => value.ToString();

    /// <summary>Where a double-click returns to.</summary>
    public int DefaultValue { get; set; }

    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            Value = _value;
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            Value = _value;
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _minimum, _maximum);
            if (clamped == _value)
                return;

            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(_value);
        }
    }

    /// <summary>Sets the value without telling anyone - for loading an instrument into the view,
    /// where firing the change event would write the value straight back onto the song and mark it
    /// dirty for having been looked at.</summary>
    public void SetValueSilently(int value)
    {
        _value = Math.Clamp(value, _minimum, _maximum);
        Invalidate();
    }

    private void ShowMidiMenu(Point at)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem(
            Learning ? "Stop learning" : "Learn MIDI",
            null,
            (_, _) => MidiLearnRequested?.Invoke(this)));

        var forget = new ToolStripMenuItem(
            BoundController is { } controller ? $"Forget CC {controller}" : "Not bound",
            null,
            (_, _) => MidiForgetRequested?.Invoke(this))
        {
            Enabled = BoundController is not null,
        };
        menu.Items.Add(forget);

        DarkTheme.Apply(menu);
        menu.Show(this, at);
    }

    private float Fraction => _maximum > _minimum
        ? (_value - _minimum) / (float)(_maximum - _minimum)
        : 0f;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && BindingKey.Length > 0)
        {
            ShowMidiMenu(e.Location);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragOrigin = e.Location;
            _dragStartValue = _value;
            Focus();
            Capture = true;
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            // Up is more. Screen y grows downward, hence the negation.
            int travel = _dragOrigin.Y - e.Y;
            float perPixel = (_maximum - _minimum) / (float)DragPixelsForFullRange;
            if (ModifierKeys.HasFlag(Keys.Shift))
                perPixel /= 5f;

            Value = _dragStartValue + (int)Math.Round(travel * perPixel);
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        Capture = false;
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        Value = DefaultValue;
        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int step = ModifierKeys.HasFlag(Keys.Shift) ? 1 : Math.Max(1, (_maximum - _minimum) / 32);
        Value += Math.Sign(e.Delta) * step;
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

    protected override bool IsInputKey(Keys keyData) => keyData is
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = e.Shift ? 1 : Math.Max(1, (_maximum - _minimum) / 32);

        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Right: Value += step; e.Handled = true; break;
            case Keys.Down or Keys.Left: Value -= step; e.Handled = true; break;
            case Keys.Home: Value = _minimum; e.Handled = true; break;
            case Keys.End: Value = _maximum; e.Handled = true; break;
        }

        base.OnKeyDown(e);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Inset well inside the control: the glow passes are wide, and a halo clipped by the
        // control's own edge reads as a drawing mistake rather than as light.
        int ringSize = Math.Min(Width, Height - 22) - 10;
        var ring = new RectangleF((Width - ringSize) / 2f, (Height - 22 - ringSize) / 2f, ringSize, ringSize);

        Color accent = _hovered || _dragging ? ControlPaint.Light(Accent, 0.25f) : Accent;
        float sweep = Math.Max(1f, SweepAngle * Fraction);

        DrawLedRing(g, ring, accent, sweep);
        DrawBody(g, ring, accent);
        DrawPointer(g, ring, sweep);

        if (Learning)
        {
            using var halo = new Pen(DawPalette.Effect, 2f);
            g.DrawArc(halo, ring, StartAngle, SweepAngle);
        }

        g.SmoothingMode = SmoothingMode.Default;

        if (!Learning && BoundController is { } controller)
        {
            using var ccFont = new Font("Segoe UI", 6f);
            using var ccInk = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 190));
            g.DrawString($"CC{controller}", ccFont, ccInk, 2, 1);
        }

        DrawCaption(g, ring);
    }

    /// <summary>
    /// The lit ring. GDI+ has no blur, so the arc is stroked several times at falling alpha and
    /// rising width, which is what makes an LED look like light spilling onto the panel rather
    /// than like a drawn line. The unlit track stays nearly as dark as the panel, as it is on real
    /// hardware where the ring is only visible where it is on.
    /// </summary>
    private static void DrawLedRing(Graphics g, RectangleF ring, Color accent, float sweep)
    {
        using (var track = new Pen(Color.FromArgb(0x1C, 0x21, 0x30), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(track, ring, StartAngle, SweepAngle);
        }

        foreach (var (width, alpha) in new[] { (9.5f, 20), (6.5f, 38), (4.2f, 78) })
        {
            using var glow = new Pen(DawPalette.WithAlpha(accent, alpha), width)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawArc(glow, ring, StartAngle, sweep);
        }

        using (var core = new Pen(accent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(core, ring, StartAngle, sweep);
        }

        // An LED is hottest in its middle, and this thin near-white pass down the centre of the lit
        // arc is the difference between a ring that looks lit and one that looks painted.
        using (var hot = new Pen(Blend(accent, Color.White, 0.6f), 0.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(hot, ring, StartAngle, sweep);
        }
    }

    /// <summary>The cap: lit from above, so a vertical gradient with a bright rim along the top
    /// edge and a dark one along the bottom, over a shadow that lifts it off the panel. Three arcs
    /// and a gradient are the whole of the moulded-plastic look.</summary>
    private void DrawBody(Graphics g, RectangleF ring, Color accent)
    {
        float cx = ring.X + (ring.Width / 2f);
        float cy = ring.Y + (ring.Height / 2f);
        float radius = (ring.Width / 2f) - 4f;
        var body = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);

        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
        {
            g.FillEllipse(shadow, body.X, body.Y + 2.5f, body.Width, body.Height);
        }

        // The gradient rectangle is a pixel taller than the body so the brush's own edge never
        // lands on the ellipse, where it would show as a hard band.
        using (var fill = new LinearGradientBrush(
                   new RectangleF(body.X, body.Y - 1f, body.Width, body.Height + 2f),
                   Color.FromArgb(0x35, 0x3A, 0x47),
                   Color.FromArgb(0x11, 0x14, 0x1C),
                   90f))
        {
            g.FillEllipse(fill, body);
        }

        using (var top = new Pen(Color.FromArgb(0x52, 0x59, 0x6B), 1.2f))
        {
            g.DrawArc(top, body, 200f, 140f);
        }

        using (var bottom = new Pen(Color.FromArgb(0x07, 0x09, 0x0F), 1.2f))
        {
            g.DrawArc(bottom, body, 20f, 140f);
        }

        // Focus is the one state that has to be visible without moving the mouse, and the rim is
        // the only part of the cap not already spoken for.
        if (Focused)
        {
            using var focus = new Pen(DawPalette.WithAlpha(accent, 170), 1.2f);
            g.DrawEllipse(focus, body);
        }
    }

    /// <summary>The groove: a dark line with a light one riding on it, from a little way out of the
    /// centre to just short of the edge.</summary>
    private static void DrawPointer(Graphics g, RectangleF ring, float sweep)
    {
        float cx = ring.X + (ring.Width / 2f);
        float cy = ring.Y + (ring.Height / 2f);
        float radius = (ring.Width / 2f) - 4f;

        double angle = (StartAngle + sweep) * Math.PI / 180.0;
        float dx = (float)Math.Cos(angle);
        float dy = (float)Math.Sin(angle);

        float inner = radius * 0.18f;
        float outer = radius * 0.86f;

        using (var groove = new Pen(Color.FromArgb(0x05, 0x06, 0x0B), 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(groove, cx + (dx * inner), cy + (dy * inner) + 0.7f, cx + (dx * outer), cy + (dy * outer) + 0.7f);
        }

        using (var mark = new Pen(Color.FromArgb(0xC9, 0xCF, 0xDB), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(mark, cx + (dx * inner), cy + (dy * inner), cx + (dx * outer), cy + (dy * outer));
        }
    }

    private void DrawCaption(Graphics g, RectangleF ring)
    {
        using var captionFont = new Font("Segoe UI", 6.75f, FontStyle.Bold);
        using var valueFont = new Font("Consolas", 7.5f);
        using var dim = new SolidBrush(DawPalette.TextDim);
        using var learning = new SolidBrush(DawPalette.Effect);
        using var ink = new SolidBrush(DawPalette.Text);

        var centred = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(
            Learning ? "LEARN..." : Caption,
            captionFont,
            Learning ? learning : dim,
            new RectangleF(0, ring.Bottom + 3, Width, 12),
            centred);
        g.DrawString(Format(_value), valueFont, ink, new RectangleF(0, ring.Bottom + 13, Width, 12), centred);
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        from.R + (int)((to.R - from.R) * amount),
        from.G + (int)((to.G - from.G) * amount),
        from.B + (int)((to.B - from.B) * amount));
}
