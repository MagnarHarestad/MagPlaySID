using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The ADSR envelope, drawn from the same two bytes the retro view spells as A/D and S/R.
///
/// The SID's rates are not linear and not in any unit a listener thinks in: attack 0 is 2ms and
/// attack 15 is eight seconds, spread over a curve of its own. The widths here come from the real
/// tables, so a slow attack looks slow - the whole point being that a number you cannot feel becomes
/// a shape you can.
/// </summary>
internal sealed class EnvelopeView : Control
{
    /// <summary>Attack times in milliseconds for rates 0-15, from the SID's own table.</summary>
    private static readonly double[] AttackMs =
        [2, 8, 16, 24, 38, 56, 68, 80, 100, 250, 500, 800, 1000, 3000, 5000, 8000];

    /// <summary>Decay and release run three times slower than attack at the same rate - one
    /// counter, three prescalers.</summary>
    private static double DecayMs(int rate) => AttackMs[rate] * 3.0;

    private int _attack;
    private int _decay;
    private int _sustain;
    private int _release;

    public EnvelopeView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.Inset;
    }

    public void SetEnvelope(int attack, int decay, int sustain, int release)
    {
        _attack = Math.Clamp(attack, 0, 15);
        _decay = Math.Clamp(decay, 0, 15);
        _sustain = Math.Clamp(sustain, 0, 15);
        _release = Math.Clamp(release, 0, 15);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using (var border = new Pen(DawPalette.BorderSoft))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        int left = 8;
        int right = Width - 8;
        int top = 10;
        int bottom = Height - 10;
        if (right <= left || bottom <= top)
            return;

        // The three timed stages share the width in proportion to their real durations, with the
        // sustain leg taking whatever is left - so a long attack visibly eats the envelope.
        double attack = AttackMs[_attack];
        double decay = DecayMs(_decay);
        double release = DecayMs(_release);
        double timed = attack + decay + release;
        double span = Math.Max(timed * 1.45, 1.0);

        float width = right - left;
        float attackWidth = (float)(width * (attack / span));
        float decayWidth = (float)(width * (decay / span));
        float releaseWidth = (float)(width * (release / span));
        float sustainWidth = Math.Max(6f, width - attackWidth - decayWidth - releaseWidth);

        float sustainY = bottom - ((bottom - top) * (_sustain / 15f));

        float x0 = left;
        float x1 = x0 + attackWidth;
        float x2 = x1 + decayWidth;
        float x3 = x2 + sustainWidth;
        float x4 = Math.Min(right, x3 + releaseWidth);

        PointF[] curve =
        [
            new PointF(x0, bottom),
            new PointF(x1, top),
            new PointF(x2, sustainY),
            new PointF(x3, sustainY),
            new PointF(x4, bottom),
        ];

        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Envelope, 28)))
        {
            PointF[] area = [.. curve, new PointF(x4, bottom), new PointF(x0, bottom)];
            g.FillPolygon(fill, area);
        }

        // Where one stage hands over to the next, so the four numbers can be found on the shape.
        using (var divider = new Pen(DawPalette.WithAlpha(DawPalette.Envelope, 60)))
        {
            foreach (float x in new[] { x1, x2, x3 })
                g.DrawLine(divider, x, top - 4, x, bottom);
        }

        using (var glow = new Pen(DawPalette.WithAlpha(DawPalette.Envelope, 50), 3.5f))
            g.DrawLines(glow, curve);
        using (var pen = new Pen(DawPalette.Envelope, 1.7f))
            g.DrawLines(pen, curve);

        g.SmoothingMode = SmoothingMode.Default;

        using var font = new Font("Segoe UI", 6.75f, FontStyle.Bold);
        using var ink = new SolidBrush(DawPalette.WithAlpha(DawPalette.Envelope, 150));
        g.DrawString("ENVELOPE", font, ink, 8, 2);

        using var timeFont = new Font("Consolas", 6.75f);
        using var timeInk = new SolidBrush(DawPalette.TextFaint);
        string summary = $"{Describe(attack)} · {Describe(decay)} · {_sustain:X} · {Describe(release)}";
        var right2 = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(summary, timeFont, timeInk, new RectangleF(0, 2, Width - 8, 12), right2);
    }

    private static string Describe(double milliseconds) =>
        milliseconds >= 1000 ? $"{milliseconds / 1000:0.#}s" : $"{milliseconds:0}ms";
}
