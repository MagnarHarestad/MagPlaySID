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
///
/// Each step is one frame of the curve and can be dragged up or down (Shift: fine). The drag is
/// played live here and handed over once, when the button comes up.
/// </summary>
internal sealed class CutoffView : Control
{
    private IReadOnlyList<FilterStep> _program = [];
    private int[] _cutoffs = [];
    private int _selected = -1;
    private int _sweep;
    private (int Frame, int Cutoff)? _playhead;

    private int _hoverStep = -1;
    private int _dragStep = -1;
    private int _dragStartY;
    private int _dragStartCutoff;
    private int _dragCutoff;
    private int _dragCeiling;
    private double _dragPerPixel;

    public CutoffView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DawPalette.Inset;
    }

    /// <summary>A step grabbed with the mouse, before it is dragged.</summary>
    public event Action<int>? StepPicked;

    /// <summary>A step's new cutoff, raised once when its drag ends - and only if it moved.</summary>
    public event Action<int, int>? CutoffDragged;

    /// <summary>Shows a program, with one of its steps marked, or -1 for none.</summary>
    public void SetProgram(IReadOnlyList<FilterStep> program, int selectedStep)
    {
        _program = [.. program];
        if (_dragStep >= _program.Count)
            _dragStep = -1;
        _cutoffs = Play(Dragged());
        _selected = selectedStep;
        _sweep = SweepOf(_program);
        Invalidate();
    }

    /// <summary>Where the program is while the song plays: the frame counted from its first step,
    /// and the cutoff the SID was actually given then. Null hides the marker.</summary>
    public void SetPlayhead((int Frame, int Cutoff)? at)
    {
        if (_playhead == at)
            return;

        _playhead = at;
        Invalidate();
    }

    /// <summary>The program with the step being dragged at its dragged cutoff.</summary>
    private IReadOnlyList<FilterStep> Dragged()
    {
        if (_dragStep < 0)
            return _program;

        var program = _program.ToList();
        program[_dragStep] = program[_dragStep].WithCutoff(_dragCutoff);
        return program;
    }

    /// <summary>The plot area and the scale's top. While a step is dragged the scale only grows,
    /// when the step goes past the top - shrinking under the mouse would make it run away.</summary>
    private (int Left, int Right, int Top, int Bottom, int Ceiling) Plot()
    {
        int highest = _cutoffs.Length > 0 ? _cutoffs.Max() : 0;

        // Most programs live low in the range, and on a full $00-$FF scale their shape is a flat
        // line. The scale tops out a little above the highest cutoff instead, and says where.
        int ceiling = Math.Clamp(((highest * 9 / 8) + 0x0F) & ~0x0F, 0x10, 0xFF);
        if (_dragStep >= 0)
            ceiling = Math.Max(ceiling, _dragCeiling);

        return (8, Width - 8, 18, Height - 8, ceiling);
    }

    /// <summary>The step under an x position: the step frame it is over, as long as the point is
    /// over the program rather than the tail after it. -1 for none.</summary>
    private int StepAt(int x)
    {
        var (left, right, _, _, _) = Plot();
        if (_program.Count == 0 || _cutoffs.Length == 0 || right <= left)
            return -1;

        float frameWidth = (right - left) / (float)_cutoffs.Length;
        float programEnd = left + (_program.Count * frameWidth);
        if (x < left - 4 || x > programEnd + 4)
            return -1;

        return Math.Clamp((int)((x - left) / frameWidth), 0, _program.Count - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int step = e.Button == MouseButtons.Left ? StepAt(e.X) : -1;
        if (step < 0)
            return;

        // Picked first: the host shows the step, which hands this graph its program again.
        StepPicked?.Invoke(step);
        if (step >= _program.Count)
            return;

        var (_, _, top, bottom, ceiling) = Plot();
        _dragCeiling = ceiling;
        _dragPerPixel = ceiling / (double)Math.Max(1, bottom - top);
        _dragStep = step;
        _dragStartY = e.Y;
        _dragStartCutoff = _program[step].Cutoff;
        _dragCutoff = _dragStartCutoff;
        _cutoffs = Play(Dragged());
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragStep >= 0)
        {
            double perPixel = _dragPerPixel;
            if ((ModifierKeys & Keys.Shift) != 0)
                perPixel = Math.Min(perPixel, 0.25);

            int cutoff = Math.Clamp(
                _dragStartCutoff + (int)Math.Round((_dragStartY - e.Y) * perPixel),
                FilterStep.MinCutoff, FilterStep.MaxCutoff);
            if (cutoff != _dragCutoff)
            {
                _dragCutoff = cutoff;
                _cutoffs = Play(Dragged());
                Invalidate();
            }

            return;
        }

        int hover = StepAt(e.X);
        Cursor = hover >= 0 ? Cursors.SizeNS : Cursors.Default;
        if (hover != _hoverStep)
        {
            _hoverStep = hover;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragStep < 0)
            return;

        int step = _dragStep;
        int cutoff = _dragCutoff;
        bool moved = cutoff != _dragStartCutoff;
        _dragStep = -1;
        Capture = false;
        _cutoffs = Play(_program);
        Invalidate();

        if (moved)
            CutoffDragged?.Invoke(step, cutoff);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        // Lost mid-drag (a dialog, Alt+Tab): nothing is written.
        if (!Capture && _dragStep >= 0)
        {
            _dragStep = -1;
            _cutoffs = Play(_program);
            Invalidate();
        }

        base.OnMouseCaptureChanged(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverStep >= 0)
        {
            _hoverStep = -1;
            Invalidate();
        }
    }

    /// <summary>The sweep left running when the program ends - the last setup step's.</summary>
    private static int SweepOf(IReadOnlyList<FilterStep> program)
    {
        int sweep = 0;
        foreach (var step in program)
        {
            if (step.IsSetup)
                sweep = step.SweepRate;
        }

        return sweep;
    }

    /// <summary>The graph frame a played frame lands on. Past the end, a running sweep goes round
    /// its period again when the graph holds a whole one; anything else stays on the last frame,
    /// where the cutoff has settled.</summary>
    private int GraphFrame(int frame)
    {
        if (frame < _cutoffs.Length)
            return frame;

        int period = _sweep == 0 ? 0 : 512 / Math.Abs(_sweep);
        int tail = _cutoffs.Length - _program.Count;
        return period > 0 && period <= tail
            ? _program.Count + ((frame - _program.Count) % period)
            : _cutoffs.Length - 1;
    }

    /// <summary>The cutoff each frame ends on, -1 before anything has been written. The program
    /// takes one frame per step; after it, a running sweep gets long enough to show one full
    /// triangle and a still cutoff a short tail.</summary>
    private static int[] Play(IReadOnlyList<FilterStep> program)
    {
        int sweep = SweepOf(program);
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

        var (left, right, top, bottom, ceiling) = Plot();
        if (right <= left || bottom <= top || _cutoffs.Length == 0)
            return;

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

        // The step under the mouse, or being dragged: a handle on its value, and the value.
        int handle = _dragStep >= 0 ? _dragStep : _hoverStep;
        if (handle >= 0 && handle < _program.Count && handle < _cutoffs.Length && _cutoffs[handle] >= 0)
        {
            float x = X(handle) + (frameWidth / 2);
            float y = Y(_cutoffs[handle]);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var ring = new Pen(DawPalette.Filter, 1.5f))
                g.DrawEllipse(ring, x - 4, y - 4, 8, 8);
            if (_dragStep >= 0)
            {
                using var fill = new SolidBrush(DawPalette.Filter);
                g.FillEllipse(fill, x - 2.5f, y - 2.5f, 5, 5);
            }

            g.SmoothingMode = SmoothingMode.Default;

            using var valueFont = new Font("Consolas", 7f, FontStyle.Bold);
            using var valueInk = new SolidBrush(DawPalette.HeaderInk(DawPalette.Filter));
            string text = $"{handle:00}: ${_cutoffs[handle]:X2}";
            var size = g.MeasureString(text, valueFont);
            float textX = Math.Min(x + 7, right - size.Width);
            float textY = Math.Clamp(y - size.Height - 2, top, bottom - size.Height);
            g.DrawString(text, valueFont, valueInk, textX, textY);
        }

        // The playing frame, at the cutoff the SID really has: a sweep runs on from wherever its
        // phase is, so the dot can sit off a curve drawn from a sweep started at zero.
        if (_playhead is { } at && at.Frame >= 0)
        {
            float x = X(GraphFrame(at.Frame)) + (frameWidth / 2);
            float y = Y(Math.Min(at.Cutoff, ceiling));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var line = new Pen(DawPalette.WithAlpha(DawPalette.Filter, 34)))
                g.DrawLine(line, x, top - 4, x, bottom);
            using (var halo = new SolidBrush(DawPalette.WithAlpha(DawPalette.Filter, 45)))
                g.FillEllipse(halo, x - 5, y - 5, 10, 10);
            using (var dot = new SolidBrush(DawPalette.WithAlpha(Color.White, 210)))
                g.FillEllipse(dot, x - 2.2f, y - 2.2f, 4.4f, 4.4f);
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
