namespace MagSid.Editor.App;

/// <summary>
/// The dark palette the tracker grid established, pulled out so every window that adopts it
/// uses the same values instead of a near-miss copy. Only the shared roles live here - a
/// control with genuinely local colours (the tracker grid's note/hold/marker inks) keeps them
/// to itself.
/// </summary>
public static class EditorPalette
{
    public static readonly Color Background = Color.FromArgb(0x10, 0x10, 0x20);
    public static readonly Color Title = Color.FromArgb(0x66, 0xCC, 0xFF);
    public static readonly Color Subtitle = Color.FromArgb(0x8A, 0x8A, 0xA8);
    public static readonly Color Heading = Color.FromArgb(0xAA, 0xAA, 0xFF);
    public static readonly Color Body = Color.FromArgb(0xD2, 0xD2, 0xDE);
    public static readonly Color Mono = Color.FromArgb(0x9E, 0xC8, 0xFF);

    public static readonly Color FieldBackground = Color.FromArgb(0x1C, 0x1C, 0x30);
    public static readonly Color ButtonBackground = Color.FromArgb(0x28, 0x28, 0x3C);
    public static readonly Color ButtonHover = Color.FromArgb(0x36, 0x36, 0x50);
    public static readonly Color Border = Color.FromArgb(0x4A, 0x4A, 0x6E);
    public static readonly Color Selection = Color.FromArgb(0x20, 0x30, 0x4A);
}
