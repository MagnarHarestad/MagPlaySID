using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The four waveform tabs and Silence, over a drawing of whichever is chosen.
///
/// The byte is a SID control register: bit 4 triangle, 5 saw, 6 pulse, 7 noise, bit 0 the gate.
/// More than one can be set at once - the combined waveforms are a real and much-used SID trick -
/// and the drawing shows what the combination actually is.
///
/// Left-click picks one waveform on its own, right-click adds or removes one. Choosing a waveform
/// is the common intent and combining the rare one, so the plain click is the plain meaning.
///
/// Sync and ring sit beside them as their own group. They are bits 1 and 2 of the same register
/// rather than waveforms, so either button just flips them: there is nothing to be exclusive
/// about. Both modulate against the voice before this one - voice 1's partner is voice 3 - and
/// ring is only audible on triangle, which the player leaves to the ear rather than enforcing.
///
/// The gate bit is not a tab: it is what starts a note, and an instrument whose base waveform has it
/// clear would simply never sound. It is forced on, quietly, the same way the assembler's emitter
/// does it.
///
/// Silence is $09: no waveform, the test bit holding the oscillator at zero, gate on - the usual
/// first wavetable step of a drum. It is the whole byte rather than a bit to combine, and leaving
/// it for a waveform drops the test bit, which would otherwise keep that waveform silent too.
/// </summary>
internal sealed class WaveformSelector : Control
{
    private const int TabHeight = 28;
    private const int ModifierWidth = 86;
    private const int ModifierGap = 10;

    /// <summary>No waveform, test and gate on.</summary>
    public const byte Silence = 0x09;

    /// <summary>A bit of 0 is Silence, which is a whole byte rather than a bit.</summary>
    private static readonly (string Label, byte Bit)[] Tabs =
    [
        ("Triangle", 0x10),
        ("Saw", 0x20),
        ("Pulse", 0x40),
        ("Noise", 0x80),
        ("Silence", 0x00),
    ];

    private bool IsSilent => (_waveform & 0xF0) == 0;

    private bool IsOn(byte bit) => bit == 0 ? IsSilent : (_waveform & bit) != 0;

    /// <summary>Sync and ring modulation: low-nibble bits, not waveforms. Both work against the
    /// voice before this one - voice 1's partner is voice 3 - and ring is only audible on
    /// triangle, which is why they are a separate group rather than two more tabs.</summary>
    private static readonly (string Label, byte Bit)[] Modifiers =
    [
        ("Sync", 0x02),
        ("Ring", 0x04),
    ];

    private byte _waveform = 0x41;
    private int _hovered = -1;
    private int _hoveredModifier = -1;

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

    /// <summary>Where the waveform tabs end and the modifier group begins. The modifiers take a
    /// fixed strip on the right so they stay the same size whatever the panel does, and the four
    /// waveforms and Silence share what is left.</summary>
    private int WaveformStripWidth =>
        Math.Max(Tabs.Length * 40, Width - (Modifiers.Length * ModifierWidth) - ModifierGap);

    private int TabAt(int x, int y)
    {
        if (y > TabHeight || Width <= 0 || x >= WaveformStripWidth)
            return -1;

        int index = x * Tabs.Length / WaveformStripWidth;
        return Math.Clamp(index, 0, Tabs.Length - 1);
    }

    private int ModifierAt(int x, int y)
    {
        if (y > TabHeight)
            return -1;

        int offset = x - WaveformStripWidth - ModifierGap;
        if (offset < 0)
            return -1;

        int index = offset / ModifierWidth;
        return index < Modifiers.Length ? index : -1;
    }

    private RectangleF ModifierBox(int index) => new(
        WaveformStripWidth + ModifierGap + (index * ModifierWidth), 0, ModifierWidth - 2, TabHeight);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = TabAt(e.X, e.Y);
        int modifier = ModifierAt(e.X, e.Y);

        if (at != _hovered || modifier != _hoveredModifier)
        {
            _hovered = at;
            _hoveredModifier = modifier;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = -1;
        _hoveredModifier = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    /// <summary>Left picks this waveform alone, right toggles it into or out of the combination.
    /// The low nibble carries through untouched either way: gate, sync and ring modulation are not
    /// waveforms, and choosing one should not quietly clear them.</summary>
    protected override void OnMouseClick(MouseEventArgs e)
    {
        // A modifier is not a waveform, so it has nothing to be exclusive about: either button
        // simply flips the bit, and the waveform underneath is untouched.
        int modifier = ModifierAt(e.X, e.Y);
        if (modifier >= 0 && e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            Apply((byte)(_waveform ^ Modifiers[modifier].Bit));
            base.OnMouseClick(e);
            return;
        }

        int at = TabAt(e.X, e.Y);
        if (at >= 0 && e.Button is MouseButtons.Left or MouseButtons.Right)
        {
            byte bit = Tabs[at].Bit;
            if (bit == 0)
            {
                Apply(Silence);
                base.OnMouseClick(e);
                return;
            }

            // Out of silence, the test bit goes with it - and there is nothing to combine with.
            byte low = (byte)(_waveform & (IsSilent ? 0x07 : 0x0F));
            byte next = e.Button == MouseButtons.Right && !IsSilent
                ? (byte)(_waveform ^ bit)
                : (byte)(low | bit);

            // Clearing the last waveform bit would leave an instrument that cannot make a sound, so
            // the last one standing refuses to go out.
            if (((next | 0x01) & 0xF0) != 0)
            {
                Apply(next);
            }
        }

        base.OnMouseClick(e);
    }

    private void Apply(byte next)
    {
        byte gated = (byte)(next | 0x01);
        if (gated == _waveform)
        {
            return;
        }

        _waveform = gated;
        Invalidate();
        WaveformChanged?.Invoke(_waveform);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        float tabWidth = WaveformStripWidth / (float)Tabs.Length;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var (label, bit) = Tabs[i];
            DrawTab(g, new RectangleF(i * tabWidth, 0, tabWidth - 2, TabHeight), label,
                IsOn(bit), _hovered == i, Accent);
        }

        // In the effects colour, not the oscillator's: these two are modulation against the
        // neighbouring voice, and colouring them like the waveform would suggest they were one.
        for (int i = 0; i < Modifiers.Length; i++)
        {
            var (label, bit) = Modifiers[i];
            DrawTab(g, ModifierBox(i), label,
                (_waveform & bit) != 0, _hoveredModifier == i, DawPalette.Effect);
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

        // A gesture nobody can see is a gesture nobody finds.
        g.DrawString("RIGHT-CLICK TO COMBINE", hexFont, hexInk, preview.X + 5, preview.Y + 2);
    }

    private static void DrawTab(Graphics g, RectangleF box, string label, bool on, bool hovered, Color accent)
    {
        using (var fill = new SolidBrush(on
                   ? DawPalette.WithAlpha(accent, 40)
                   : hovered ? Color.FromArgb(0x1D, 0x26, 0x38) : Color.FromArgb(0x11, 0x16, 0x1F)))
            g.FillRectangle(fill, box);

        using (var border = new Pen(on ? accent : DawPalette.BorderSoft))
            g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);

        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var ink = new SolidBrush(on ? accent : DawPalette.TextDim);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(label, font, ink, box, centred);
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
