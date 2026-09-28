namespace MagSid.Core.Model;

/// <summary>
/// The two halves of a byte that carries two values.
///
/// Three of an instrument's bytes are shaped this way - attack/decay, sustain/release, and vibrato's
/// delay/speed - and every editor that offers one half as its own control has to put it back without
/// disturbing the other. Doing that inline is four characters of shifting and masking that look
/// obviously right, which in this project has twice now been the shape of a real bug.
/// </summary>
public static class PackedNibbles
{
    /// <summary>The upper half: attack, sustain, vibrato delay.</summary>
    public static int High(byte value) => value >> 4;

    /// <summary>The lower half: decay, release, vibrato speed.</summary>
    public static int Low(byte value) => value & 0x0F;

    /// <summary>Replaces the upper half, leaving the lower one exactly as it was.</summary>
    public static byte WithHigh(byte value, int high) =>
        (byte)(((high & 0x0F) << 4) | (value & 0x0F));

    /// <summary>Replaces the lower half, leaving the upper one exactly as it was.</summary>
    public static byte WithLow(byte value, int low) =>
        (byte)((value & 0xF0) | (low & 0x0F));

    public static byte From(int high, int low) =>
        (byte)(((high & 0x0F) << 4) | (low & 0x0F));
}
