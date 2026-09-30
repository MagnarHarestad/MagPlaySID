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

        int ringSize = Math.Min(Width, Height - 22) - 4;
        var ring = new RectangleF(
            (Width - ringSize) / 2f, 2f, ringSize, ringSize);

        using (var track = new Pen(DawPalette.BorderSoft, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, ring, StartAngle, SweepAngle);

        float sweep = Math.Max(1f, SweepAngle * Fraction);
        Color accent = _hovered || _dragging ? ControlPaint.Light(Accent, 0.25f) : Accent;

        // A wide, faint pass under the line reads as glow without a blur.
        using (var halo = new Pen(DawPalette.WithAlpha(accent, 70), 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(halo, ring, StartAngle, sweep);
        using (var value = new Pen(accent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(value, ring, StartAngle, sweep);

        float cx = ring.X + (ring.Width / 2f);
        float cy = ring.Y + (ring.Height / 2f);
        float bodyRadius = (ring.Width / 2f) - 5f;

        using (var body = new SolidBrush(DawPalette.Inset))
            g.FillEllipse(body, cx - bodyRadius, cy - bodyRadius, bodyRadius * 2, bodyRadius * 2);
        using (var edge = new Pen(Focused ? accent : DawPalette.BorderSoft))
            g.DrawEllipse(edge, cx - bodyRadius, cy - bodyRadius, bodyRadius * 2, bodyRadius * 2);

        double angle = (StartAngle + sweep) * Math.PI / 180.0;
        float px = cx + (float)(Math.Cos(angle) * bodyRadius * 0.72);
        float py = cy + (float)(Math.Sin(angle) * bodyRadius * 0.72);

        using (var pointer = new Pen(accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pointer, cx, cy, px, py);
        using (var hub = new SolidBrush(accent))
            g.FillEllipse(hub, cx - 2f, cy - 2f, 4f, 4f);

        g.SmoothingMode = SmoothingMode.Default;

        // Listening, or already bound. Both belong on the knob itself: a dialog listing bindings
        // would answer the question in the one place the eye is not.
        if (Learning)
        {
            using var halo = new Pen(DawPalette.Effect, 2f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawArc(halo, ring, StartAngle, SweepAngle);
            g.SmoothingMode = SmoothingMode.Default;
        }
        else if (BoundController is { } controller)
        {
            using var ccFont = new Font("Segoe UI", 6f);
            using var ccInk = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 190));
            g.DrawString($"CC{controller}", ccFont, ccInk, 2, 1);
        }

        using var captionFont = new Font("Segoe UI", 6.75f, FontStyle.Bold);
        using var valueFont = new Font("Consolas", 7.5f);
        using var dim = new SolidBrush(DawPalette.TextDim);
        using var ink = new SolidBrush(DawPalette.Text);

        var centred = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(
            Learning ? "LEARN..." : Caption,
            captionFont,
            Learning ? new SolidBrush(DawPalette.Effect) : dim,
            new RectangleF(0, ring.Bottom + 2, Width, 12),
            centred);
        g.DrawString(Format(_value), valueFont, ink, new RectangleF(0, ring.Bottom + 12, Width, 12), centred);
    }
}
