using MagSid.Sid;
using static MagSid.Editor.App.SignalPathTrace;

namespace MagSid.Editor.App;

/// <summary>
/// A <see cref="SignalPathTrace"/> drawn: a frame a column - the register writes, the gate, the
/// envelope, the filter's cutoff and mode, the oscillator and the output - with the note's start
/// in green and, while it is heard, where it has reached in amber. Sized to the whole trace, so
/// whatever holds it scrolls it.
/// </summary>
public sealed class SignalPathCanvas : Control
{
    public const int FrameWidth = 40;
    public const int LabelWidth = 100;

    private const int TopMargin = 20;
    private const int RulerHeight = 24;
    private const int WritesHeight = 24;
    private const int GateHeight = 32;
    private const int EnvelopeHeight = 130;
    private const int CutoffHeight = 90;
    private const int FilterModeHeight = 90;
    private const int OscHeight = 110;
    private const int OutHeight = 110;
    private const int RowGap = 6;

    private TraceResult? _trace;
    private double? _playheadFrame;

    public SignalPathCanvas()
    {
        // Painted through an off-screen buffer: erased and redrawn in two visible steps, the
        // playhead's repaints turn into steady flicker - the same reason the Digi tool's canvas
        // does this.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = EditorPalette.Background;
    }

    public TraceResult? Trace => _trace;

    /// <summary>Shows a trace, sized to it. Sized here, once, rather than from inside Paint -
    /// reassigning a control's own Size from within its own Paint handler triggers a re-layout
    /// mid-paint, which is what once left the canvas rendering only its first couple of rows.</summary>
    public void ShowTrace(TraceResult trace)
    {
        _trace = trace;
        int width = LabelWidth + trace.Frames.Count * FrameWidth + 20;
        int height = TopMargin + RulerHeight + WritesHeight + GateHeight + EnvelopeHeight +
                     CutoffHeight + FilterModeHeight + OscHeight + OutHeight + RowGap * 6 + 20;
        Size = new Size(width, height);
        Invalidate();
    }

    /// <summary>Where the audition has reached, in trace frames, or null when nothing is sounding.</summary>
    public double? PlayheadFrame
    {
        get => _playheadFrame;
        set
        {
            _playheadFrame = value;
            Invalidate();
        }
    }

    /// <summary>The x of a frame's left edge, for a host keeping the playhead in view.</summary>
    public static int XOfFrame(double frame) => LabelWidth + (int)(frame * FrameWidth);

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawTrace(e.Graphics);
        base.OnPaint(e);
    }

    private void DrawTrace(Graphics g)
    {
        g.Clear(EditorPalette.Background);
        if (_trace is null || _trace.Frames.Count == 0)
        {
            return;
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var mono = new Font("Consolas", 8f);
        using var rowFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);

        int top = TopMargin;
        int left = LabelWidth;

        // Frame ruler
        using (var rulerBrush = new SolidBrush(EditorPalette.Subtitle))
        using (var gridPen = new Pen(EditorPalette.Border))
        {
            for (int i = 0; i < _trace.Frames.Count; i++)
            {
                int x = left + i * FrameWidth;
                if (i % 2 == 0)
                {
                    g.DrawString(i.ToString(), mono, rulerBrush, x + 2, top);
                }
                g.DrawLine(gridPen, x, top, x, top + RulerHeight + WritesHeight + GateHeight + EnvelopeHeight + OscHeight + OutHeight);
            }
        }

        int y = top + RulerHeight;

        DrawRowLabel(g, rowFont, "writes\n$d4xx", y, WritesHeight);
        DrawWrites(g, mono, left, y, WritesHeight);
        y += WritesHeight + RowGap;

        DrawRowLabel(g, rowFont, "gate", y, GateHeight);
        DrawGate(g, mono, left, y, GateHeight);
        y += GateHeight + RowGap;

        DrawRowLabel(g, rowFont, "envelope", y, EnvelopeHeight);
        DrawEnvelope(g, mono, left, y, EnvelopeHeight);
        y += EnvelopeHeight + RowGap;

        DrawRowLabel(g, rowFont, "cutoff\n$d416", y, CutoffHeight);
        DrawFilterCutoff(g, left, y, CutoffHeight);
        y += CutoffHeight + RowGap;

        DrawRowLabel(g, rowFont, "filter mode\n$d418", y, FilterModeHeight);
        DrawFilterMode(g, mono, left, y, FilterModeHeight);
        y += FilterModeHeight + RowGap;

        DrawRowLabel(g, rowFont, "oscillator", y, OscHeight);
        DrawWaveformRow(g, left, y, OscHeight, f => f.TestBit ? null : f.OscSamples, f => f.TestBit,
            EditorPalette.Mono, Color.FromArgb(0x5a, 0x20, 0x20));
        y += OscHeight + RowGap;

        DrawRowLabel(g, rowFont, "out\nwave x env", y, OutHeight);
        DrawWaveformRow(g, left, y, OutHeight, f => f.OutSamples, _ => false,
            EditorPalette.Title, Color.Empty);
        y += OutHeight + RowGap;

        if (_trace.TriggerFrame >= 0)
        {
            using var trigPen = new Pen(Color.FromArgb(0x66, 0xcc, 0x66), 1.5f);
            int x = left + _trace.TriggerFrame * FrameWidth;
            g.DrawLine(trigPen, x, top, x, y);
        }

        // Last, so it sits over the trace rather than under it. Amber, to stay distinct from the
        // green trigger marker beside it - the two are often only a frame or two apart.
        if (_playheadFrame is double playhead)
        {
            using var playPen = new Pen(Color.FromArgb(0xff, 0xd7, 0x00), 2f);
            int x = left + (int)(playhead * FrameWidth);
            g.DrawLine(playPen, x, top, x, y);
        }
    }

    private static void DrawRowLabel(Graphics g, Font font, string text, int y, int height)
    {
        using var brush = new SolidBrush(EditorPalette.Heading);
        var rect = new Rectangle(4, y, LabelWidth - 8, height);
        var format = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, format);
    }

    private void DrawWrites(Graphics g, Font font, int left, int y, int height)
    {
        using var tickPen = new Pen(EditorPalette.Mono, 1.5f);
        using var textBrush = new SolidBrush(EditorPalette.Subtitle);

        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            if (frame.Writes.Count == 0)
            {
                continue;
            }

            int x = left + i * FrameWidth + FrameWidth / 2;
            g.DrawLine(tickPen, x, y + 2, x, y + height - 2);
        }
    }

    private void DrawGate(Graphics g, Font font, int left, int y, int height)
    {
        using var onBrush = new SolidBrush(Color.FromArgb(0x2f, 0x6b, 0x2f));
        using var offBrush = new SolidBrush(EditorPalette.FieldBackground);
        using var onText = new SolidBrush(Color.FromArgb(0x9a, 0xff, 0x9a));
        using var offText = new SolidBrush(EditorPalette.Subtitle);

        bool? prevGate = null;
        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x = left + i * FrameWidth;
            g.FillRectangle(frame.GateOn ? onBrush : offBrush, x, y + 2, FrameWidth, height - 4);

            if (prevGate != frame.GateOn)
            {
                g.DrawString(frame.GateOn ? "on" : "off", font, frame.GateOn ? onText : offText, x + 2, y + 5);
            }
            prevGate = frame.GateOn;
        }
    }

    private void DrawEnvelope(Graphics g, Font font, int left, int y, int height)
    {
        using var linePen = new Pen(EditorPalette.Title, 1.4f);
        using var stateText = new SolidBrush(EditorPalette.Subtitle);

        var points = new List<PointF>();
        EnvelopeGenerator.EnvelopeState? prevState = null;

        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            var samples = frame.EnvelopeSamples;
            if (samples.Length == 0)
            {
                continue;
            }

            if (prevState != frame.EnvelopeState)
            {
                string label = frame.EnvelopeState switch
                {
                    EnvelopeGenerator.EnvelopeState.Attack => "A",
                    EnvelopeGenerator.EnvelopeState.DecaySustain => "D/S",
                    _ => "R",
                };
                int lx = left + i * FrameWidth;
                g.DrawString(label, font, stateText, lx + 2, y + 2);
            }
            prevState = frame.EnvelopeState;

            for (int s = 0; s < samples.Length; s++)
            {
                float x = left + i * FrameWidth + (float)s / samples.Length * FrameWidth;
                float norm = samples[s] / 255f;
                float py = y + height - 4 - norm * (height - 20);
                points.Add(new PointF(x, py));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    /// <summary>The filter's cutoff ($D416) as a step chart - one value per frame, not
    /// sub-sampled like the envelope/oscillator/output rows above, because the filter genuinely
    /// only moves once per frame (see FrameData.FilterCutoff's own comment).</summary>
    private void DrawFilterCutoff(Graphics g, int left, int y, int height)
    {
        using var linePen = new Pen(EditorPalette.Title, 1.4f);

        int plotBottom = y + height - 6;
        int plotTop = y + 6;
        float ScaleY(int cutoff) => plotBottom - cutoff / 255f * (plotBottom - plotTop);

        var points = new List<PointF>();
        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x0 = left + i * FrameWidth;
            int x1 = x0 + FrameWidth;
            float py = ScaleY(frame.FilterCutoff);
            points.Add(new PointF(x0, py));
            points.Add(new PointF(x1, py));
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    /// <summary>Filter mode + master volume ($D418), the high nibble being the actual filter
    /// type (low/band/high-pass) - plotted the same way as cutoff, a step chart of the whole
    /// byte, since that is what a single register write actually carries. Res+Route ($D417)
    /// rides along as a text label wherever it changes: it is single-byte state, not a value
    /// worth a line of its own, but a Setup step always writes both together so it belongs on
    /// this row rather than getting a third graph.</summary>
    private void DrawFilterMode(Graphics g, Font font, int left, int y, int height)
    {
        using var linePen = new Pen(EditorPalette.Title, 1.4f);
        using var labelText = new SolidBrush(EditorPalette.Subtitle);

        int plotBottom = y + height - 6;
        int plotTop = y + 20;
        float ScaleY(int value) => plotBottom - value / 255f * (plotBottom - plotTop);

        var points = new List<PointF>();
        for (int i = 0; i < _trace!.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x0 = left + i * FrameWidth;
            int x1 = x0 + FrameWidth;
            float py = ScaleY(frame.FilterModeVol);
            points.Add(new PointF(x0, py));
            points.Add(new PointF(x1, py));

            if (frame.FilterResRouteChanged)
            {
                g.DrawString($"res:${frame.FilterResRoute:x2}", font, labelText, x0 + 2, y + 2);
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    private void DrawWaveformRow(
        Graphics g, int left, int y, int height,
        Func<FrameData, int[]?> selector, Func<FrameData, bool> isTest,
        Color lineColor, Color testColor)
    {
        using var linePen = new Pen(lineColor, 1f);
        using var testBrush = testColor == Color.Empty ? null : new SolidBrush(testColor);
        using var testText = new SolidBrush(EditorPalette.Body);

        int mid = y + height / 2;

        // Global scale so a quiet instrument doesn't look identical to a loud one.
        int maxAbs = 1;
        foreach (var frame in _trace!.Frames)
        {
            var samples = selector(frame);
            if (samples is null)
            {
                continue;
            }
            foreach (int v in samples)
            {
                maxAbs = Math.Max(maxAbs, Math.Abs(v - Center(samples)));
            }
        }

        bool testActiveLabelDrawn = false;
        var points = new List<PointF>();

        for (int i = 0; i < _trace.Frames.Count; i++)
        {
            var frame = _trace.Frames[i];
            int x = left + i * FrameWidth;

            if (isTest(frame))
            {
                if (testBrush is not null)
                {
                    g.FillRectangle(testBrush, x, y + 1, FrameWidth, height - 2);
                    if (!testActiveLabelDrawn)
                    {
                        g.DrawString("TEST", new Font("Segoe UI", 7.5f), testText, x + 2, y + 3);
                        testActiveLabelDrawn = true;
                    }
                }
                continue;
            }
            testActiveLabelDrawn = false;

            var samples = selector(frame);
            if (samples is null || samples.Length == 0)
            {
                continue;
            }

            int c = Center(samples);
            for (int s = 0; s < samples.Length; s++)
            {
                float sx = left + i * FrameWidth + (float)s / samples.Length * FrameWidth;
                float norm = (samples[s] - c) / (float)maxAbs;
                float sy = mid - norm * (height / 2f - 2);
                points.Add(new PointF(sx, sy));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(linePen, points.ToArray());
        }
    }

    private static int Center(int[] samples)
    {
        long sum = 0;
        foreach (int v in samples)
        {
            sum += v;
        }
        return samples.Length == 0 ? 0 : (int)(sum / samples.Length);
    }
}
