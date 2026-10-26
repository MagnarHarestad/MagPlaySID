using MagSid.Playback;
using static MagSid.Editor.App.DigiEncoder;

namespace MagSid.Editor.App;

/// <summary>
/// The Digi to SID-osc graph: four bands over the clip - the source waveform, what the SID
/// renders, the formant that drives the carrier's filter, and voiced/unvoiced with the pitch -
/// one column per 20ms frame, with the audition's playhead. As wide as the clip at the current
/// zoom and as tall as the view it sits in: whatever holds it scrolls it sideways, and the
/// mouse wheel zooms it (see <see cref="WheelZoom"/>).
/// </summary>
public sealed class DigiCanvas : Control
{
    public const int GraphMargin = 12;
    private const float MinPxPerFrame = 2f;
    private const float MaxPxPerFrame = 80f;

    private AnalysisResult? _analysis;
    private float[] _rawSamples = [];
    private int _sampleRate = 44100;

    /// <summary>PCM straight out of the same reSID port Audition plays through - see
    /// DigiEncoder.RenderClip - kept purely so the graph can draw what the chip will actually
    /// output, not just the source .wav.</summary>
    private float[] _renderedAudio = [];

    /// <summary>Pixels per analysis frame - mouse-wheel adjustable, fitted to the view each time
    /// a new analysis is shown.</summary>
    private float _pxPerFrame = 10f;

    private float? _playheadFrame;
    private int _viewWidth = 1;

    public DigiCanvas()
    {
        // Without this, the playhead timer's ~30/sec Invalidate() during Audition visibly
        // flickers - each repaint erases to the background color and redraws in two visible
        // steps instead of one. Standard fix: paint to an off-screen buffer and blit it in
        // one go.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = EditorPalette.Background;
    }

    /// <summary>The mouse wheel over the graph: where (in this control's own pixels) and which
    /// way. The host zooms (<see cref="Zoom"/>) and keeps that frame where the pointer is.</summary>
    public event Action<int, int>? WheelZoom;

    public AnalysisResult? Analysis => _analysis;

    public float PxPerFrame => _pxPerFrame;

    /// <summary>The clip and its analysis, fitted to the view; the rendered output is cleared
    /// until a new render arrives.</summary>
    public void ShowAnalysis(AnalysisResult? analysis, float[] rawSamples, int sampleRate)
    {
        _analysis = analysis;
        _rawSamples = rawSamples;
        _sampleRate = sampleRate;
        _renderedAudio = [];

        // Fit-to-width by default; the user zooms from there with the mouse wheel.
        int frames = Math.Max(1, analysis?.FrameCount ?? 1);
        int visibleWidth = Math.Max(1, _viewWidth - GraphMargin * 2);
        _pxPerFrame = Math.Clamp(visibleWidth / (float)frames, MinPxPerFrame, MaxPxPerFrame);
        FitWidth();
    }

    public float[] RenderedAudio
    {
        get => _renderedAudio;
        set
        {
            _renderedAudio = value;
            Invalidate();
        }
    }

    public float? PlayheadFrame
    {
        get => _playheadFrame;
        set
        {
            _playheadFrame = value;
            Invalidate();
        }
    }

    /// <summary>The size of the view this sits in: never narrower than it, as tall as it.</summary>
    public void SetView(int width, int height)
    {
        _viewWidth = Math.Max(1, width);
        Height = Math.Max(1, height);
        FitWidth();
    }

    /// <summary>Zooms in (factor above 1) or out, within the limits.</summary>
    public void Zoom(float factor)
    {
        _pxPerFrame = Math.Clamp(_pxPerFrame * factor, MinPxPerFrame, MaxPxPerFrame);
        FitWidth();
    }

    /// <summary>The clip's width at the current zoom, margins included.</summary>
    public int ContentWidth => GraphMargin * 2 + (int)((_analysis?.FrameCount ?? 0) * _pxPerFrame);

    public float FrameAt(int x) => (x - GraphMargin) / _pxPerFrame;

    public float XOfFrame(float frame) => GraphMargin + frame * _pxPerFrame;

    private void FitWidth()
    {
        Width = Math.Max(ContentWidth, _viewWidth);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        WheelZoom?.Invoke(e.X, e.Delta);
        // Deliberately not calling base.OnMouseWheel: a scroll there would fight the zoom.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawAnalysis(e.Graphics);
        base.OnPaint(e);
    }

    private void DrawAnalysis(Graphics g)
    {
        g.Clear(EditorPalette.Background);
        if (_analysis is null || _analysis.FrameCount == 0)
        {
            using var brush = new SolidBrush(EditorPalette.Subtitle);
            g.DrawString("Load a .wav to see the source waveform and detected pitch here.",
                new Font("Segoe UI", 9f), brush, 12, 12);
            return;
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var a = _analysis;
        int margin = GraphMargin;
        int height = Math.Max(1, ClientSize.Height - margin * 2);
        float px = _pxPerFrame;

        // Four bands stacked top to bottom: the raw waveform first (so it reads as "the source"
        // the other three are derived from), then the rendered reSID output/formant/voiced-pitch.
        int bandHeight = height / 4;
        int waveTop = margin, waveBottom = margin + bandHeight - 8;
        int residTop = margin + bandHeight + 8, residBottom = margin + bandHeight * 2 - 8;
        int brightTop = margin + bandHeight * 2 + 8, brightBottom = margin + bandHeight * 3 - 8;
        int pitchTop = margin + bandHeight * 3 + 8, pitchBottom = margin + height;

        double minHz = LowestNoteHz;
        double maxHz = HighestNoteHz;

        using var wavePen = new Pen(EditorPalette.Body, 1f);
        using var waveMidPen = new Pen(EditorPalette.Border);
        using var residPen = new Pen(EditorPalette.Title, 1f);
        using var residMidPen = new Pen(EditorPalette.Border);
        using var brightPen = new Pen(Color.FromArgb(0xd8, 0xa8, 0x3a), 1.4f);
        using var voicedBrush = new SolidBrush(Color.FromArgb(0x2f, 0x6b, 0x2f));
        using var unvoicedBrush = new SolidBrush(Color.FromArgb(0x5a, 0x20, 0x20));
        using var pitchPen = new Pen(EditorPalette.Mono, 1.6f);
        using var labelBrush = new SolidBrush(EditorPalette.Subtitle);
        using var labelFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);

        g.DrawString("waveform (source .wav)", labelFont, labelBrush, margin, waveTop);
        g.DrawString("reSID output (rendered, what the chip will actually play)", labelFont, labelBrush, margin, residTop - 16);
        g.DrawString("formant (dominant resonance, drives the carrier filter)", labelFont, labelBrush, margin, brightTop - 16);
        g.DrawString("voiced / pitch", labelFont, labelBrush, margin, pitchTop - 16);

        DrawWaveform(g, wavePen, waveMidPen, margin, waveTop, waveBottom, px);
        DrawRenderedWaveform(g, residPen, residMidPen, margin, residTop, residBottom, px);

        var brightPoints = new List<PointF>();
        for (int f = 0; f < a.FrameCount; f++)
        {
            float x0 = margin + f * px;
            float x1 = x0 + px;

            float formantNorm = (float)NormalizedFormant(a.FormantHz[f]);
            float by = brightBottom - formantNorm * (brightBottom - brightTop - 4);
            brightPoints.Add(new PointF(x0, by));
            brightPoints.Add(new PointF(x1, by));

            g.FillRectangle(a.Voiced[f] ? voicedBrush : unvoicedBrush,
                x0, pitchTop, Math.Max(1, px), pitchBottom - pitchTop);
        }
        if (brightPoints.Count > 1)
        {
            g.DrawLines(brightPen, brightPoints.ToArray());
        }

        var pitchPoints = new List<PointF>();
        for (int f = 0; f < a.FrameCount; f++)
        {
            if (!a.Voiced[f] || a.PitchHz[f] <= 0)
            {
                if (pitchPoints.Count > 1)
                {
                    g.DrawLines(pitchPen, pitchPoints.ToArray());
                }
                pitchPoints.Clear();
                continue;
            }

            float x = margin + (f + 0.5f) * px;
            double norm = Math.Clamp(Math.Log(a.PitchHz[f] / minHz) / Math.Log(maxHz / minHz), 0.0, 1.0);
            float y = (float)(pitchBottom - norm * (pitchBottom - pitchTop));
            pitchPoints.Add(new PointF(x, y));
        }
        if (pitchPoints.Count > 1)
        {
            g.DrawLines(pitchPen, pitchPoints.ToArray());
        }

        if (_playheadFrame is float playhead)
        {
            using var playheadPen = new Pen(Color.FromArgb(0xff, 0xd7, 0x00), 2f);
            float x = margin + playhead * px;
            g.DrawLine(playheadPen, x, margin, x, margin + height);
        }
    }

    /// <summary>Peak-envelope waveform of the raw samples, one min/max pair per analysis frame -
    /// drawn on the same per-frame grid as the three derived rows below it (rather than true
    /// per-sample resolution) so a column here lines up exactly with the column it produced in
    /// amplitude/formant/voiced-pitch, at any zoom level.</summary>
    private void DrawWaveform(Graphics g, Pen wavePen, Pen midPen, int margin, int top, int bottom, float px)
    {
        int mid = (top + bottom) / 2;
        g.DrawLine(midPen, margin, mid, margin + _analysis!.FrameCount * px, mid);

        if (_rawSamples.Length == 0)
        {
            return;
        }

        int samplesPerFrame = Math.Max(1, _sampleRate / FrameRate);
        float halfHeight = (bottom - top) / 2f - 2f;

        DrawWaveformDetail(g, margin, mid, halfHeight, px, samplesPerFrame);

        // One continuous polyline rather than a separate min/max tick per frame: each frame
        // contributes its peak then its trough (in time order), so the envelope traces as one
        // connected zigzag - a proper waveform line, not disconnected per-column strokes.
        var points = new List<PointF>(_analysis.FrameCount * 2);
        for (int f = 0; f < _analysis.FrameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, _rawSamples.Length - offset);
            if (count <= 0)
            {
                break;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float s = _rawSamples[offset + i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            float x = margin + (f + 0.5f) * px;
            float yMax = mid - Math.Clamp(max, -1f, 1f) * halfHeight;
            float yMin = mid - Math.Clamp(min, -1f, 1f) * halfHeight;

            // Alternate which extreme comes first so consecutive frames' lines don't all cross
            // through the same corner - keeps the envelope's outline visually cleaner.
            if (f % 2 == 0)
            {
                points.Add(new PointF(x, yMax));
                points.Add(new PointF(x, yMin));
            }
            else
            {
                points.Add(new PointF(x, yMin));
                points.Add(new PointF(x, yMax));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(wavePen, points.ToArray());
        }
    }

    /// <summary>Same peak-envelope idea as <see cref="DrawWaveform"/>, but over <see
    /// cref="_renderedAudio"/> - real PCM out of <see cref="SongPlaybackEngine"/>/SidChip, not a
    /// prediction of it - so what actually clips, buzzes, or goes silent because a candidate's
    /// filter closed all the way is visible before ever pressing Audition. Always at
    /// SongPlaybackEngine.SampleRate (44.1kHz) regardless of the source .wav's own rate, since
    /// that is the engine's fixed output rate, not something this clip's source file chose.
    /// Nothing is drawn - just the center line - until a search has actually populated the
    /// buffer (see RenderFullClip): Re-analyze and Load .wav both trigger one.</summary>
    private void DrawRenderedWaveform(Graphics g, Pen wavePen, Pen midPen, int margin, int top, int bottom, float px)
    {
        int mid = (top + bottom) / 2;
        g.DrawLine(midPen, margin, mid, margin + _analysis!.FrameCount * px, mid);

        if (_renderedAudio.Length == 0)
        {
            return;
        }

        int samplesPerFrame = SongPlaybackEngine.SampleRate / FrameRate;
        float halfHeight = (bottom - top) / 2f - 2f;

        var points = new List<PointF>(_analysis.FrameCount * 2);
        for (int f = 0; f < _analysis.FrameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, _renderedAudio.Length - offset);
            if (count <= 0)
            {
                break;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float s = _renderedAudio[offset + i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            float x = margin + (f + 0.5f) * px;
            float yMax = mid - Math.Clamp(max, -1f, 1f) * halfHeight;
            float yMin = mid - Math.Clamp(min, -1f, 1f) * halfHeight;

            if (f % 2 == 0)
            {
                points.Add(new PointF(x, yMax));
                points.Add(new PointF(x, yMin));
            }
            else
            {
                points.Add(new PointF(x, yMin));
                points.Add(new PointF(x, yMax));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(wavePen, points.ToArray());
        }
    }

    /// <summary>The same peak-envelope idea as <see cref="DrawWaveform"/>'s own line, but one
    /// column per screen pixel instead of one per analysis frame, in a dim tone behind it - so
    /// zooming in reveals real waveform detail (individual pitch periods, transients) the
    /// frame-grid line can't show, since that one is deliberately locked to the same 20ms grid
    /// as amplitude/formant/voiced-pitch below it.</summary>
    private void DrawWaveformDetail(Graphics g, int margin, int mid, float halfHeight, float px, int samplesPerFrame)
    {
        using var detailPen = new Pen(Color.FromArgb(70, EditorPalette.Body), 1f);

        double samplesPerPixel = samplesPerFrame / (double)px;
        int contentLeft = margin;
        int contentRight = margin + (int)(_analysis!.FrameCount * px);

        var points = new List<PointF>(Math.Max(0, contentRight - contentLeft) * 2);
        bool firstHalf = true;
        for (int x = contentLeft; x < contentRight; x++)
        {
            double samplePos = (x - margin) / (double)px * samplesPerFrame;
            int startSample = (int)samplePos;
            int endSample = Math.Min(_rawSamples.Length, (int)(samplePos + Math.Max(1, samplesPerPixel)));
            if (startSample >= endSample)
            {
                continue;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = startSample; i < endSample; i++)
            {
                float s = _rawSamples[i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            float yMax = mid - Math.Clamp(max, -1f, 1f) * halfHeight;
            float yMin = mid - Math.Clamp(min, -1f, 1f) * halfHeight;

            if (firstHalf)
            {
                points.Add(new PointF(x, yMax));
                points.Add(new PointF(x, yMin));
            }
            else
            {
                points.Add(new PointF(x, yMin));
                points.Add(new PointF(x, yMax));
            }
            firstHalf = !firstHalf;
        }

        if (points.Count > 1)
        {
            g.DrawLines(detailPen, points.ToArray());
        }
    }
}
