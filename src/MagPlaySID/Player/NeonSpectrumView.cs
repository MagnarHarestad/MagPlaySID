using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MagSid.Playback;

namespace MagPlaySid.Player;

/// <summary>
/// The spectrum a third way, as water: bars of little neon blocks standing on a swaying line, reaching
/// up or down as a slow swell passes along it, with thin lines of light rolling through them and
/// sparks drifting above and below. The music decides how high the swell stands at each place, low
/// notes on the left and high on the right; the swell itself moves by the tune's own time, so it
/// stands still when the tune is paused and rushes when it fast-forwards.
/// </summary>
internal sealed class NeonSpectrumView : Control
{
    private const int Bars = 96;
    private const float PeakFallPerFrame = 0.007f;

    /// <summary>Below this share of its recent loudest a band raises no water.</summary>
    private const float StillLevel = 0.38f;

    /// <summary>How much of the way to a new level a bar goes in a frame, up and down: the water
    /// lifts at once and settles slowly.</summary>
    private const float Lifting = 0.35f, Settling = 0.07f;

    /// <summary>The bars to each side that a line of light's height is evened out over.</summary>
    private const int SoftBars = 6;

    /// <summary>How finely the colour's sweep from blue to orange is drawn: as many brushes as this.</summary>
    private const int Shades = 16;

    private const int Sparks = 48;
    private const double Tau = 2 * Math.PI;

    /// <summary>A block's height as a share of the view's, the gap to the next as a share of a block.</summary>
    private const double BlockHeight = 0.022, BlockGap = 0.42;

    private static readonly (double At, Color Colour)[] Sweep =
    [
        (0.00, Color.FromRgb(0x2B, 0x4B, 0xC4)),
        (0.30, Color.FromRgb(0x64, 0x48, 0xBC)),
        (0.52, Color.FromRgb(0xB8, 0x3A, 0xB8)),
        (0.74, Color.FromRgb(0xDC, 0x62, 0x7A)),
        (1.00, Color.FromRgb(0xF6, 0xA6, 0x32)),
    ];

    /// <summary>The lines of light: where each lies about the middle, how many waves it has across
    /// the view, how fast it rolls, where it starts, and its colours' stops.</summary>
    private static readonly (double Lying, double Waves, double Speed, double Start, double Hue)[] Lights =
    [
        (0.05, 1.3, 0.11, 0.00, 0.0),
        (-0.04, 1.9, -0.08, 0.31, 0.5),
        (0.12, 2.6, 0.15, 0.62, 0.25),
        (0.00, 0.9, -0.05, 0.14, 0.75),
        (-0.10, 3.3, 0.19, 0.85, 1.0),
    ];

    private static readonly IBrush Back = SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground);
    private static readonly IPen Border = SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder);
    private static readonly IBrush Glow = new ImmutableLinearGradientBrush(
        [
            new ImmutableGradientStop(0.0, Color.FromArgb(0, 0x30, 0x20, 0x80)),
            new ImmutableGradientStop(0.5, Color.FromArgb(120, 0x30, 0x20, 0x80)),
            new ImmutableGradientStop(1.0, Color.FromArgb(0, 0x30, 0x20, 0x80)),
        ],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    private static readonly IBrush[] Fill = Enumerable.Range(0, Shades)
        .Select(shade => SidPlayerPalette.Brush(At(shade / (double)(Shades - 1))))
        .ToArray();
    private static readonly IPen[] Halo = Enumerable.Range(0, Shades)
        .Select(shade => SidPlayerPalette.Pen(SidPlayerPalette.WithAlpha(At(shade / (double)(Shades - 1)), 60), 2))
        .ToArray();
    private static readonly IPen[] Light = Lights.Select(light => (IPen)new ImmutablePen(Across(light.Hue, 190), 1.0)).ToArray();
    private static readonly IPen[] LightHalo = Lights.Select(light => (IPen)new ImmutablePen(Across(light.Hue, 40), 3.5)).ToArray();
    private static readonly IPen Line = new ImmutablePen(Across(0, 255), 1.0);
    private static readonly IPen LineHalo = new ImmutablePen(Across(0, 70), 5);
    private static readonly IBrush Spark = SidPlayerPalette.Brush(Color.FromArgb(210, 0xFF, 0xE8, 0xC8));

    private readonly float[] _raising = new float[Bars];
    private readonly float[] _levels = new float[Bars];
    private readonly float[] _peaks = new float[Bars];
    private readonly float[] _soft = new float[Bars];
    private readonly double[] _line = new double[Bars + 1];
    private readonly StreamGeometry[] _blocks = new StreamGeometry[Shades];
    private readonly StreamGeometryContext[] _paths = new StreamGeometryContext[Shades];
    private float _energy;
    private double _time;

    public NeonSpectrumView() => ClipToBounds = true;

    /// <summary>On to the tune's sample <paramref name="at"/>, from the spectrum's bands from low to
    /// high, each as a share of its recent loudest.</summary>
    public void Update(IReadOnlyList<float> bands, long at)
    {
        _time = at / (double)SidFilePlaybackEngine.SampleRate;
        for (int bar = 0; bar < Bars; bar++)
        {
            int from = bar * bands.Count / Bars, to = Math.Max(from + 1, (bar + 1) * bands.Count / Bars);
            float level = 0;
            for (int band = from; band < to; band++)
                level = Math.Max(level, bands[band]);
            _raising[bar] = Math.Max(0, (level - StillLevel) / (1 - StillLevel));
        }

        float sum = 0;
        for (int bar = 0; bar < Bars; bar++)
        {
            // Each with its neighbours, so the water stands in humps and not in spikes.
            float wanted = (_raising[Math.Max(0, bar - 2)] + 2 * _raising[Math.Max(0, bar - 1)] + 3 * _raising[bar]
                + 2 * _raising[Math.Min(Bars - 1, bar + 1)] + _raising[Math.Min(Bars - 1, bar + 2)]) / 9;
            _levels[bar] += (wanted - _levels[bar]) * (wanted > _levels[bar] ? Lifting : Settling);
            _peaks[bar] = _levels[bar] > _peaks[bar] ? _levels[bar] : Math.Max(_levels[bar], _peaks[bar] - PeakFallPerFrame);
            sum += _levels[bar];
        }
        _energy = sum / Bars;

        for (int bar = 0; bar < Bars; bar++)
        {
            float even = 0;
            int from = Math.Max(0, bar - SoftBars), to = Math.Min(Bars - 1, bar + SoftBars);
            for (int near = from; near <= to; near++)
                even += _levels[near];
            _soft[bar] = even / (to - from + 1);
        }
        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_raising);
        Array.Clear(_levels);
        Array.Clear(_peaks);
        Array.Clear(_soft);
        _energy = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Back, bounds);
        context.FillRectangle(Glow, bounds);

        double middle = bounds.Height / 2;
        double reach = middle - 4;
        for (int bar = 0; bar <= Bars; bar++)
        {
            double along = bar / (double)Bars;
            _line[bar] = middle + reach * (0.17 * _energy * Math.Sin(Tau * (1.1 * along - 0.21 * _time))
                + 0.05 * Math.Sin(Tau * (2.7 * along + 0.17 * _time)));
        }

        DrawLights(context, bounds, middle, reach);
        DrawBars(context, bounds, reach);

        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(new Point(0, _line[0]), false);
            for (int bar = 1; bar <= Bars; bar++)
                path.LineTo(new Point(bounds.Width * bar / Bars, _line[bar]));
            path.EndFigure(false);
        }
        context.DrawGeometry(null, LineHalo, line);
        context.DrawGeometry(null, Line, line);

        DrawSparks(context, bounds, middle, reach);
        context.DrawRectangle(Border, bounds.Deflate(0.5));
    }

    private void DrawLights(DrawingContext context, Rect bounds, double middle, double reach)
    {
        for (int light = 0; light < Lights.Length; light++)
        {
            var (lying, waves, speed, start, _) = Lights[light];
            var curve = new StreamGeometry();
            using (var path = curve.Open())
            {
                for (int bar = 0; bar <= Bars; bar++)
                {
                    double along = bar / (double)Bars;
                    double height = 0.1 + 0.62 * _soft[Math.Min(bar, Bars - 1)];
                    double y = middle + reach * (lying + height * Math.Sin(Tau * (waves * along - speed * _time + start)));
                    if (bar == 0)
                        path.BeginFigure(new Point(0, y), false);
                    else
                        path.LineTo(new Point(bounds.Width * along, y));
                }
                path.EndFigure(false);
            }
            context.DrawGeometry(null, LightHalo[light], curve);
            context.DrawGeometry(null, Light[light], curve);
        }
    }

    /// <summary>Each bar up or down from the line by the swell at its place, lowest where the swell
    /// crosses the line; its blocks gathered by colour, so a frame draws few shapes however many blocks.</summary>
    private void DrawBars(DrawingContext context, Rect bounds, double reach)
    {
        double slot = (bounds.Width - 8) / Bars;
        double width = Math.Max(1.2, slot * 0.62);
        double block = Math.Max(1.6, bounds.Height * BlockHeight);
        double step = block * (1 + BlockGap);
        double tallest = reach * 1.05;

        for (int shade = 0; shade < Shades; shade++)
        {
            _blocks[shade] = new StreamGeometry();
            _paths[shade] = _blocks[shade].Open();
        }

        for (int bar = 0; bar < Bars; bar++)
        {
            double along = (bar + 0.5) / Bars;
            double swell = Math.Clamp((Math.Sin(Tau * (1.7 * along - 0.13 * _time))
                + 0.55 * Math.Sin(Tau * (3.1 * along + 0.09 * _time + 0.3))) / 1.1, -1, 1);
            double shape = 0.22 + 0.78 * Math.Abs(swell);
            int way = swell >= 0 ? -1 : 1;
            double x = 4 + bar * slot + (slot - width) / 2;
            double y = _line[bar] + way * step * 0.5;

            int blocks = (int)(_levels[bar] * shape * tallest / step);
            double hue = 0.5 + 0.5 * Math.Sin(Tau * (1.3 * along - 0.07 * _time));
            for (int b = 0; b < blocks; b++)
                AddBlock(ShadeOf(hue + 0.5 * b / Math.Max(1, blocks)), x, y + way * b * step, width, block, way);

            int peak = (int)(_peaks[bar] * shape * tallest / step);
            if (peak > blocks)
                AddBlock(ShadeOf(hue + 0.5), x, y + way * peak * step, width, block * 0.6, way);
        }

        for (int shade = 0; shade < Shades; shade++)
        {
            _paths[shade].Dispose();
            context.DrawGeometry(Fill[shade], Halo[shade], _blocks[shade]);
        }
    }

    private void AddBlock(int shade, double x, double y, double width, double height, int way)
    {
        double top = way < 0 ? y - height : y;
        var path = _paths[shade];
        path.BeginFigure(new Point(x, top), true);
        path.LineTo(new Point(x + width, top));
        path.LineTo(new Point(x + width, top + height));
        path.LineTo(new Point(x, top + height));
        path.EndFigure(true);
    }

    /// <summary>The colour's sweep there and back, so a hue past either end comes back along it.</summary>
    private static int ShadeOf(double hue)
    {
        double folded = hue > 1 ? 2 - hue : hue;
        return (int)(Math.Clamp(folded, 0, 1) * (Shades - 1));
    }

    private void DrawSparks(DrawingContext context, Rect bounds, double middle, double reach)
    {
        var sparks = new StreamGeometry();
        using (var path = sparks.Open())
        {
            for (int spark = 0; spark < Sparks; spark++)
            {
                // Scattered by the golden ratio: the same places every time, and no two together.
                double seed = (spark * 0.618034) % 1;
                double other = (spark * 0.754878) % 1;
                if (Math.Sin(Tau * (other + (0.3 + seed * 0.5) * _time)) < 0)
                    continue;

                double along = (seed + (0.012 + other * 0.018) * _time) % 1;
                double height = 0.2 + 0.5 * _soft[(int)(along * Bars)];
                double y = middle + reach * ((other - 0.5) * 0.35 + height * Math.Sin(Tau * (2 * along - 0.1 * _time + other)));
                double x = bounds.Width * along;
                const double Size = 2;
                path.BeginFigure(new Point(x, y), true);
                path.LineTo(new Point(x + Size, y));
                path.LineTo(new Point(x + Size, y + Size));
                path.LineTo(new Point(x, y + Size));
                path.EndFigure(true);
            }
        }
        context.DrawGeometry(Spark, null, sparks);
    }

    /// <summary>The colours along a line of light, starting at <paramref name="start"/> of their sweep.</summary>
    private static IImmutableBrush Across(double start, int alpha) => new ImmutableLinearGradientBrush(
        Enumerable.Range(0, 5)
            .Select(stop => new ImmutableGradientStop(stop / 4.0, SidPlayerPalette.WithAlpha(At(Math.Abs(((start + stop / 2.0) % 2) - 1)), alpha)))
            .ToArray(),
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));

    private static Color At(double position)
    {
        for (int i = 1; i < Sweep.Length; i++)
        {
            if (position > Sweep[i].At)
                continue;
            var (fromAt, from) = Sweep[i - 1];
            var (toAt, to) = Sweep[i];
            double t = (position - fromAt) / (toAt - fromAt);
            return Color.FromRgb((byte)(from.R + (to.R - from.R) * t), (byte)(from.G + (to.G - from.G) * t), (byte)(from.B + (to.B - from.B) * t));
        }
        return Sweep[^1].Colour;
    }
}
