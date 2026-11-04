using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MagPlay.C64;
using SkiaSharp;

namespace MagPlaySid.Player;

/// <summary>
/// The music as a fight, after the C64's The Last Ninja: a black ninja in a moonlit forest arena,
/// seen from above as the camera slowly circles, taking on guards who come at it from all sides in
/// turn. Every note the tune starts is a move of the ninja's - a drum's kick a kick that thumps
/// through the ground, the snare a strike with a flash of white light, the bass a sweep or a stomp,
/// the melody punches, cuts of the sword and roundhouse kicks, a leap up a flying kick and a leap
/// down a back flip to land kneeling - and the middle voice a guard's swing the ninja blocks. It all
/// moves by the tune's own time, so it stands still when the tune is paused and fights faster as it
/// fast-forwards. Painted with Skia straight onto the window's surface, for the glow and the blur.
/// </summary>
internal sealed class NinjaView : Control
{
    private readonly NinjaWorld _world = new();
    private readonly NinjaFrame[] _frames = [new(), new(), new()];
    private readonly NinjaPainter _painter = new();
    private int _nextFrame;

    /// <summary>Each voice's control byte and frequency at the last update: a note starts where the
    /// gate goes on, or the pitch moves to another note with it on.</summary>
    private readonly int[] _control = new int[9];
    private readonly int[] _frequency = new int[9];

    /// <summary>The melody's last note, to leap when it leaps.</summary>
    private double _melodyNote = -1;

    public NinjaView() => ClipToBounds = true;

    /// <summary>The fight brought to the tune's sample <paramref name="at"/>: the move of the loudest
    /// note started since the last update.</summary>
    public void Update(C64PlaybackEngine engine, long at)
    {
        int voices = Math.Min(9, 3 * engine.Chips);
        int chosen = -1, loudest = -1;
        int low = -1, high = -1;
        for (int v = 0; v < voices; v++)
        {
            var (frequency, control, envelope) = engine.VoiceAt(v, at);
            bool gate = (control & 1) != 0;
            bool noise = (control & 0x80) != 0;
            bool started = gate && ((_control[v] & 1) == 0 || (!noise && Math.Abs(Note(frequency) - Note(_frequency[v])) >= 0.5));
            _control[v] = control;
            _frequency[v] = frequency;
            if (gate && !noise && frequency > 0)
            {
                if (low < 0 || frequency < _frequency[low])
                    low = v;
                if (high < 0 || frequency > _frequency[high])
                    high = v;
            }
            if (started && envelope >= loudest)
                (chosen, loudest) = (v, envelope);
        }

        if (chosen >= 0)
        {
            var colour = SidPlayerPalette.VoiceColour(chosen % 3);
            uint argb = 0xFF000000u | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;
            _world.Strike(BeatOf(chosen, low, high), argb, Math.Clamp(0.4f + (loudest / 255f), 0.4f, 1.2f));
        }
        _world.Advance(at / (double)C64PlaybackEngine.SampleRate);
        InvalidateVisual();
    }

    /// <summary>What a note is to the fight: a drum's a kick or a snare, the lowest voice the bass,
    /// the highest the melody - leaping up or down when it leaps a fifth or more - the rest the middle.</summary>
    private NinjaWorld.Beat BeatOf(int voice, int low, int high)
    {
        if ((_control[voice] & 0x80) != 0)
            return _frequency[voice] < 0x1800 ? NinjaWorld.Beat.Kick : NinjaWorld.Beat.Snare;
        if (voice == low && low != high)
            return NinjaWorld.Beat.Bass;
        if (voice != high && low != high)
            return NinjaWorld.Beat.Middle;
        double note = Note(_frequency[voice]);
        double leap = _melodyNote < 0 ? 0 : note - _melodyNote;
        _melodyNote = note;
        return leap >= 7 ? NinjaWorld.Beat.Leap : leap <= -7 ? NinjaWorld.Beat.Fall : NinjaWorld.Beat.Melody;
    }

    /// <summary>A frequency register's note, in semitones.</summary>
    private static double Note(int frequency) => frequency <= 0 ? 0 : 12 * Math.Log2(frequency / (double)0x0115);

    public void Clear()
    {
        _world.Reset();
        Array.Clear(_control);
        Array.Clear(_frequency);
        _melodyNote = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2)
            return;
        // Three frames in turn: the renderer may still be painting the one before.
        var frame = _frames[_nextFrame];
        _nextFrame = (_nextFrame + 1) % _frames.Length;
        _world.Compose(frame, size);
        frame.Sort();
        context.Custom(new Paint(new Rect(size), frame, _painter));
    }

    private sealed class Paint(Rect bounds, NinjaFrame frame, NinjaPainter painter) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            painter.Paint(lease.SkCanvas, frame);
        }
    }
}

/// <summary>Paints a <see cref="NinjaFrame"/> with Skia: its paints, blurs and shaders made once and
/// kept, used on the renderer's thread alone.</summary>
internal sealed class NinjaPainter
{
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPath _path = new();
    private readonly Dictionary<int, SKMaskFilter> _blurs = [];
    private SKShader? _sky, _vignette;
    private (float Width, float Height, uint Top, uint Bottom) _shadersFor;

    public void Paint(SKCanvas canvas, NinjaFrame frame)
    {
        float w = (float)frame.Size.Width, h = (float)frame.Size.Height;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, w, h));
        if (_shadersFor != (w, h, frame.SkyTop, frame.SkyBottom))
        {
            _sky?.Dispose();
            _vignette?.Dispose();
            _sky = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), [new SKColor(frame.SkyTop), new SKColor(frame.SkyBottom)], SKShaderTileMode.Clamp);
            _vignette = SKShader.CreateRadialGradient(new SKPoint(w / 2, h / 2), w * 0.55f,
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 170)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
            _shadersFor = (w, h, frame.SkyTop, frame.SkyBottom);
        }
        _fill.Shader = _sky;
        _fill.MaskFilter = null;
        _fill.BlendMode = SKBlendMode.SrcOver;
        _fill.Color = SKColors.White;
        canvas.DrawRect(0, 0, w, h, _fill);
        _fill.Shader = null;

        var shapes = frame.Shapes;
        for (int i = 0; i < frame.Count; i++)
        {
            ref var s = ref shapes[i];
            var paint = s.Kind is NinjaFrame.Kind.Line or NinjaFrame.Kind.Outline ? _stroke : _fill;
            paint.Color = new SKColor(s.Colour);
            paint.BlendMode = s.Additive ? SKBlendMode.Plus : SKBlendMode.SrcOver;
            paint.MaskFilter = s.Blur > 0.5f ? Blur(s.Blur) : null;
            switch (s.Kind)
            {
                case NinjaFrame.Kind.Line:
                    paint.StrokeWidth = s.Width;
                    canvas.DrawLine(s.X1, s.Y1, s.X2, s.Y2, paint);
                    break;
                case NinjaFrame.Kind.Disc:
                    canvas.DrawCircle(s.X1, s.Y1, s.Width, paint);
                    break;
                case NinjaFrame.Kind.Ellipse:
                    canvas.DrawOval(s.X1, s.Y1, s.X2, s.Y2, paint);
                    break;
                case NinjaFrame.Kind.Polygon:
                case NinjaFrame.Kind.Outline:
                    _path.Reset();
                    _path.MoveTo(frame.Points[s.First], frame.Points[s.First + 1]);
                    for (int p = 1; p < s.Count; p++)
                        _path.LineTo(frame.Points[s.First + (2 * p)], frame.Points[s.First + (2 * p) + 1]);
                    if (s.Kind == NinjaFrame.Kind.Polygon)
                        _path.Close();
                    else
                        paint.StrokeWidth = s.Width;
                    canvas.DrawPath(_path, paint);
                    break;
                case NinjaFrame.Kind.Mesh:
                    var (meshPoints, meshColours) = frame.MeshOf(s.First);
                    if (meshPoints.Length > 0)
                    {
                        _fill.Color = SKColors.White;
                        _fill.BlendMode = SKBlendMode.SrcOver;
                        _fill.MaskFilter = null;
                        // Made and let go here: SkiaSharp's own shortcut leaves the vertices to the
                        // collector, which may free them while Skia is still drawing them.
                        using (var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, meshPoints, meshColours))
                            canvas.DrawVertices(vertices, SKBlendMode.Modulate, _fill);
                    }
                    break;
                case NinjaFrame.Kind.Flash:
                    if (s.Colour == 0)
                    {
                        _fill.Shader = _vignette;
                        _fill.Color = SKColors.White;
                        _fill.BlendMode = SKBlendMode.SrcOver;
                        canvas.DrawRect(0, 0, w, h, _fill);
                        _fill.Shader = null;
                    }
                    else
                    {
                        canvas.DrawRect(0, 0, w, h, paint);
                    }
                    break;
            }
        }
        _fill.MaskFilter = _stroke.MaskFilter = null;
        _fill.BlendMode = _stroke.BlendMode = SKBlendMode.SrcOver;
        canvas.Restore();
    }

    /// <summary>A blur as wide as <paramref name="radius"/>, in half-pixel steps, made once.</summary>
    private SKMaskFilter Blur(float radius)
    {
        int key = (int)Math.Min(80, radius * 2);
        if (!_blurs.TryGetValue(key, out var blur))
            _blurs[key] = blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, key / 2f * 0.6f);
        return blur;
    }
}
