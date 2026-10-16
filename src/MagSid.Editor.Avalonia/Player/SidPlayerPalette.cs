using Avalonia;
using Avalonia.Media;

namespace MagSid.Editor.Avalonia.Player;

/// <summary>
/// One palette for the whole player window, the same as the Windows editor's: the spectrum's sweep
/// runs ember to green across the frequency axis, and each voice's scope takes its colour from a
/// fixed point on that sweep - one gradient, sampled in four places.
/// </summary>
internal static class SidPlayerPalette
{
    public static readonly Color Background = Color.FromRgb(0x0B, 0x0B, 0x12);
    public static readonly Color PanelBackground = Color.FromRgb(0x12, 0x12, 0x1C);
    public static readonly Color PanelBorder = Color.FromRgb(0x25, 0x25, 0x38);

    public static readonly Color Title = Color.FromRgb(0xE8, 0xE8, 0xF4);
    public static readonly Color Subtitle = Color.FromRgb(0x78, 0x78, 0x95);
    public static readonly Color Control = Color.FromRgb(0xD8, 0xD8, 0xE8);
    public static readonly Color ControlHover = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color ControlDisabled = Color.FromRgb(0x44, 0x44, 0x58);

    private static readonly (float Position, Color Colour)[] Sweep =
    [
        (0.00f, Color.FromRgb(0xF2, 0x6D, 0x5B)),   // ember red, the lowest bars
        (0.18f, Color.FromRgb(0xF2, 0xA8, 0x3C)),   // amber
        (0.38f, Color.FromRgb(0xE0, 0xD9, 0x62)),   // yellow
        (0.62f, Color.FromRgb(0x8F, 0xC9, 0xA8)),   // pale teal
        (1.00f, Color.FromRgb(0x3C, 0xE0, 0x8A)),   // green, the highest
    ];

    private static readonly float[] VoicePositions = [0.16f, 0.55f, 0.95f];

    public static Color VoiceColour(int voice) => Sample(VoicePositions[voice]);

    /// <summary>The sweep at <paramref name="position"/> in [0, 1], linearly between stops.</summary>
    public static Color Sample(float position)
    {
        position = Math.Clamp(position, 0f, 1f);

        for (int i = 1; i < Sweep.Length; i++)
        {
            if (position > Sweep[i].Position)
                continue;

            var (fromAt, from) = Sweep[i - 1];
            var (toAt, to) = Sweep[i];
            float t = toAt > fromAt ? (position - fromAt) / (toAt - fromAt) : 0f;

            return Color.FromRgb(
                (byte)(from.R + ((to.R - from.R) * t)),
                (byte)(from.G + ((to.G - from.G) * t)),
                (byte)(from.B + ((to.B - from.B) * t)));
        }

        return Sweep[^1].Colour;
    }

    public static Color WithAlpha(Color colour, int alpha) => Color.FromArgb((byte)alpha, colour.R, colour.G, colour.B);

    public static IBrush Brush(Color colour) => new SolidColorBrush(colour);

    /// <summary>A panel's inset background - a hair lighter at the top, which reads as depth.</summary>
    public static IBrush PanelFill() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x16, 0x16, 0x22), 0),
            new GradientStop(Color.FromRgb(0x0D, 0x0D, 0x16), 1),
        },
    };
}
