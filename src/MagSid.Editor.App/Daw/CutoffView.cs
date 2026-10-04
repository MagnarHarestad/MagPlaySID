using System.Drawing.Drawing2D;
using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The filter program's cutoff over time - the filter's counterpart to <see cref="EnvelopeView"/>.
///
/// The curve is not drawn from the step values but played: the program runs through the player's
/// own <see cref="FilterEngine"/> frame by frame, so a sweep's triangle, a setup step stopping it
/// and the cutoff staying put after the last step all look the way they will sound.
/// </summary>
internal sealed class CutoffView : Control
{
    private IReadOnlyList<FilterStep> _program = [];
    private int[] _cutoffs = [];
    private int _selected = -1;

    public CutoffView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.Inset;
    }

    /// <summary>Shows a program, with one of its steps marked, or -1 for none.</summary>
    public void SetProgram(IReadOnlyList<FilterStep> program, int selectedStep)
    {
        _program = [.. program];
        _cutoffs = Play(_program);
        _selected = selectedStep;
        Invalidate();
    }

    /// <summary>The cutoff each frame ends on, -1 before anything has been written. The program
    /// takes one frame per step; after it, a running sweep gets long enough to show one full
    /// triangle and a still cutoff a short tail.</summary>
    private static int[] Play(IReadOnlyList<FilterStep> program)
    {
        int sweep = 0;
        foreach (var step in program)
        {
            if (step.IsSetup)
                sweep = step.SweepRate;
        }

        int tail = sweep == 0 ? 16 : Math.Min(256, 512 / Math.Abs(sweep));
        int frames = Math.Clamp(program.Count + tail, 32, 384);

        var engine = new FilterEngine();
        engine.Claim(program);

        var cutoffs = new int[frames];
        int cutoff = -1;
        for (int frame = 0; frame < frames; frame++)
        {
            engine.StepFrame((register, value) =>
            {
                if (register == 0x16)
                    cutoff = value & 0xFF;
            });
            cutoffs[frame] = cutoff;
        }

        return cutoffs;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using (var border = new Pen(DawPalette.BorderSoft))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        int left = 8;
        int right = Width - 8;
        int top = 18;
        int bottom = Height - 8;
        if (right <= left || bottom <= top || _cutoffs.Length == 0)
            return;

        // Most programs live low in the range, and on a full $00-$FF scale their shape is a flat
        // line. The scale tops out a little above the highest cutoff instead, and says where.
        int highest = _cutoffs.Max();
        int ceiling = Math.Clamp(((highest * 9 / 8) + 0x0F) & ~0x0F, 0x10, 0xFF);

        float frameWidth = (right - left) / (float)_cutoffs.Length;
        float X(int frame) => left + (frame * frameWidth);
        float Y(int cutoff) => bottom - ((bottom - top) * (cutoff / (float)ceiling));

        using (var grid = new Pen(DawPalette.WithAlpha(DawPalette.Filter, 22)) { DashStyle = DashStyle.Dot })
            g.DrawLine(grid, left, top, right, top);

        // Where the program ends and the cutoff is left to itself - or to the sweep.
        using (var divider = new Pen(DawPalette.WithAlpha(DawPalette.Filter, 60)))
        {
            float end = X(_program.Count);
            if (_program.Count < _cutoffs.Length)
                g.DrawLine(divider, end, top - 4, end, bottom);

            if (_selected >= 0 && _selected < _program.Count)
            {
                using var highlight = new SolidBrush(DawPalette.WithAlpha(DawPalette.Filter, 34));
                g.FillRectangle(highlight, X(_selected), top - 4, Math.Max(2f, frameWidth), bottom - top + 4);
            }
        }

        // One value per frame, held for the frame - drawn as steps, since that is what the
        // register does.
        var curve = new List<PointF>();
        for (int frame = 0; frame < _cutoffs.Length; frame++)
        {
            if (_cutoffs[frame] < 0)
                continue;

            float y = Y(_cutoffs[frame]);
            curve.Add(new PointF(X(frame), y));
            curve.Add(new PointF(X(frame + 1), y));
        }

        if (curve.Count >= 2)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Filter, 28)))
            {
                PointF[] area = [.. curve, new PointF(curve[^1].X, bottom), new PointF(curve[0].X, bottom)];
                g.FillPolygon(fill, area);
            }

            using (var glow = new Pen(DawPalette.WithAlpha(DawPalette.Filter, 50), 3.5f))
                g.DrawLines(glow, curve.ToArray());
            using (var pen = new Pen(DawPalette.Filter, 1.7f))
                g.DrawLines(pen, curve.ToArray());

            g.SmoothingMode = SmoothingMode.Default;
        }

        using var font = new Font("Segoe UI", 6.75f, FontStyle.Bold);
        using var ink = new SolidBrush(DawPalette.WithAlpha(DawPalette.Filter, 150));
        g.DrawString("CUTOFF", font, ink, 8, 2);

        using var scaleFont = new Font("Consolas", 6.75f);
        using var scaleInk = new SolidBrush(DawPalette.TextFaint);
        g.DrawString($"${ceiling:X2}", scaleFont, scaleInk, 58, 2);

        using var summaryFont = new Font("Consolas", 6.75f);
        using var summaryInk = new SolidBrush(DawPalette.TextFaint);
        var farRight = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(Summary(), summaryFont, summaryInk, new RectangleF(0, 2, Width - 8, 12), farRight);
    }

    private string Summary()
    {
        var written = _cutoffs.Where(c => c >= 0).ToArray();
        if (written.Length == 0)
            return string.Empty;

        int sweep = _program.LastOrDefault(s => s.IsSetup).SweepRate;
        string range = $"${written.Min():X2}-${written.Max():X2}";
        string steps = _program.Count == 1 ? "1 step" : $"{_program.Count} steps";
        return sweep == 0 ? $"{range} · {steps}" : $"{range} · {steps} · sweep {sweep:+#;-#}";
    }
}
