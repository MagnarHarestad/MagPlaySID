using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The four waveform tabs, over a drawing of whichever is chosen.
///
/// The byte is a SID control register: bit 4 triangle, 5 saw, 6 pulse, 7 noise, bit 0 the gate.
/// More than one can be set at once - the combined waveforms are a real and much-used SID trick -
/// so these are four independent latches rather than a radio group, and the drawing shows what the
/// combination actually is.
///
/// The gate bit is not a tab: it is what starts a note, and an instrument whose base waveform has it
/// clear would simply never sound. It is forced on, quietly, the same way the assembler's emitter
/// does it.
/// </summary>
internal sealed class WaveformSelector : Control
{
    private const int TabHeight = 28;

    private static readonly (string Label, byte Bit)[] Tabs =
    [
        ("TRI", 0x10),
        ("SAW", 0x20),
        ("PUL", 0x40),
        ("NOI", 0x80),
    ];

    private byte _waveform = 0x41;
    private int _hovered = -1;

    public WaveformSelector()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.Panel;
        Cursor = Cursors.Hand;
    }

    public event Action<byte>? WaveformChanged;

    public Color Accent { get; set; } = DawPalette.Oscillator;

    public byte Waveform
    {
        get => _waveform;
        set
        {
            byte gated = (byte)(value | 0x01);
            if (_waveform == gated)
                return;

            _waveform = gated;
            Invalidate();
        }
    }

    private int TabAt(int x, int y)
    {
        if (y > TabHeight || Width <= 0)
            return -1;

        int index = x * Tabs.Length / Width;
        return Math.Clamp(index, 0, Tabs.Length - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = TabAt(e.X, e.Y);
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
        int at = TabAt(e.X, e.Y);
        if (at < 0)
            return;

        byte bit = Tabs[at].Bit;
        byte next = (byte)(_waveform ^ bit);

        // Clearing the last waveform bit would leave an instrument that cannot make a sound, so the
        // last one standing refuses to go out.
        if ((next & 0xF0) == 0)
            return;

        _waveform = (byte)(next | 0x01);
        Invalidate();
        WaveformChanged?.Invoke(_waveform);

        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        float tabWidth = Width / (float)Tabs.Length;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var (label, bit) = Tabs[i];
            bool on = (_waveform & bit) != 0;
            var box = new RectangleF(i * tabWidth, 0, tabWidth - 2, TabHeight);

            using (var fill = new SolidBrush(on
                       ? DawPalette.WithAlpha(Accent, 40)
                       : _hovered == i ? Color.FromArgb(0x1D, 0x26, 0x38) : Color.FromArgb(0x11, 0x16, 0x1F)))
                g.FillRectangle(fill, box);

            using (var border = new Pen(on ? Accent : DawPalette.BorderSoft))
                g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);

            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var ink = new SolidBrush(on ? Accent : DawPalette.TextDim);
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, ink, box, centred);
        }

        var preview = new Rectangle(0, TabHeight + 6, Width - 1, Height - TabHeight - 8);
        if (preview.Height <= 4)
            return;

        using (var fill = new SolidBrush(DawPalette.Inset))
            g.FillRectangle(fill, preview);
        using (var border = new Pen(DawPalette.BorderSoft))
            g.DrawRectangle(border, preview);

        DrawWaveform(g, preview);

        using var hexFont = new Font("Consolas", 7f);
        using var hexInk = new SolidBrush(DawPalette.TextFaint);
        var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString($"${_waveform:X2}", hexFont, hexInk,
            new RectangleF(0, preview.Y + 2, preview.Width - 5, 12), far);
    }

    /// <summary>Sums whichever waveforms are selected. Combined waveforms on a real SID are an AND
    /// of the oscillator's own outputs and not a sum, so this is an impression rather than a
    /// simulation - enough to tell four choices apart, which is all a tab strip needs.</summary>
    private void DrawWaveform(Graphics g, Rectangle box)
    {
        int steps = Math.Max(16, box.Width);
        var points = new PointF[steps];
        float mid = box.Y + (box.Height / 2f);
        float amplitude = (box.Height / 2f) - 5f;

        var random = new Random(7);
        float noiseHeld = 0f;

        for (int i = 0; i < steps; i++)
        {
            float phase = i / (float)steps * 3f;
            float fractional = phase - (float)Math.Floor(phase);
            float value = 0f;
            int voices = 0;

            if ((_waveform & 0x10) != 0)
            {
                value += fractional < 0.5f ? (fractional * 4f) - 1f : 3f - (fractional * 4f);
                voices++;
            }

            if ((_waveform & 0x20) != 0)
            {
                value += (fractional * 2f) - 1f;
                voices++;
            }

            if ((_waveform & 0x40) != 0)
            {
                value += fractional < 0.5f ? 1f : -1f;
                voices++;
            }

            if ((_waveform & 0x80) != 0)
            {
                if (i % 5 == 0)
                    noiseHeld = ((float)random.NextDouble() * 2f) - 1f;
                value += noiseHeld;
                voices++;
            }

            if (voices > 1)
                value /= voices;

            points[i] = new PointF(box.X + i, mid - (value * amplitude));
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var glow = new Pen(DawPalette.WithAlpha(DawPalette.Envelope, 45), 3f))
            g.DrawLines(glow, points);
        using (var pen = new Pen(DawPalette.Envelope, 1.5f))
            g.DrawLines(pen, points);
        g.SmoothingMode = SmoothingMode.Default;
    }
}
