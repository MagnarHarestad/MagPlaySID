namespace MagSid.Editor.App.Daw;

/// <summary>
/// The DAW view's colours: one scheme across the arrangement, the piano roll and the instrument, so
/// a thing keeps its colour wherever it appears.
///
/// Cyan is the oscillator, green the envelopes and programs, violet the filter, and amber the two
/// optional effects - vibrato and slide - which is also the pair that decides whether their code is
/// assembled into the player at all. Instrument colours come off one list so a note block in the
/// piano roll, its clip in the arrangement and its row in the list all agree.
///
/// Deliberately separate from <see cref="EditorPalette"/>: the retro view's scheme belongs to the
/// tracker grid and should not shift because this one wanted a different green.
/// </summary>
internal static class DawPalette
{
    public static readonly Color Background = Color.FromArgb(0x0E, 0x10, 0x18);
    public static readonly Color Panel = Color.FromArgb(0x14, 0x1A, 0x27);
    public static readonly Color PanelHeader = Color.FromArgb(0x17, 0x1E, 0x2D);
    public static readonly Color Inset = Color.FromArgb(0x0A, 0x0D, 0x14);
    public static readonly Color Border = Color.FromArgb(0x26, 0x30, 0x48);
    public static readonly Color BorderSoft = Color.FromArgb(0x1E, 0x27, 0x40);

    public static readonly Color Text = Color.FromArgb(0xD2, 0xD2, 0xDE);
    public static readonly Color TextDim = Color.FromArgb(0x6E, 0x74, 0x89);
    public static readonly Color TextFaint = Color.FromArgb(0x4A, 0x50, 0x65);

    public static readonly Color Oscillator = Color.FromArgb(0x35, 0xC8, 0xF0);
    public static readonly Color Envelope = Color.FromArgb(0x3C, 0xE0, 0x8A);
    public static readonly Color Filter = Color.FromArgb(0xC7, 0x9B, 0xF5);
    public static readonly Color Effect = Color.FromArgb(0xF0, 0xA8, 0x3C);

    /// <summary>The switch that says which view is showing.</summary>
    public static readonly Color Active = Color.FromArgb(0xA0, 0x6C, 0xF0);

    public static readonly Color ActiveInk = Color.FromArgb(0x12, 0x08, 0x1F);

    /// <summary>One colour per instrument, by number - sixteen, the format's own limit, so no two
    /// instruments share one. All light enough for the dark note names drawn on them. Patterns
    /// take the same colours by number, and past sixteen they wrap.</summary>
    private static readonly Color[] InstrumentColours =
    [
        Color.FromArgb(0x35, 0xC8, 0xF0), // cyan
        Color.FromArgb(0x3C, 0xE0, 0x8A), // green
        Color.FromArgb(0xF0, 0xA8, 0x3C), // orange
        Color.FromArgb(0xA0, 0x6C, 0xF0), // purple
        Color.FromArgb(0xF0, 0x6E, 0x9A), // pink
        Color.FromArgb(0x6E, 0xE0, 0xD0), // aqua
        Color.FromArgb(0xF0, 0xE0, 0x5A), // yellow
        Color.FromArgb(0xF0, 0x60, 0x4C), // red
        Color.FromArgb(0x6A, 0x8C, 0xF8), // blue
        Color.FromArgb(0xB4, 0xE0, 0x4C), // lime
        Color.FromArgb(0xE0, 0x7A, 0xE8), // magenta
        Color.FromArgb(0xD8, 0xB8, 0x90), // tan
        Color.FromArgb(0xC4, 0xB4, 0xFF), // lavender
        Color.FromArgb(0xFF, 0xB4, 0xA0), // salmon
        Color.FromArgb(0xC8, 0xA0, 0x40), // gold
        Color.FromArgb(0xB8, 0xC0, 0xD0), // silver
    ];

    public static Color ForInstrument(int number) =>
        InstrumentColours[Math.Max(0, number - 1) % InstrumentColours.Length];

    public static Color WithAlpha(Color colour, int alpha) =>
        Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    /// <summary>A panel-header tint of an accent: present enough to name the section, quiet enough
    /// not to compete with the controls under it.</summary>
    public static Color HeaderInk(Color accent) => Color.FromArgb(
        (int)(accent.R * 0.75f) + 40, (int)(accent.G * 0.75f) + 40, (int)(accent.B * 0.75f) + 40);
}
