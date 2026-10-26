namespace MagPlay.Score;

/// <summary>
/// A tune as a tracker would write it - order lists, patterns of rows, instruments and tables -
/// in a form that can say whatever a C64 player does. Built after GoatTracker's model, which
/// the trackers that came after it share, and widened where other players go further: notes
/// on any frequency table, rows of any length, several effects to a row, tempo per voice,
/// multispeed, and raw register steps for what no effect describes. <see cref="ScorePlayer"/>
/// plays it; a score is right for a tune when the two make the same register writes.
///
/// A "call" is one run of the player - once a frame for most tunes, several times for
/// multispeed ones, at the CIA timer's rate for others. Rows, tables and effects count in calls;
/// time itself is counted in the machine's CPU cycles, so PAL, NTSC, multispeed and CIA tunes are
/// all the same thing at different rates, and digis - written many times between two calls - can
/// be placed where they really fall.
/// </summary>
public sealed class Score
{
    public string Title { get; set; } = "";

    public Machine Machine { get; set; } = Machine.Pal;

    /// <summary>CPU cycles from one call to the next: a frame for a vertical-blank tune, a frame
    /// divided for multispeed, the timer's value for a CIA tune. A <see cref="CallRate"/> effect
    /// changes it, as a tune that reprograms its timer does.</summary>
    public int CyclesPerCall { get; set; } = Machines.CyclesPerFrame(Machine.Pal);

    public double CallsPerSecond => Machines.ClockHz(Machine) / CyclesPerCall;

    /// <summary>The cycle the first call happens on: after a tune's init, when it is transcribed.</summary>
    public long FirstCall { get; set; }

    /// <summary>The cycle of each call, for a tune whose calls are not evenly spaced - an interrupt
    /// moved about, a timer reprogrammed every few calls. Past its end, calls go on
    /// <see cref="CyclesPerCall"/> apart; null starts them on <see cref="FirstCall"/>.</summary>
    public List<long>? CallCycles { get; set; }

    /// <summary>What the music does not say, given outright.</summary>
    public RawLayer Raw { get; } = new();

    /// <summary>The 16-bit SID frequency of each note number. A tune's own table, when it has one,
    /// so its notes come out exactly - players differ in tuning by a fraction of a semitone.</summary>
    public int[] NoteTable { get; set; } = NoteTables.MagSidPal();

    /// <summary>Calls a row lasts, for every voice, until a tempo effect changes it.</summary>
    public int Tempo { get; set; } = 6;

    /// <summary>With funk tempo, rows alternate <see cref="Tempo"/> and this.</summary>
    public int? FunkTempo { get; set; }

    /// <summary>$D418 at the start: filter mode in the high nibble, master volume in the low.
    /// Null writes nothing before the first call - a transcription's init is the tune's own.</summary>
    public int? ModeVolume { get; set; } = 0x0F;

    public List<Instrument> Instruments { get; } = [];

    public List<Pattern> Patterns { get; } = [];

    public Voice[] Voices { get; } = [new(), new(), new()];

    /// <summary>Wave programs: control byte and pitch, a step a call - shared by the instruments
    /// that point into it, as GoatTracker's wavetable is.</summary>
    public List<TableStep> WaveTable { get; } = [];

    public List<TableStep> PulseTable { get; } = [];

    /// <summary>Filter programs. The SID has one filter, so one runs at a time: the one the voice
    /// that last started a filter program asked for.</summary>
    public List<TableStep> FilterTable { get; } = [];

    /// <summary>Where a filter program running from the first call starts, for a tune whose filter
    /// follows no instrument; -1 for none.</summary>
    public int FilterProgram { get; set; } = -1;

    /// <summary>Digis: what a <see cref="PlaySample"/> effect streams to the chip between calls.</summary>
    public List<Sample> Samples { get; } = [];

    public Instrument? InstrumentNumber(int number) => Instruments.FirstOrDefault(i => i.Number == number);

    public Pattern? PatternNumber(int number) => Patterns.FirstOrDefault(p => p.Number == number);
}

/// <summary>One voice's order list: the patterns it plays, in turn, and where it goes back to.</summary>
public sealed class Voice
{
    public List<OrderEntry> Order { get; } = [];

    /// <summary>The entry the voice goes on from after its last.</summary>
    public int LoopTo { get; set; }
}

/// <param name="Transpose">Semitones added to every note of the pattern.</param>
/// <param name="Repeat">Times the pattern plays.</param>
public sealed record OrderEntry(int Pattern, int Transpose = 0, int Repeat = 1);

public sealed class Pattern
{
    public int Number { get; set; }

    public List<Row> Rows { get; } = [];
}

/// <summary>A row of one voice: a note or none, an instrument or none, and effects.</summary>
public sealed record Row
{
    public RowNote Note { get; init; } = RowNote.Hold;

    /// <summary>0 keeps the voice's instrument.</summary>
    public int Instrument { get; init; }

    public IReadOnlyList<Effect> Effects { get; init; } = [];

    /// <summary>Calls the row lasts, when not the tempo's - for players that give each note a
    /// duration rather than a place on a grid, as Rob Hubbard's does.</summary>
    public int? Calls { get; init; }

    public static Row Hold { get; } = new();

    public static Row Of(int note, int instrument = 0, params Effect[] effects) =>
        new() { Note = RowNote.Play(note), Instrument = instrument, Effects = effects };
}

public enum RowNoteKind
{
    /// <summary>Nothing new: the voice goes on as it was.</summary>
    Hold,

    /// <summary>A note: the gate goes on, the instrument starts from its beginning.</summary>
    Note,

    /// <summary>The gate goes off - the release.</summary>
    GateOff,

    /// <summary>The gate goes on again, nothing else restarted.</summary>
    GateOn,
}

public readonly record struct RowNote(RowNoteKind Kind, int Note = 0)
{
    public static RowNote Hold => new(RowNoteKind.Hold);

    public static RowNote GateOff => new(RowNoteKind.GateOff);

    public static RowNote GateOn => new(RowNoteKind.GateOn);

    public static RowNote Play(int note) => new(RowNoteKind.Note, note);
}

/// <summary>How a voice is quietened before a new note, so the note's attack starts clean.</summary>
public enum HardRestartKind
{
    /// <summary>Only the gate goes off.</summary>
    GateOff,

    /// <summary>The gate goes off and AD and SR are set - usually to $0F00 - so the envelope
    /// falls to nothing, GoatTracker's default.</summary>
    Envelope,

    /// <summary>Nothing: the next note simply follows.</summary>
    None,
}

public sealed class Instrument
{
    public int Number { get; set; }

    public string Name { get; set; } = "";

    public int AttackDecay { get; set; }

    public int SustainRelease { get; set; }

    /// <summary>Where its wave, pulse and filter programs start in the score's tables; -1 for none -
    /// a pulse or filter program already running then runs on, as GoatTracker's pointer 0 leaves it.</summary>
    public int WaveProgram { get; set; } = -1;

    public int PulseProgram { get; set; } = -1;

    public int FilterProgram { get; set; } = -1;

    /// <summary>The control byte of a note's first call - $09, test bit and gate, is the common
    /// hard restart. That call writes only it and the envelope: the note's pitch and the wave and
    /// pulse programs start on the next, the voice sounding as it was until then. Null starts
    /// them all on the first call.</summary>
    public int? FirstControl { get; set; }

    /// <summary>Calls before the next row's note that the hard restart happens; 0 for none.</summary>
    public int GateTimer { get; set; } = 2;

    public HardRestartKind HardRestart { get; set; } = HardRestartKind.Envelope;

    /// <summary>The envelope the hard restart sets, AD high byte and SR low.</summary>
    public int HardRestartEnvelope { get; set; } = 0x0F00;

    /// <summary>The instrument's own vibrato, after a delay, whenever the row has no pitch effect.</summary>
    public Vibrato? Vibrato { get; set; }
}

/// <param name="Delay">Calls after the note before it starts.</param>
/// <param name="Speed">Calls of a quarter period: up a quarter, down a half, up a quarter.</param>
/// <param name="Depth">Frequency added or taken each call.</param>
public sealed record Vibrato(int Delay, int Speed, int Depth);

public enum Machine
{
    Pal,
    Ntsc,
}

public static class Machines
{
    public static double ClockHz(Machine machine) => machine == Machine.Ntsc ? 1022727.0 : 985248.0;

    /// <summary>A frame in cycles: 312 lines of 63 on PAL, 263 of 65 on NTSC.</summary>
    public static int CyclesPerFrame(Machine machine) => machine == Machine.Ntsc ? 263 * 65 : 312 * 63;
}

/// <summary>
/// A digi: values written to one register, one every <see cref="CyclesPerValue"/> cycles. Most are
/// 4-bit samples in $D418's volume nibble - the filter mode bits kept as the player has them -
/// but the target can be any register, as the pulse-width and test-bit techniques write others.
/// </summary>
public sealed class Sample
{
    public string Name { get; set; } = "";

    public int Register { get; set; } = 0x18;

    /// <summary>Written into $D418's low nibble with the mode bits kept; otherwise the whole byte.</summary>
    public bool VolumeNibble { get; set; } = true;

    public byte[] Values { get; set; } = [];

    public double CyclesPerValue { get; set; } = 985248.0 / 4000;

    /// <summary>Where it starts again after the last value; -1 to stop.</summary>
    public int LoopFrom { get; set; } = -1;
}

public static class NoteTables
{
    /// <summary>MagSID's PAL table, notes C-0 to B-7.</summary>
    public static int[] MagSidPal()
    {
        var table = new int[96];
        for (int i = 0; i < table.Length; i++)
        {
            var (lo, hi) = MagSid.Core.NotePalTable.GetFreq(MagSid.Core.Model.NoteName.AllNotes[i]);
            table[i] = lo | (hi << 8);
        }
        return table;
    }

    /// <summary>A note's name, C-0 to B-7.</summary>
    public static string Name(int note) =>
        note is >= 0 and < 96 ? MagSid.Core.Model.NoteName.AllNotes[note].ToString() : $"#{note}";
}
