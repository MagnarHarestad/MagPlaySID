namespace MagSid.Core.Model;

/// <summary>
/// A single SID note: letter A-G, sharp or natural (no flats - the MAGSIDv7 toolchain's
/// pitch table in Startup\Tables.s only defines "-"/"#" suffixes), octave 0-7.
/// </summary>
public readonly record struct NoteName
{
    private static readonly char[] ValidLetters = ['A', 'B', 'C', 'D', 'E', 'F', 'G'];

    public char Letter { get; }
    public bool IsSharp { get; }
    public int Octave { get; }

    public NoteName(char letter, bool isSharp, int octave)
    {
        letter = char.ToUpperInvariant(letter);
        if (Array.IndexOf(ValidLetters, letter) < 0)
            throw new ArgumentOutOfRangeException(nameof(letter), letter, "Note letter must be A-G.");
        if (octave is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(octave), octave, "Octave must be 0-7.");

        Letter = letter;
        IsSharp = isSharp;
        Octave = octave;
    }

    /// <summary>Parses the exact "C-5" / "C#4" form the file format uses. Flats are rejected.</summary>
    public static bool TryParse(string text, out NoteName note)
    {
        note = default;
        if (string.IsNullOrEmpty(text) || text.Length != 3)
            return false;

        char letter = char.ToUpperInvariant(text[0]);
        char accidental = text[1];
        char octaveChar = text[2];

        if (Array.IndexOf(ValidLetters, letter) < 0)
            return false;
        if (accidental != '-' && accidental != '#')
            return false;
        if (octaveChar is < '0' or > '7')
            return false;

        note = new NoteName(letter, accidental == '#', octaveChar - '0');
        return true;
    }

    public static NoteName Parse(string text)
    {
        if (!TryParse(text, out var note))
        {
            throw new FormatException(
                $"'{text}' is not a valid note (expected e.g. \"C-5\" or \"C#4\"; flats are not supported).");
        }

        return note;
    }

    /// <summary>All 96 valid notes, C-0 through B-7, in pitch order - convenient for UI dropdowns.</summary>
    public static IReadOnlyList<NoteName> AllNotes { get; } = BuildAllNotes();

    private static IReadOnlyList<NoteName> BuildAllNotes()
    {
        var pitchOrder = new (char Letter, bool IsSharp)[]
        {
            ('C', false), ('C', true), ('D', false), ('D', true),
            ('E', false), ('F', false), ('F', true), ('G', false),
            ('G', true), ('A', false), ('A', true), ('B', false),
        };

        var notes = new List<NoteName>(96);
        for (int octave = 0; octave <= 7; octave++)
        {
            foreach (var (letter, isSharp) in pitchOrder)
                notes.Add(new NoteName(letter, isSharp, octave));
        }

        return notes;
    }

    public override string ToString() => $"{Letter}{(IsSharp ? '#' : '-')}{Octave}";

    /// <summary>This note's index into the player's 96-entry pitch table (C-0=$00 .. B-7=$5F) -
    /// the same order <see cref="AllNotes"/> enumerates in, and what a WAVE/ARP note ultimately
    /// compiles down to in Startup\Tables.s. Useful wherever a note needs to be shown alongside
    /// the raw table byte it represents.</summary>
    public int TableIndex => Octave * 12 + PitchClassIndex();

    private int PitchClassIndex() => (Letter, IsSharp) switch
    {
        ('C', false) => 0, ('C', true) => 1, ('D', false) => 2, ('D', true) => 3,
        ('E', false) => 4, ('F', false) => 5, ('F', true) => 6, ('G', false) => 7,
        ('G', true) => 8, ('A', false) => 9, ('A', true) => 10, ('B', false) => 11,
        _ => throw new InvalidOperationException($"Unreachable: {Letter} is not a valid letter."),
    };
}
