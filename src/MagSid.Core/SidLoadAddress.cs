namespace MagSid.Core;

/// <summary>
/// Where in C64 memory an exported .sid puts the player, and which choices are actually playable.
///
/// The player itself imposes nothing: it holds no alignment requirements and no baked-in
/// addresses, so reassembling at another origin relocates it completely (verified by comparing
/// the SID register writes of BellaCiao built at $1000, $2000 and $4400 - identical over 4000
/// frames). Every rule below therefore comes from the machine, not from the player.
/// </summary>
public static class SidLoadAddress
{
    public const int Default = 0x1000;

    /// <summary>Zero page holds the player's own variables ($20-$48), $0100-$01FF is the stack
    /// and $0200-$03FF the KERNAL's workspace and vectors. A tune loaded into any of it would be
    /// overwritten while it plays.</summary>
    public const int Lowest = 0x0400;

    /// <summary>$D000-$DFFF is I/O - VIC, SID, CIA and colour RAM. Code stored there is not
    /// stored at all.</summary>
    private const int IoStart = 0xD000;
    private const int IoEnd = 0xDFFF;

    /// <summary>Null when the address is usable for a tune of <paramref name="byteLength"/>
    /// bytes, otherwise the reason it is not, phrased for a message box.</summary>
    public static string? Validate(int address, int byteLength)
    {
        if (address < Lowest)
        {
            return $"${address:X4} is too low. The player keeps its own variables in zero page " +
                   $"and the area below ${Lowest:X4} belongs to the stack and the KERNAL, so a " +
                   "tune loaded there would be overwritten as it plays.";
        }

        int end = address + byteLength - 1;
        if (end > 0xFFFF)
        {
            return $"A {byteLength}-byte tune at ${address:X4} would run past the end of memory " +
                   $"(to ${end:X5}). The highest address that fits is ${0x10000 - byteLength:X4}.";
        }

        // Straddling I/O counts: the tune is one contiguous block, so it is enough for any part
        // of it to land there.
        if (address <= IoEnd && end >= IoStart)
        {
            return $"A {byteLength}-byte tune at ${address:X4} reaches ${end:X4}, overlapping the " +
                   $"I/O area ${IoStart:X4}-${IoEnd:X4} where the VIC, SID and CIA chips sit " +
                   "instead of RAM. Pick an address that keeps the whole tune clear of it.";
        }

        return null;
    }

    /// <summary>Accepts "$2000", "2000" and "0x2000" - the three ways the address gets written in
    /// this project's own sources and notes. Null when it is not a 16-bit hex number at all.</summary>
    public static int? TryParse(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith('$'))
            trimmed = trimmed[1..];
        else if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];

        if (trimmed.Length is 0 or > 4)
            return null;

        return int.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }
}
