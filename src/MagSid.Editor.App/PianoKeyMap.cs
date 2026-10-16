using System.Runtime.InteropServices;
using MagSid.Core.Model;

namespace MagSid.Editor.App;

/// <summary>
/// The classic tracker piano layout: ZSXDCVGBHNJM is one octave and Q2W3ER5T6Y7U the next one up.
///
/// Shared rather than owned by the tracker grid, because the DAW's piano roll plays from the same
/// keys and a second copy of this map would drift from the first the day either changed. Both views
/// also read the octave key from here, so a layout where the apostrophe sits somewhere else moves
/// it in both at once.
/// </summary>
internal static class PianoKeyMap
{
    private static readonly Dictionary<Keys, int> LowerRowPitch = new()
    {
        [Keys.Z] = 0, [Keys.S] = 1, [Keys.X] = 2, [Keys.D] = 3, [Keys.C] = 4, [Keys.V] = 5,
        [Keys.G] = 6, [Keys.B] = 7, [Keys.H] = 8, [Keys.N] = 9, [Keys.J] = 10, [Keys.M] = 11,
    };

    private static readonly Dictionary<Keys, int> UpperRowPitch = new()
    {
        [Keys.Q] = 0, [Keys.D2] = 1, [Keys.W] = 2, [Keys.D3] = 3, [Keys.E] = 4, [Keys.R] = 5,
        [Keys.D5] = 6, [Keys.T] = 7, [Keys.D6] = 8, [Keys.Y] = 9, [Keys.D7] = 10, [Keys.U] = 11,
    };

    private static readonly (char Letter, bool IsSharp)[] PitchClasses =
    [
        ('C', false), ('C', true), ('D', false), ('D', true), ('E', false), ('F', false),
        ('F', true), ('G', false), ('G', true), ('A', false), ('A', true), ('B', false),
    ];

    /// <summary>Lowest and highest octave the base octave may be set to. The upper key row plays
    /// one above it, which <see cref="Note"/> clamps rather than refuses - the alternative is a row
    /// of keys that go silent at the top of the range.</summary>
    public const int MinOctave = 0;
    public const int MaxOctave = 7;

    /// <summary>The key that types an apostrophe on the current Windows layout, which is what both
    /// views use to change octave - resolved rather than hardcoded to the US Oem7 position, so it
    /// lands on whatever key actually types it.</summary>
    public static Keys OctaveKey
    {
        get
        {
            // Off Windows there is no layout to ask; the key under ' on a US keyboard stands in.
            if (!OperatingSystem.IsWindows())
                return Keys.OemQuotes;

            short result = VkKeyScanEx('\'', GetKeyboardLayout(0));
            return result == -1 ? Keys.Oem7 : (Keys)(result & 0xFF);
        }
    }

    /// <summary>The pitch class a key plays on the lower row, or null. Exposed beside
    /// <see cref="TryNote"/> because the tracker grid needs to know which row a key came from -
    /// the upper one enters at octave + 1.</summary>
    public static int? LowerPitch(Keys key) =>
        LowerRowPitch.TryGetValue(key, out int pitch) ? pitch : null;

    public static int? UpperPitch(Keys key) =>
        UpperRowPitch.TryGetValue(key, out int pitch) ? pitch : null;

    /// <summary>The note a key plays with the given base octave, or null when it is not a piano
    /// key. The upper row sounds an octave above the lower one.</summary>
    public static NoteName? TryNote(Keys key, int octave)
    {
        if (LowerRowPitch.TryGetValue(key, out int lower))
        {
            return Note(lower, octave);
        }

        return UpperRowPitch.TryGetValue(key, out int upper) ? Note(upper, octave + 1) : null;
    }

    public static NoteName Note(int pitchClass, int octave)
    {
        var (letter, isSharp) = PitchClasses[pitchClass];
        return new NoteName(letter, isSharp, Math.Clamp(octave, MinOctave, MaxOctave));
    }

    /// <summary>Steps the octave within range - the same move both views make on the octave key.
    /// </summary>
    public static int StepOctave(int octave, bool up) =>
        Math.Clamp(octave + (up ? 1 : -1), MinOctave, MaxOctave);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanEx(char ch, IntPtr hkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
