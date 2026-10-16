using MagSid.Core.Midi;

namespace MagSid.Editor.App;

/// <summary>
/// What the Import MIDI dialog says - the parts of a file, the song it will make, and how - kept
/// here so the Windows dialog and the Avalonia one say exactly the same.
/// </summary>
public static class MidiImportText
{
    private static readonly string[] GmFamilies =
    [
        "Piano", "Chromatic perc.", "Organ", "Guitar", "Bass", "Strings", "Ensemble", "Brass",
        "Reed", "Pipe", "Synth lead", "Synth pad", "Synth FX", "Ethnic", "Percussive", "Sound FX",
    ];

    public static readonly string[] Roles = ["Melody - highest note", "Bass - lowest note", "Drums + bass", "Chords - arpeggios"];

    public static readonly string[] Octaves = ["-3 oct", "-2 oct", "-1 oct", "0 oct", "+1 oct", "+2 oct", "+3 oct"];

    public static readonly string[] Grids = ["16th notes - 4 rows a beat", "32nd notes - 8 rows a beat"];

    public static string Heading(MidiSong midi, string fileName) =>
        $"{fileName}  -  {midi.Bpm:0.#} BPM, {midi.BeatsPerBar}/{midi.BeatUnit}, " +
        $"{midi.Tracks.Count} track{(midi.Tracks.Count == 1 ? "" : "s")}";

    public static string Source(MidiSource source) => $"T{source.Track + 1} ch{source.Channel + 1}";

    public static string Sound(MidiSource source) =>
        source.LooksLikeDrums ? "Drums" : source.Program is { } p ? $"{GmFamilies[p / 8]} ({p + 1})" : "";

    public static string KeyName(int key) =>
        $"{"C C#D D#E F F#G G#A A#B "[(key % 12 * 2)..(key % 12 * 2 + 2)].Trim()}{key / 12 - 1}";

    public static string Range(MidiSource source) => $"{KeyName(source.LowestKey)} - {KeyName(source.HighestKey)}";

    public static string Hint(string bankDirectory) =>
        "Each voice plays one note at a time: Melody keeps the highest note of a chord, Bass the " +
        "lowest. Drums + bass puts the rhythm section on one voice the C64 way - kick and snare " +
        "from the drum part, the bass line between them, the kick settling on the bass note. " +
        "Notes snap to the grid, and a bar that repeats is stored once. With GM sound bank on, " +
        "each part's General MIDI sound becomes a demo-song instrument - a piano Lead2, a synth " +
        "lead Lightforce's Lead1 - replaceable by files of your own in " + bankDirectory +
        ". Load your own .json " +
        "instruments here (for Drums + bass, the bass; for Chords, the sound every chord is " +
        "played on), pick one from the Soundbank, or edit them afterwards. Chords reads the chord of " +
        "each bar; an inversion is the same chord, so C-E-G and E-G-C share one instrument, a chord " +
        "has at most 4 notes, and chords beyond the 16 instruments are picked note by note in the pattern.";

    /// <summary>The song the choices make, in a few lines: size, what was bent to fit, tempo.</summary>
    public static string Report(MidiImportReport report, bool chordGateOffs)
    {
        var lines = new List<string>
        {
            $"{report.Bars} bars in {report.Patterns} patterns of {report.BarsPerPattern} bar{(report.BarsPerPattern == 1 ? "" : "s")} " +
            $"({report.RowsPerBar * report.BarsPerPattern} rows) - {report.TotalBytes} bytes with the player.",
            $"{report.NotesPlaced} notes placed.",
        };

        var bent = new List<string>();
        if (report.NotesDropped > 0)
            bent.Add($"{report.NotesDropped} dropped where a voice had more than one note at once");
        if (report.NotesMoved > 0)
            bent.Add($"{report.NotesMoved} moved onto the grid");
        if (report.NotesShifted > 0)
            bent.Add($"{report.NotesShifted} moved by octaves into C-0..B-7");
        if (bent.Count > 0)
            lines.Add(string.Join("; ", bent) + ".");
        if (report.PickupRows > 0)
            lines.Add($"The file starts on an upbeat: {report.PickupRows} rows of rest go in front, so its bars line up.");
        if (report.ChordsFound > 0)
            lines.Add(report.ChordsPicked == 0
                ? $"{report.ChordsFound} chords, one arpeggio instrument each."
                : $"{report.ChordsFound} chords; the {report.ChordsKept} most played became arpeggio instruments, and the other " +
                  $"{report.ChordsPicked} are picked note by note in the pattern on one instrument - instruments run out at 16.");
        if (report.ChordsFound > 0 && chordGateOffs)
            lines.Add("Each chord's gate goes off 3 rows after it is struck, 4 rows - with a longer release, " +
                      "$B or $C - for chords that usually ring longer than 8; in the chord's wavetable, so it costs no pattern bytes.");
        if (report.DrumHits + report.DrumsLeftOut > 0)
            lines.Add($"{report.DrumHits} drum hits placed, {report.DrumsLeftOut} left out - hi-hats when off, " +
                      "toms and cymbals, flams, and hits a kick or snare already had.");

        double off = (report.SongBpm / report.SourceBpm - 1) * 100;
        if (Math.Abs(off) >= 0.5)
            lines.Add($"Plays at {report.SongBpm:0.#} BPM, {Math.Abs(off):0}% {(off > 0 ? "faster" : "slower")} than the file.");
        if (report.TempoChanges > 0)
            lines.Add($"The file changes tempo {report.TempoChanges} time{(report.TempoChanges == 1 ? "" : "s")}; the song keeps one speed.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The voice's sound line beside its choices.</summary>
    public static string VoiceSound(VoiceImport voice, int index, string? loadedName, MidiImportOptions options)
    {
        if (!voice.Plays)
            return "";

        if (voice.Role == VoiceRole.Chords)
        {
            string sound = loadedName is { } name ? (name.Length > 0 ? name : "(loaded)") : "pulse (default)";
            return $"Chords on {sound}";
        }

        string description = MidiImporter.SoundFor(voice, index, options).Description;
        return voice.Role == VoiceRole.DrumsAndBass ? $"Kit + {description}" : description;
    }
}
