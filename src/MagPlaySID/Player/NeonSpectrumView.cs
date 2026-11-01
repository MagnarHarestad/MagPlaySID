using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MagPlaySid.Player;

/// <summary>
/// The spectrum a third way: neon bars mirrored about a glowing line, low notes on the left in blue,
/// through violet and magenta to orange for the highest, a soft halo round each and a mark left
/// falling above it. Takes the spectrum's own levels, so it works nothing out but its bars.
/// </summary>
internal sealed class NeonSpectrumView : Control
{
    private const int Bars = 96;
    private const float PeakFallPerFrame = 0.007f;

    /// <summary>The share of a bar's height it also reaches below the line.</summary>
    private const double Below = 0.8;

    private static readonly (double At, Color Colour)[] Sweep =
    [
        (0.00, Color.FromRgb(0x2B, 0x4B, 0xC4)),
        (0.30, Color.FromRgb(0x64, 0x48, 0xBC)),
        (0.52, Color.FromRgb(0xB8, 0x3A, 0xB8)),
        (0.74, Color.FromRgb(0xDC, 0x62, 0x7A)),
        (1.00, Color.FromRgb(0xF6, 0xA6, 0x32)),
    ];

    private static readonly IBrush Back = SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground);
    private static readonly IPen Border = SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder);
    private static readonly IBrush[] Fill = Made(255);
    private static readonly IBrush[] Halo = Made(46);
    private static readonly IBrush[] Mark = Made(170);
    private static readonly IBrush Line = new ImmutableLinearGradientBrush(
        Sweep.Select(s => new ImmutableGradientStop(s.At, Lighter(s.Colour))).ToArray(),
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));

    private readonly float[] _levels = new float[Bars];
    private readonly float[] _peaks = new float[Bars];

    public NeonSpectrumView() => ClipToBounds = true;

    /// <summary>On a frame, from the spectrum's bands from low to high: each bar the loudest of its share.</summary>
    public void Update(IReadOnlyList<float> bands)
    {
        for (int bar = 0; bar < Bars; bar++)
        {
            int from = bar * bands.Count / Bars, to = Math.Max(from + 1, (bar + 1) * bands.Count / Bars);
            float level = 0;
            for (int band = from; band < to; band++)
                level = Math.Max(level, bands[band]);
            _levels[bar] = level;
            _peaks[bar] = level > _peaks[bar] ? level : Math.Max(level, _peaks[bar] - PeakFallPerFrame);
        }
        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Back, bounds);

        double middle = bounds.Height / 2;
        double reach = middle - 6;
        double slot = (bounds.Width - 8) / Bars;
        double width = Math.Max(1.2, slot * 0.6);
        for (int bar = 0; bar < Bars; bar++)
        {
            double x = 4 + bar * slot + (slot - width) / 2;
            double up = _levels[bar] * reach;
            double down = up * Below;
            if (up >= 0.5)
            {
                context.FillRectangle(Halo[bar], new Rect(x - slot * 0.16, middle - up - 1, width + slot * 0.32, up + down + 2));
                context.FillRectangle(Fill[bar], new Rect(x, middle - up, width, up + down));
            }
            if (_peaks[bar] > 0.01f)
                context.FillRectangle(Mark[bar], new Rect(x, middle - _peaks[bar] * reach - 2.5, width, 1.3));
        }

        context.FillRectangle(Line, new Rect(0, middle - 0.5, bounds.Width, 1));
        context.DrawRectangle(Border, bounds.Deflate(0.5));
    }

    private static IBrush[] Made(int alpha) => Enumerable.Range(0, Bars)
        .Select(bar => SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(At(bar / (double)(Bars - 1)), alpha)))
        .ToArray();

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

    private static Color Lighter(Color c) =>
        Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2));
}
