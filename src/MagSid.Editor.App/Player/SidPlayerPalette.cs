using System.Drawing.Drawing2D;

namespace MagSid.Editor.App.Player;

/// <summary>
/// One palette for the whole player window, so the spectrum and the three scopes read as parts of
/// the same instrument rather than three widgets that happen to share a form.
///
/// The spectrum's sweep runs amber to green across the frequency axis, and each voice's scope takes
/// its trace colour from a fixed point on that same sweep - so voice 1 is the amber end, voice 3 the
/// green. That is the whole colour scheme: one gradient, sampled in four places.
/// </summary>
internal static class SidPlayerPalette
{
    /// <summary>Near-black rather than black: it keeps the glow of a bright bar from banding, and it
    /// is a shade off the tracker's own background so the window reads as part of the same app.</summary>
    public static readonly Color Background = Color.FromArgb(0x0B, 0x0B, 0x12);

    public static readonly Color PanelBackground = Color.FromArgb(0x12, 0x12, 0x1C);
    public static readonly Color PanelBorder = Color.FromArgb(0x25, 0x25, 0x38);

    public static readonly Color Title = Color.FromArgb(0xE8, 0xE8, 0xF4);
    public static readonly Color Subtitle = Color.FromArgb(0x78, 0x78, 0x95);
    public static readonly Color Control = Color.FromArgb(0xD8, 0xD8, 0xE8);
    public static readonly Color ControlHover = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color ControlDisabled = Color.FromArgb(0x44, 0x44, 0x58);

    /// <summary>The sweep, low frequency to high. Sampled for the spectrum bars and for each
    /// voice's trace.</summary>
    private static readonly (float Position, Color Colour)[] Sweep =
    [
        (0.00f, Color.FromArgb(0xF2, 0x6D, 0x5B)),   // ember red, the lowest bars
        (0.18f, Color.FromArgb(0xF2, 0xA8, 0x3C)),   // amber
        (0.38f, Color.FromArgb(0xE0, 0xD9, 0x62)),   // yellow
        (0.62f, Color.FromArgb(0x8F, 0xC9, 0xA8)),   // pale teal
        (1.00f, Color.FromArgb(0x3C, 0xE0, 0x8A)),   // green, the highest
    ];

    /// <summary>Where each voice sits on the sweep. Spread wide enough that three traces on a dark
    /// panel are told apart at a glance, which is the only job these colours have.</summary>
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

            return Color.FromArgb(
                (int)(from.R + ((to.R - from.R) * t)),
                (int)(from.G + ((to.G - from.G) * t)),
                (int)(from.B + ((to.B - from.B) * t)));
        }

        return Sweep[^1].Colour;
    }

    public static Color Dim(Color colour, float amount) => Color.FromArgb(
        (int)(colour.R * amount), (int)(colour.G * amount), (int)(colour.B * amount));

    public static Color WithAlpha(Color colour, int alpha) =>
        Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    /// <summary>A panel's inset background - a hair lighter at the top, which reads as depth without
    /// drawing a border around everything.</summary>
    public static Brush PanelFill(Rectangle bounds) => new LinearGradientBrush(
        bounds with { Height = Math.Max(1, bounds.Height) },
        Color.FromArgb(0x16, 0x16, 0x22),
        Color.FromArgb(0x0D, 0x0D, 0x16),
        LinearGradientMode.Vertical);
}
