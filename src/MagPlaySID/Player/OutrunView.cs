using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MagSid.Playback;
using SkiaSharp;

namespace MagPlaySid.Player;

/// <summary>
/// The music as a drive: a red convertible seen from behind, two in it, cruising a coast road
/// through palms and street lamps toward a great sun over a mountain range - in an arcade racer's
/// colours by day, and a synthwave night of neon grid, stars and a striped sun once the song is half
/// played. The music drives it: how loud it is, how fast the road comes; a kick of the bass a jolt of
/// the car, the grid and the sun flaring, the tail lights glowing; the treble the stars; and the
/// near mountains' ridge is the spectrum itself, low notes in the middle and high to the sides. The
/// road's own bends and hills are always the same for the same distance driven. It all moves by the
/// tune's own time, so it stands still when the tune is paused and races as it fast-forwards.
/// Painted with Skia straight onto the window's surface.
/// </summary>
internal sealed class OutrunView : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Points along the near mountains' ridge, which the spectrum raises.</summary>
    public const int Ridge = 41;

    /// <summary>World units a second at the slowest and how much more the music's loudness adds.</summary>
    private const double CruiseSpeed = 4200, LoudSpeed = 6000;

    /// <summary>A song's length when it is not known: the night comes at its half, three minutes in.</summary>
    private const double UnknownLength = 360;

    private readonly OutrunFrame[] _frames = [new(), new(), new()];
    private readonly OutrunPainter _painter = new();
    private int _nextFrame;

    private double _time = -1, _distance, _heading;
    private float _energy, _kick, _sparkle, _lowAverage, _highAverage;
    private float[] _previous = [];
    private double _lastKick = -10, _lastSparkle = -10;
    private double _phase;
    private readonly float[] _ridge = new float[Ridge];

    public OutrunView() => ClipToBounds = true;

    /// <summary>On to the tune's sample <paramref name="at"/>, from the spectrum's bands from low to
    /// high, each as a share of its recent loudest, in a song <paramref name="lengthSeconds"/> long.</summary>
    public void Update(IReadOnlyList<float> bands, long at, double? lengthSeconds)
    {
        double now = at / (double)SidFilePlaybackEngine.SampleRate;
        double dt = _time < 0 ? 0 : Math.Clamp(now - _time, 0, 0.5);
        _time = now;
        int count = bands.Count;
        if (count == 0)
            return;

        // How loud, all told; and how suddenly the bass and the treble rise - each band's rise since
        // the last frame - against how they have lately been rising: a rise well above that is a hit.
        if (_previous.Length != count)
            _previous = new float[count];
        float all = 0, low = 0, high = 0;
        int lowTo = Math.Max(1, count / 5), highFrom = count / 2;
        for (int band = 0; band < count; band++)
        {
            float level = bands[band];
            float rise = Math.Max(0, level - _previous[band]);
            _previous[band] = level;
            all += level;
            if (band < lowTo)
                low += rise;
            else if (band >= highFrom)
                high += rise;
        }
        all /= count;
        low /= lowTo;
        high /= count - highFrom;
        _energy += (all - _energy) * (all > _energy ? 0.25f : 0.04f);
        if (dt > 0)
        {
            if (low > Math.Max(0.04f, _lowAverage * 2.5f) && now - _lastKick > 0.22)
            {
                _kick = Math.Min(1, 0.6f + (low * 3));
                _lastKick = now;
            }
            if (high > Math.Max(0.03f, _highAverage * 2.2f) && now - _lastSparkle > 0.09)
            {
                _sparkle = 1;
                _lastSparkle = now;
            }
            _lowAverage += (low - _lowAverage) * 0.05f;
            _highAverage += (high - _highAverage) * 0.05f;
            _kick *= (float)Math.Exp(-dt * 7);
            _sparkle *= (float)Math.Exp(-dt * 5);
        }

        // The ridge: the bass in the middle, the treble to each side, rising at once and settling slowly.
        int half = Ridge / 2;
        for (int point = 0; point < Ridge; point++)
        {
            double share = Math.Abs(point - half) / (double)half;
            int from = (int)(share * share * 0.85 * (count - 1)), to = Math.Min(count, from + 1 + (count / 24));
            float level = 0;
            for (int band = from; band < to; band++)
                level = Math.Max(level, bands[band]);
            float wanted = Math.Clamp((level - 0.25f) / 0.75f, 0, 1);
            _ridge[point] += (wanted - _ridge[point]) * (wanted > _ridge[point] ? 0.4f : 0.05f);
        }

        // On down the road, faster the louder, a kick a shove: and round its bends, which turn the sky.
        double speed = CruiseSpeed + (LoudSpeed * Math.Clamp(_energy * 1.3, 0, 1.2)) + (1500 * _kick);
        double step = speed * dt;
        _distance += step;
        _heading += OutrunTrack.Curve(_distance / OutrunTrack.SegmentLength) * step * 0.00012;

        _phase = Phase(now, lengthSeconds is > 1 ? lengthSeconds.Value : UnknownLength);
        InvalidateVisual();
    }

    /// <summary>The time of day: 0 the day, 0.5 the sunset at about the song's half, 1 the night.</summary>
    private static double Phase(double seconds, double length)
    {
        double span = Math.Clamp(length * 0.18, 12, 70);
        double from = (length * 0.47) - (span / 2);
        double t = Math.Clamp((seconds - from) / span, 0, 1);
        return t * t * (3 - (2 * t));
    }

    public void Clear()
    {
        _time = -1;
        _distance = _heading = 0;
        _energy = _kick = _sparkle = _lowAverage = _highAverage = 0;
        Array.Clear(_previous);
        _lastKick = _lastSparkle = -10;
        _phase = 0;
        Array.Clear(_ridge);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2)
            return;
        var frame = _frames[_nextFrame];
        _nextFrame = (_nextFrame + 1) % _frames.Length;
        frame.Width = (float)size.Width;
        frame.Height = (float)size.Height;
        frame.Time = Math.Max(0, _time);
        frame.Distance = _distance;
        frame.Heading = _heading;
        frame.Phase = (float)_phase;
        frame.Kick = _kick;
        frame.Sparkle = _sparkle;
        frame.Energy = _energy;
        Array.Copy(_ridge, frame.Ridge, Ridge);
        context.Custom(new Paint(new Rect(size), frame, _painter));
    }

    private sealed class Paint(Rect bounds, OutrunFrame frame, OutrunPainter painter) : ICustomDrawOperation
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

/// <summary>What one frame of the drive shows, handed from the window's thread to the painter's.</summary>
internal sealed class OutrunFrame
{
    public float Width, Height, Phase, Kick, Sparkle, Energy;
    public double Time, Distance, Heading;
    public readonly float[] Ridge = new float[OutrunView.Ridge];
}

/// <summary>The road: its bends and hills by how far along it is, the same every drive.</summary>
internal static class OutrunTrack
{
    public const double SegmentLength = 200;

    /// <summary>How sharply the road bends at segment <paramref name="segment"/>: long sweeps one
    /// way and the other, with straights between.</summary>
    public static double Curve(double segment)
    {
        double s = segment * 0.0045;
        double bend = (Math.Sin(s) * 0.8) + (Math.Sin((s * 2.3) + 1.7) * 0.5) + (Math.Sin((s * 0.37) + 4.1) * 0.6);
        // Gentle near the middle, so the straights stay straight.
        double sign = Math.Sign(bend);
        double size = Math.Max(0, Math.Abs(bend) - 0.35);
        return sign * size * 2.4;
    }

    /// <summary>How high the road is at <paramref name="z"/>, in world units.</summary>
    public static double Hill(double z)
    {
        double s = z / SegmentLength * 0.006;
        return (Math.Sin(s) * 420) + (Math.Sin((s * 2.7) + 0.9) * 180);
    }
}

/// <summary>Paints the drive with Skia: the sky, the sun and the mountains, the road segment by
/// segment from the car to the horizon, the palms and lamps beside it, and the car.</summary>
internal sealed class OutrunPainter
{
    private const int DrawDistance = 170;
    private const float RoadHalf = 3000, CameraHeight = 1000, Near = 30;
    private const float PalmHeight = 3800, LampHeight = 2700;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _flat = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _glow = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _glowLine = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly Dictionary<int, SKMaskFilter> _blurs = [];

    private readonly SKPath _grassA = new(), _grassB = new(), _roadA = new(), _roadB = new(), _rumbleA = new(), _rumbleB = new(),
        _lanes = new(), _laneGlow = new(), _gridAcross = new(), _gridAlong = new(), _ridge = new(), _ridgeLine = new(), _far = new(), _stripes = new();

    // Each segment as seen from the camera: the road's middle and half-width where it starts, the
    // scale there, and how far down the ground nearer to the car already reaches.
    private readonly float[] _x = new float[DrawDistance + 1], _y = new float[DrawDistance + 1], _scale = new float[DrawDistance + 1], _clip = new float[DrawDistance + 1];
    private readonly bool[] _seen = new bool[DrawDistance + 1];
    private readonly float[] _nearX = new float[DrawDistance + 1], _nearY = new float[DrawDistance + 1], _nearW = new float[DrawDistance + 1];

    private static readonly SKPath PalmTrunk = BuildPalmTrunk(), PalmLeaves = BuildPalmLeaves(), LampPole = BuildLampPole();
    private static readonly SKPath CarBody = Car.Body(), CarDeck = Car.Deck(), CarBumper = Car.Bumper(), CarPanel = Car.Panel(),
        CarTyres = Car.Tyres(), CarGlass = Car.Glass(), CarHeadrests = Car.Headrests(), CarDriverHair = Car.DriverHair(),
        CarPassengerHair = Car.PassengerHair(), CarLightsShape = Car.Lights(), CarPlate = Car.Plate(), CarPipes = Car.Pipes(), CarShine = Car.Shine();

    private static readonly (float X, float Y, float Size)[] Stars = MakeStars();
    private static readonly (float X, float Y, float Size)[] Clouds = [(0.08f, 0.28f, 1.0f), (0.27f, 0.16f, 0.7f), (0.55f, 0.24f, 1.2f), (0.78f, 0.12f, 0.8f), (0.93f, 0.3f, 0.9f), (1.2f, 0.2f, 1.0f)];

    // The colours at day, the golden hour, the sunset, the dusk and the night.
    private static readonly uint[] SkyTop = [0xFF2F86E0, 0xFF4A78C8, 0xFF3A1F73, 0xFF1A0A3D, 0xFF06020F];
    private static readonly uint[] SkyMiddle = [0xFF6FB4EF, 0xFFF0B27A, 0xFFC2367F, 0xFF5A1A6E, 0xFF170632];
    private static readonly uint[] SkyLow = [0xFFBFE6FA, 0xFFFFD59A, 0xFFFF9A3C, 0xFFFF4F8B, 0xFF4A1060];
    private static readonly uint[] SunTop = [0xFFFFFBE8, 0xFFFFF0A0, 0xFFFFE14D, 0xFFFFC43B, 0xFFFFB03C];
    private static readonly uint[] SunLow = [0xFFFFF6CC, 0xFFFFB347, 0xFFFF4F7B, 0xFFFF2D95, 0xFFD0127A];
    private static readonly uint[] Ground = [0xFFC9B486, 0xFFB79A70, 0xFF5E2F52, 0xFF2A1036, 0xFF0D0518];
    private static readonly uint[] GrassA = [0xFFDCC894, 0xFFC9A878, 0xFF6A3358, 0xFF2C1238, 0xFF0F0620];
    private static readonly uint[] GrassB = [0xFFD0BB86, 0xFFBD9C6C, 0xFF5F2C4F, 0xFF261031, 0xFF0C0519];
    private static readonly uint[] RoadA = [0xFF6F7078, 0xFF6A636C, 0xFF3D2C48, 0xFF1D1230, 0xFF110A20];
    private static readonly uint[] RoadB = [0xFF787981, 0xFF726A73, 0xFF44314F, 0xFF221535, 0xFF150C26];
    private static readonly uint[] RumbleA = [0xFFE8302A, 0xFFE83A30, 0xFFFF3D7F, 0xFFFF2D95, 0xFFFF2D95];
    private static readonly uint[] RumbleB = [0xFFF4F4F4, 0xFFF6EFE6, 0xFFF2E6FF, 0xFF29D8FF, 0xFF29F0FF];
    private static readonly uint[] Lane = [0xFFF8F8F8, 0xFFFFF6E6, 0xFFFFD1E8, 0xFF7AF6FF, 0xFF29F0FF];
    private static readonly uint[] FarFill = [0xFF9DBBDD, 0xFFC096A8, 0xFF5A2A78, 0xFF2A0F4D, 0xFF12062A];
    private static readonly uint[] FarEdge = [0xFFD2E3F3, 0xFFF4CDBA, 0xFFFF7AB8, 0xFFA040FF, 0xFF7A2CFF];
    private static readonly uint[] NearFill = [0xFF7499C7, 0xFF8E6C94, 0xFF3C1660, 0xFF1A0838, 0xFF0C0420];
    private static readonly uint[] NearEdge = [0xFFB2CDEA, 0xFFFFB896, 0xFFFF4FB8, 0xFFFF3DF0, 0xFFFF3DF0];
    private static readonly uint[] Haze = [0xFFE8F4FB, 0xFFFFE2B8, 0xFFFF8A7A, 0xFFB03A9A, 0xFF3A0D5A];
    private static readonly uint[] Trunk = [0xFF8A6238, 0xFF6E4A30, 0xFF2A1030, 0xFF160822, 0xFF0C0416];
    private static readonly uint[] Leaves = [0xFF2F8F3A, 0xFF3F7A35, 0xFF24102F, 0xFF13061F, 0xFF0A0314];
    private static readonly uint[] CarRed = [0xFFD8211C, 0xFFD8301E, 0xFFC0204A, 0xFF9A1844, 0xFF7A1238];
    private static readonly uint[] CarDark = [0xFF8E1210, 0xFF7E1A14, 0xFF6A1030, 0xFF4A0A2A, 0xFF36081F];
    private static readonly uint[] Cloud = [0xFFFFFFFF, 0xFFFFE6C8, 0xFFFFA8B8, 0xFF9A4A9A, 0xFF4A1A5A];

    public void Paint(SKCanvas canvas, OutrunFrame frame)
    {
        float w = frame.Width, h = frame.Height;
        float p = frame.Phase, night = Smooth(p, 0.45f, 1f), dusk = Smooth(p, 0.2f, 0.6f);
        float horizon = h * 0.46f, focal = h;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, w, h));

        // The sky.
        using var sky = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, horizon),
            [Colour(SkyTop, p), Colour(SkyMiddle, p), Colour(SkyLow, p)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
        _flat.Color = SKColors.White;
        _flat.Shader = sky;
        canvas.DrawRect(0, 0, w, horizon + 1, _flat);
        _flat.Shader = null;
        PaintStars(canvas, frame, w, horizon, night);
        PaintSun(canvas, frame, w, h, horizon, p, dusk, sky);
        PaintClouds(canvas, frame, w, horizon, p, night);
        PaintMountains(canvas, frame, w, h, horizon, p, night);

        // The ground to the horizon, then the road over it.
        _flat.Color = Colour(Ground, p);
        canvas.DrawRect(0, horizon, w, h - horizon, _flat);
        PaintRoad(canvas, frame, w, h, horizon, focal, p, night);

        // The haze over the far road, and the glow of the horizon after sunset.
        var haze = Colour(Haze, p);
        using (var mist = SKShader.CreateLinearGradient(new SKPoint(0, horizon - 2), new SKPoint(0, horizon + ((h - horizon) * 0.4f)),
                   [haze.WithAlpha((byte)(170 - (60 * night))), haze.WithAlpha(0)], SKShaderTileMode.Clamp))
        {
            _flat.Color = SKColors.White;
            _flat.Shader = mist;
            canvas.DrawRect(0, horizon - 2, w, (h - horizon) * 0.4f + 2, _flat);
            _flat.Shader = null;
        }

        PaintRoadside(canvas, frame, w, h, focal, p, night);
        PaintCar(canvas, frame, w, h, p, night);
        canvas.Restore();
    }

    private void PaintStars(SKCanvas canvas, OutrunFrame frame, float w, float horizon, float night)
    {
        if (night <= 0.02f)
            return;
        foreach (var (x, y, size) in Stars)
        {
            double twinkle = 0.55 + (0.45 * Math.Sin((frame.Time * (1.3 + size)) + (x * 40)));
            float alpha = (float)(night * twinkle * (0.5 + (0.5 * size)) * (1 - (y * 0.6)));
            alpha = Math.Clamp(alpha + (frame.Sparkle * size * 0.5f * night), 0, 1);
            _fill.Color = new SKColor(255, 240, 255, (byte)(alpha * 255));
            canvas.DrawCircle(x * w, y * horizon * 0.95f, 0.5f + (size * 0.9f) + (frame.Sparkle * size * 0.6f), _fill);
        }
    }

    private void PaintSun(SKCanvas canvas, OutrunFrame frame, float w, float h, float horizon, float p, float dusk, SKShader sky)
    {
        float radius = Key([0.12f, 0.2f, 0.36f, 0.4f, 0.42f], p) * h * (1 + (0.05f * frame.Kick));
        float y = horizon + (Key([-0.31f, -0.18f, -0.05f, 0.08f, 0.15f], p) * h);
        float x = w * 0.5f;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, w, horizon));

        // Its glow, then the disc - striped across its lower half from the golden hour on, the
        // stripes drifting down.
        var top = Colour(SunTop, p);
        var low = Colour(SunLow, p);
        using (var halo = SKShader.CreateRadialGradient(new SKPoint(x, y), radius * 1.8f,
                   [low.WithAlpha((byte)(120 + (70 * frame.Kick))), low.WithAlpha(0)], [0.5f, 1], SKShaderTileMode.Clamp))
        {
            _flat.Color = SKColors.White;
            _flat.Shader = halo;
            canvas.DrawCircle(x, y, radius * 1.8f, _flat);
            _flat.Shader = null;
        }

        using (var shade = SKShader.CreateLinearGradient(new SKPoint(0, y - radius), new SKPoint(0, y + radius), [top, low], SKShaderTileMode.Clamp))
        {
            _fill.Shader = shade;
            _fill.Color = SKColors.White;
            canvas.DrawCircle(x, y, radius, _fill);
            _fill.Shader = null;
        }
        if (dusk > 0.01f)
        {
            // The stripes: the sky seen through the sun, cut from its middle down, drifting down.
            _stripes.Reset();
            double drift = frame.Time * 0.12 % 1;
            for (int k = -1; k < 9; k++)
            {
                float at = (float)((k + drift) / 9);
                float stripeY = y - (radius * 0.3f) + (radius * 1.3f * at);
                float thick = radius * (0.012f + (0.075f * Math.Max(0, at))) * dusk;
                if (thick > 0.3f)
                    _stripes.AddRect(new SKRect(x - radius - 2, stripeY, x + radius + 2, stripeY + thick));
            }
            _flat.Color = SKColors.White;
            _flat.Shader = sky;
            canvas.DrawPath(_stripes, _flat);
            _flat.Shader = null;
        }
        canvas.Restore();
    }

    private void PaintClouds(SKCanvas canvas, OutrunFrame frame, float w, float horizon, float p, float night)
    {
        float alpha = 1 - Smooth(p, 0.3f, 0.85f);
        if (alpha <= 0.02f)
            return;
        var colour = Colour(Cloud, p);
        float shift = (float)(((frame.Heading * 0.4) + (frame.Time * 0.004)) % 1.4);
        foreach (var (cx, cy, size) in Clouds)
        {
            float x = (float)(((cx - shift + 2.8) % 1.4) - 0.2) * w;
            float y = cy * horizon;
            float r = horizon * 0.09f * size;
            _fill.Color = colour.WithAlpha((byte)(alpha * 210));
            canvas.DrawOval(x, y, r * 2.2f, r * 0.7f, _fill);
            canvas.DrawCircle(x - (r * 0.9f), y - (r * 0.2f), r * 0.75f, _fill);
            canvas.DrawCircle(x + (r * 0.2f), y - (r * 0.5f), r, _fill);
            canvas.DrawCircle(x + (r * 1.2f), y - (r * 0.15f), r * 0.7f, _fill);
        }
    }

    private void PaintMountains(SKCanvas canvas, OutrunFrame frame, float w, float h, float horizon, float p, float night)
    {
        // The far range: the same peaks, turning past as the road bends.
        _far.Reset();
        float offset = (float)(frame.Heading * 0.25 % 1);
        _far.MoveTo(0, horizon + 1);
        for (int i = 0; i <= 64; i++)
        {
            float u = i / 64f;
            double t = (u + offset) * Math.PI * 2;
            float peak = (float)(0.55 + (0.25 * Math.Sin(t * 3)) + (0.15 * Math.Sin((t * 7) + 1.3)) + (0.08 * Math.Sin((t * 13) + 0.4)));
            _far.LineTo(u * w, horizon - (peak * horizon * 0.32f));
        }
        _far.LineTo(w, horizon + 1);
        _far.Close();
        _flat.Color = Colour(FarFill, p);
        canvas.DrawPath(_far, _flat);
        _stroke.Color = Colour(FarEdge, p).WithAlpha((byte)(90 + (110 * night)));
        _stroke.StrokeWidth = 1;
        canvas.DrawPath(_far, _stroke);

        // The near ridge, raised by the spectrum: the bass in the middle.
        _ridge.Reset();
        _ridgeLine.Reset();
        int points = frame.Ridge.Length;
        float lowest = horizon - (horizon * 0.06f);
        _ridge.MoveTo(0, horizon + 1);
        for (int i = 0; i < points; i++)
        {
            float u = i / (float)(points - 1);
            float wave = (float)(0.5 + (0.5 * Math.Sin((i * 1.9) + 0.6)));
            float valley = 0.3f + (0.7f * Math.Min(1, Math.Abs(u - 0.5f) * 2.6f));
            float y = lowest - (horizon * valley * ((0.06f * wave) + (0.46f * frame.Ridge[i])));
            float x = u * w;
            if (i == 0)
            {
                _ridge.LineTo(x, y);
                _ridgeLine.MoveTo(x, y);
            }
            else
            {
                _ridge.LineTo(x, y);
                _ridgeLine.LineTo(x, y);
            }
        }
        _ridge.LineTo(w, horizon + 1);
        _ridge.Close();
        _flat.Color = Colour(NearFill, p);
        canvas.DrawPath(_ridge, _flat);
        var edge = Colour(NearEdge, p);
        if (night > 0.05f)
        {
            _glowLine.Color = edge.WithAlpha((byte)(night * (45 + (70 * frame.Kick))));
            _glowLine.StrokeWidth = 4;
            canvas.DrawPath(_ridgeLine, _glowLine);
        }
        _stroke.Color = edge.WithAlpha((byte)(140 + (100 * night)));
        _stroke.StrokeWidth = 1.2f;
        canvas.DrawPath(_ridgeLine, _stroke);
    }

    private void PaintRoad(SKCanvas canvas, OutrunFrame frame, float w, float h, float horizon, float focal, float p, float night)
    {
        double distance = frame.Distance;
        double length = OutrunTrack.SegmentLength;
        long first = (long)Math.Floor(distance / length);
        double into = (distance / length) - first;
        double cameraY = OutrunTrack.Hill(distance + 1800) + CameraHeight;

        foreach (var path in (ReadOnlySpan<SKPath>)[_grassA, _grassB, _roadA, _roadB, _rumbleA, _rumbleB, _lanes, _laneGlow, _gridAcross, _gridAlong])
            path.Reset();

        // From the car to the horizon, each segment drawn only where the ground nearer the car does
        // not already reach: so a crest hides what lies behind it.
        double x = 0, dx = -OutrunTrack.Curve(first) * into;
        float maxY = h;
        float half = w / 2;
        float px = 0, py = 0, pw = 0;
        bool havePrevious = false;
        for (int i = 0; i <= DrawDistance; i++)
        {
            long segment = first + i;
            double z = (segment * length) - distance;
            double depth = Math.Max(Near, z);
            float scale = (float)(focal / depth);
            float sx = half + (float)(x * scale);
            float sy = horizon - (float)((OutrunTrack.Hill(segment * length) - cameraY) * scale);
            float sw = RoadHalf * scale;
            _x[i] = sx;
            _y[i] = sy;
            _scale[i] = scale;
            _clip[i] = maxY;
            _seen[i] = false;

            if (havePrevious && sy < py && sy < maxY)
            {
                // From the previous point (near) to this one (far), cut where nearer ground reaches.
                float nx = px, ny = py, nw = pw;
                if (ny > maxY)
                {
                    float t = (ny - maxY) / (ny - sy);
                    nx += (sx - nx) * t;
                    nw += (sw - nw) * t;
                    ny = maxY;
                }
                long stripe = (segment - 1) / 3;
                bool even = stripe % 2 == 0;
                (even ? _grassA : _grassB).AddRect(new SKRect(0, sy, w, ny + 0.5f));
                Quad(even ? _roadA : _roadB, nx, nw, ny, sx, sw, sy);
                float rumble = 0.11f;
                Band(even ? _rumbleA : _rumbleB, nx, nw, ny, sx, sw, sy, 1, 1 + rumble);
                Band(even ? _rumbleA : _rumbleB, nx, nw, ny, sx, sw, sy, -1 - rumble, -1);
                if (even)
                {
                    Band(_lanes, nx, nw, ny, sx, sw, sy, -0.51f, -0.49f);
                    Band(_lanes, nx, nw, ny, sx, sw, sy, -0.01f, 0.01f);
                    Band(_lanes, nx, nw, ny, sx, sw, sy, 0.49f, 0.51f);
                }
                if (night > 0.05f)
                {
                    // The neon's glow: the lit lines again, wider, to be filled faintly.
                    if (!even)
                    {
                        Band(_laneGlow, nx, nw, ny, sx, sw, sy, 0.97f, 1.16f);
                        Band(_laneGlow, nx, nw, ny, sx, sw, sy, -1.16f, -0.97f);
                    }
                    else
                    {
                        Band(_laneGlow, nx, nw, ny, sx, sw, sy, -0.53f, -0.47f);
                        Band(_laneGlow, nx, nw, ny, sx, sw, sy, -0.03f, 0.03f);
                        Band(_laneGlow, nx, nw, ny, sx, sw, sy, 0.47f, 0.53f);
                    }
                }
                if (night > 0.02f)
                {
                    if ((segment - 1) % 3 == 0 && i < 70)
                    {
                        _gridAcross.MoveTo(0, ny);
                        _gridAcross.LineTo(nx - (nw * 1.12f), ny);
                        _gridAcross.MoveTo(nx + (nw * 1.12f), ny);
                        _gridAcross.LineTo(w, ny);
                    }
                }
                _seen[i] = true;
                _nearX[i] = nx;
                _nearY[i] = ny;
                _nearW[i] = nw;
                maxY = sy;
            }
            (px, py, pw) = (sx, sy, sw);
            havePrevious = true;
            if (z > 0)
            {
                x += dx;
                dx += OutrunTrack.Curve(segment);
            }
            if (sy <= horizon - (h * 0.4f))
            {
                for (int rest = i + 1; rest <= DrawDistance; rest++)
                    (_seen[rest], _scale[rest]) = (false, 0);
                break;
            }
        }

        if (night > 0.02f)
        {
            // The grid's lines along the road: one line each, broken only where a crest hides it.
            for (int k = 0; k < 14; k++)
            {
                float across = (k % 2 == 0 ? -1 : 1) * (1.45f + (k / 2 * 0.85f));
                bool drawing = false;
                for (int i = 1; i <= DrawDistance; i++)
                {
                    if (!_seen[i])
                    {
                        drawing = false;
                        continue;
                    }
                    if (!drawing)
                        _gridAlong.MoveTo(_nearX[i] + (_nearW[i] * across), _nearY[i]);
                    _gridAlong.LineTo(_x[i] + (RoadHalf * _scale[i] * across), _y[i]);
                    drawing = true;
                }
            }
        }
        _flat.Color = Colour(GrassA, p);
        canvas.DrawPath(_grassA, _flat);
        _flat.Color = Colour(GrassB, p);
        canvas.DrawPath(_grassB, _flat);
        if (night > 0.02f)
        {
            // The neon grid on the ground, flaring with the bass.
            var magenta = new SKColor(0xFF, 0x2B, 0xD6);
            _glowLine.Color = magenta.WithAlpha((byte)(night * (22 + (60 * frame.Kick))));
            _glowLine.StrokeWidth = 3f;
            canvas.DrawPath(_gridAcross, _glowLine);
            canvas.DrawPath(_gridAlong, _glowLine);
            _stroke.Color = magenta.WithAlpha((byte)(night * (95 + (140 * frame.Kick))));
            _stroke.StrokeWidth = 0.8f;
            canvas.DrawPath(_gridAcross, _stroke);
            canvas.DrawPath(_gridAlong, _stroke);
        }
        _fill.Color = Colour(RoadA, p);
        canvas.DrawPath(_roadA, _fill);
        _fill.Color = Colour(RoadB, p);
        canvas.DrawPath(_roadB, _fill);
        _fill.Color = Colour(RumbleA, p);
        canvas.DrawPath(_rumbleA, _fill);
        _fill.Color = Colour(RumbleB, p);
        canvas.DrawPath(_rumbleB, _fill);
        var lane = Colour(Lane, p);
        if (night > 0.05f)
        {
            _flat.Color = lane.WithAlpha((byte)(night * (45 + (80 * frame.Kick))));
            canvas.DrawPath(_laneGlow, _flat);
        }
        _fill.Color = lane;
        canvas.DrawPath(_lanes, _fill);
    }

    /// <summary>Palms and street lamps beside the road, from the horizon to the car, each hidden
    /// below where the ground nearer the car stands.</summary>
    private void PaintRoadside(SKCanvas canvas, OutrunFrame frame, float w, float h, float focal, float p, float night)
    {
        long first = (long)Math.Floor(frame.Distance / OutrunTrack.SegmentLength);
        var trunk = Colour(Trunk, p);
        var leaves = Colour(Leaves, p);
        var rim = new SKColor(0xFF, 0x6A, 0xD5);
        for (int i = DrawDistance; i >= 2; i--)
        {
            float scale = _scale[i];
            if (scale <= 0 || _clip[i] <= 0 || _y[i] < 0)
                continue;
            long segment = first + i;
            uint hash = Hash(segment);
            bool palm = segment % 5 == 0;
            bool lamp = !palm && segment % 10 == 7;
            if (!palm && !lamp)
                continue;
            float side = (hash & 1) == 0 ? -1 : 1;
            float clipY = _clip[i];
            float baseY = _y[i];
            if (PalmHeight * scale > h * 1.5f)
                continue;
            if (baseY - (PalmHeight * scale) > clipY)
                continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, w, clipY));
            if (palm)
            {
                float across = RoadHalf * (1.55f + ((hash >> 3) % 100 / 100f * 0.9f));
                float x = _x[i] + (side * across * scale);
                float size = PalmHeight * scale * (0.8f + ((hash >> 11) % 100 / 250f));
                float sway = (float)(Math.Sin((frame.Time * 1.7) + (segment * 0.7)) * 0.03) + (frame.Kick * 0.025f);
                canvas.Save();
                canvas.Translate(x, baseY);
                canvas.Scale(((hash >> 17) & 1) == 0 ? size : -size, size);
                canvas.Skew(sway, 0);
                _fill.Color = trunk;
                canvas.DrawPath(PalmTrunk, _fill);
                if (night > 0.05f)
                {
                    // Lit from behind by the neon: the fronds again a little higher, in its colour.
                    _flat.Color = rim.WithAlpha((byte)(night * 170));
                    canvas.Translate(0, -1.3f / size);
                    canvas.DrawPath(PalmLeaves, _flat);
                    canvas.Translate(0, 1.3f / size);
                }
                _fill.Color = leaves;
                canvas.DrawPath(PalmLeaves, _fill);
                canvas.Restore();
            }
            else
            {
                float x = _x[i] + (side * RoadHalf * 1.3f * scale);
                float size = LampHeight * scale;
                canvas.Save();
                canvas.Translate(x, baseY);
                canvas.Scale(side < 0 ? size : -size, size);
                _fill.Color = Mix(new SKColor(0x5A, 0x5E, 0x66), new SKColor(0x1A, 0x10, 0x2A), night);
                canvas.DrawPath(LampPole, _fill);
                canvas.Restore();
                if (night > 0.05f)
                {
                    // The lamp lit: its light on the road, brighter on the beat.
                    float lx = x - (side * 0.3f * size), ly = baseY - (0.97f * size);
                    float glow = night * (0.65f + (0.35f * frame.Kick));
                    float reach = Math.Max(1.5f, size * 0.14f);
                    using (var halo = SKShader.CreateRadialGradient(new SKPoint(lx, ly), reach,
                               [new SKColor(0xFF, 0xC8, 0x8A, (byte)(glow * 150)), new SKColor(0xFF, 0xC8, 0x8A, 0)], SKShaderTileMode.Clamp))
                    {
                        _flat.Color = SKColors.White;
                        _flat.Shader = halo;
                        canvas.DrawCircle(lx, ly, reach, _flat);
                        _flat.Shader = null;
                    }
                    _fill.Color = new SKColor(0xFF, 0xF0, 0xD8, (byte)(glow * 255));
                    canvas.DrawCircle(lx, ly, Math.Max(0.6f, size * 0.025f), _fill);
                }
            }
            canvas.Restore();
        }
    }

    private void PaintCar(SKCanvas canvas, OutrunFrame frame, float w, float h, float p, float night)
    {
        float size = Math.Min(h * 0.46f / 100, w * 0.32f / 200);
        double bend = OutrunTrack.Curve((frame.Distance / OutrunTrack.SegmentLength) + 9);
        float drift = (float)Math.Clamp(-bend * 0.03, -0.06, 0.06) * w;
        float bounce = frame.Kick * 2.2f * size + (float)(Math.Sin(frame.Time * 23) * 0.25 * size * (0.3 + frame.Energy));
        float x = (w / 2) - (100 * size) + drift;
        float y = h - (100 * size) - 2 + bounce;

        canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateDegrees((float)Math.Clamp(bend * 1.2, -3, 3), 100 * size, 96 * size);
        canvas.Scale(size);

        // Its shadow on the road, then the car from the road up.
        _fill.Color = new SKColor(0, 0, 0, (byte)(110 - (40 * night)));
        canvas.DrawOval(100, 95, 96, 7, _fill);
        _fill.Color = new SKColor(0x14, 0x12, 0x18);
        canvas.DrawPath(CarTyres, _fill);
        var red = Colour(CarRed, p);
        var dark = Colour(CarDark, p);
        _fill.Color = dark;
        canvas.DrawPath(CarBumper, _fill);
        _fill.Color = red;
        canvas.DrawPath(CarBody, _fill);
        _fill.Color = Mix(red, dark, 0.35f);
        canvas.DrawPath(CarDeck, _fill);
        _fill.Color = new SKColor(0xD8, 0xF0, 0xFF, (byte)(70 - (30 * night)));
        canvas.DrawPath(CarGlass, _fill);
        _fill.Color = new SKColor(0x3A, 0x22, 0x18);
        canvas.DrawPath(CarDriverHair, _fill);
        _fill.Color = Mix(new SKColor(0xF2, 0xD0, 0x7A), new SKColor(0xB0, 0x80, 0x90), night);
        canvas.DrawPath(CarPassengerHair, _fill);
        _fill.Color = new SKColor(0x1C, 0x16, 0x1C);
        canvas.DrawPath(CarHeadrests, _fill);
        canvas.DrawPath(CarPanel, _fill);
        _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(90 - (50 * night)));
        canvas.DrawPath(CarShine, _fill);

        // The tail lights, glowing as the bass hits and more so at night.
        float light = Math.Clamp(0.35f + (0.65f * frame.Kick) + (0.4f * night), 0, 1);
        _glow.Color = new SKColor(0xFF, 0x20, 0x30, (byte)(light * 200));
        _glow.MaskFilter = Blur(6);
        canvas.DrawPath(CarLightsShape, _glow);
        _glow.MaskFilter = null;
        _fill.Color = Mix(new SKColor(0xC0, 0x10, 0x18), new SKColor(0xFF, 0x70, 0x70), light);
        canvas.DrawPath(CarLightsShape, _fill);
        _fill.Color = Mix(new SKColor(0xF4, 0xD8, 0x3C), new SKColor(0x9A, 0x8A, 0x50), night);
        canvas.DrawPath(CarPlate, _fill);
        _fill.Color = new SKColor(0x8A, 0x8A, 0x92);
        canvas.DrawPath(CarPipes, _fill);
        canvas.Restore();
    }

    private static void Quad(SKPath path, float x1, float w1, float y1, float x2, float w2, float y2)
    {
        path.MoveTo(x1 - w1, y1 + 0.5f);
        path.LineTo(x1 + w1, y1 + 0.5f);
        path.LineTo(x2 + w2, y2);
        path.LineTo(x2 - w2, y2);
        path.Close();
    }

    /// <summary>A strip along the road between two places across it, -1 its left edge and 1 its right.</summary>
    private static void Band(SKPath path, float x1, float w1, float y1, float x2, float w2, float y2, float from, float to)
    {
        path.MoveTo(x1 + (w1 * from), y1 + 0.5f);
        path.LineTo(x1 + (w1 * to), y1 + 0.5f);
        path.LineTo(x2 + (w2 * to), y2);
        path.LineTo(x2 + (w2 * from), y2);
        path.Close();
    }

    private SKMaskFilter Blur(float radius)
    {
        int key = Math.Clamp((int)Math.Round(radius * 2), 1, 200);
        if (!_blurs.TryGetValue(key, out var blur))
            _blurs[key] = blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, key / 2f * 0.6f);
        return blur;
    }

    private static uint Hash(long n)
    {
        ulong x = (ulong)n * 0x9E3779B97F4A7C15UL;
        x ^= x >> 31;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 29;
        return (uint)x;
    }

    private static float Smooth(float value, float from, float to)
    {
        float t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>A value between the five times of day's, at <paramref name="phase"/>.</summary>
    private static float Key(ReadOnlySpan<float> keys, float phase)
    {
        float at = Math.Clamp(phase, 0, 1) * (keys.Length - 1);
        int i = Math.Min((int)at, keys.Length - 2);
        return keys[i] + ((keys[i + 1] - keys[i]) * (at - i));
    }

    private static SKColor Colour(uint[] keys, float phase)
    {
        float at = Math.Clamp(phase, 0, 1) * (keys.Length - 1);
        int i = Math.Min((int)at, keys.Length - 2);
        return Mix(new SKColor(keys[i]), new SKColor(keys[i + 1]), at - i);
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + ((b.Red - a.Red) * t)),
        (byte)(a.Green + ((b.Green - a.Green) * t)),
        (byte)(a.Blue + ((b.Blue - a.Blue) * t)),
        (byte)(a.Alpha + ((b.Alpha - a.Alpha) * t)));

    private static (float, float, float)[] MakeStars()
    {
        var stars = new (float, float, float)[90];
        for (int i = 0; i < stars.Length; i++)
        {
            uint hash = Hash(i + 7919);
            stars[i] = ((hash & 0xFFFF) / 65535f, ((hash >> 16) & 0xFF) / 255f * 0.85f, ((hash >> 24) & 0xFF) / 255f);
        }
        return stars;
    }

    /// <summary>A palm's trunk, one high: its foot at the origin, leaning as it rises.</summary>
    private static SKPath BuildPalmTrunk()
    {
        var path = new SKPath();
        const int steps = 12;
        var left = new SKPoint[steps + 1];
        var right = new SKPoint[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float cx = 0.2f * t * t;
            float cy = -0.86f * t;
            float half = 0.035f - (0.017f * t);
            left[i] = new SKPoint(cx - half, cy);
            right[i] = new SKPoint(cx + half, cy);
        }
        path.MoveTo(left[0]);
        for (int i = 1; i <= steps; i++)
            path.LineTo(left[i]);
        for (int i = steps; i >= 0; i--)
            path.LineTo(right[i]);
        path.Close();
        return path;
    }

    /// <summary>A palm's crown of fronds, arching out and drooping, at the top of its trunk.</summary>
    private static SKPath BuildPalmLeaves()
    {
        var path = new SKPath();
        var top = new SKPoint(0.2f, -0.86f);
        (float Angle, float Length)[] fronds = [(-178, 0.42f), (-150, 0.48f), (-118, 0.36f), (-82, 0.3f), (-50, 0.4f), (-20, 0.5f), (8, 0.44f), (165, 0.34f)];
        foreach (var (angle, length) in fronds)
        {
            double a = angle * Math.PI / 180;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            var tip = new SKPoint(top.X + (dx * length), top.Y + (dy * length * 0.55f) + (length * 0.42f));
            var middle = new SKPoint(top.X + (dx * length * 0.55f), top.Y + (dy * length * 0.5f) - (length * 0.08f));
            float nx = -dy * 0.07f, ny = dx * 0.07f;
            path.MoveTo(top);
            path.QuadTo(middle.X + nx, middle.Y + ny - 0.02f, tip.X, tip.Y);
            path.QuadTo(middle.X - nx, middle.Y - ny + 0.03f, top.X, top.Y + 0.01f);
            path.Close();
        }
        path.AddCircle(top.X - 0.02f, top.Y + 0.03f, 0.022f);
        path.AddCircle(top.X + 0.025f, top.Y + 0.035f, 0.02f);
        return path;
    }

    /// <summary>A street lamp, one high, its foot at the origin and its arm reaching to the left.</summary>
    private static SKPath BuildLampPole()
    {
        var path = new SKPath();
        path.AddRect(new SKRect(-0.012f, -1f, 0.012f, 0));
        path.MoveTo(-0.012f, -1f);
        path.QuadTo(-0.05f, -1.06f, -0.3f, -1.0f);
        path.LineTo(-0.3f, -0.985f);
        path.QuadTo(-0.05f, -1.04f, 0.012f, -0.97f);
        path.Close();
        path.AddRoundRect(new SKRect(-0.36f, -1.0f, -0.24f, -0.965f), 0.01f, 0.01f);
        return path;
    }

    /// <summary>The convertible from behind, in a box 200 wide and 100 high, its wheels on the
    /// box's floor: low and wide, the two in it, black tail light panel and twin pipes.</summary>
    private static class Car
    {
        public static SKPath Tyres()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(6, 62, 34, 97), 5, 5);
            path.AddRoundRect(new SKRect(166, 62, 194, 97), 5, 5);
            return path;
        }

        public static SKPath Bumper()
        {
            var path = new SKPath();
            path.MoveTo(14, 70);
            path.LineTo(186, 70);
            path.LineTo(182, 90);
            path.QuadTo(100, 94, 18, 90);
            path.Close();
            return path;
        }

        public static SKPath Body()
        {
            var path = new SKPath();
            path.MoveTo(4, 74);
            path.QuadTo(2, 48, 22, 40);
            path.LineTo(178, 40);
            path.QuadTo(198, 48, 196, 74);
            path.QuadTo(100, 80, 4, 74);
            path.Close();
            return path;
        }

        public static SKPath Deck()
        {
            var path = new SKPath();
            path.MoveTo(20, 41);
            path.QuadTo(28, 29, 48, 27);
            path.LineTo(152, 27);
            path.QuadTo(172, 29, 180, 41);
            path.Close();
            return path;
        }

        public static SKPath Glass()
        {
            var path = new SKPath();
            path.MoveTo(46, 28);
            path.LineTo(60, 9);
            path.LineTo(140, 9);
            path.LineTo(154, 28);
            path.Close();
            return path;
        }

        public static SKPath DriverHair()
        {
            var path = new SKPath();
            path.AddCircle(76, 17, 9.5f);
            path.AddRoundRect(new SKRect(62, 22, 90, 34), 6, 6);
            return path;
        }

        public static SKPath PassengerHair()
        {
            var path = new SKPath();
            path.AddCircle(124, 17, 9);
            // Her hair flying in the wind.
            path.MoveTo(118, 10);
            path.QuadTo(140, 4, 158, 14);
            path.QuadTo(146, 14, 150, 22);
            path.QuadTo(138, 18, 132, 26);
            path.LineTo(120, 24);
            path.Close();
            path.AddRoundRect(new SKRect(111, 22, 137, 34), 6, 6);
            return path;
        }

        public static SKPath Headrests()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(66, 22, 86, 31), 4, 4);
            path.AddRoundRect(new SKRect(114, 22, 134, 31), 4, 4);
            return path;
        }

        public static SKPath Panel()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(16, 50, 184, 68), 4, 4);
            return path;
        }

        public static SKPath Lights()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(20, 53, 58, 65), 2, 2);
            path.AddRoundRect(new SKRect(142, 53, 180, 65), 2, 2);
            return path;
        }

        public static SKPath Plate()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(86, 72, 114, 82), 1.5f, 1.5f);
            return path;
        }

        public static SKPath Pipes()
        {
            var path = new SKPath();
            path.AddCircle(40, 90, 4);
            path.AddCircle(51, 90, 4);
            path.AddCircle(149, 90, 4);
            path.AddCircle(160, 90, 4);
            return path;
        }

        public static SKPath Shine()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(26, 42, 174, 44.5f), 1.2f, 1.2f);
            return path;
        }
    }
}
