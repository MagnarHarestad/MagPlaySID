using System.Text;

namespace MagPlay.Amiga;

/// <summary>The PC trackers whose modules are played here.</summary>
public enum SongFormat
{
    /// <summary>Scream Tracker 3: samples, 32 channels, effects A-Z.</summary>
    S3m,

    /// <summary>FastTracker 2: instruments with envelopes over samples, effects 0-9 and A-Z.</summary>
    Xm,

    /// <summary>Impulse Tracker: instruments with envelopes and new note actions, or samples alone.</summary>
    It,
}

/// <summary>A cell of a PC tracker's pattern, as its file has it: the note (1 for C-0; see the
/// constants), the instrument or sample (0 none), the volume column (the format's own byte), and
/// the effect - XM's number 0-35, or Scream and Impulse Tracker's letter as 1-26 - with its
/// parameter.</summary>
public readonly record struct SongCell(byte Note, byte Instrument, byte Volume, byte Effect, byte Parameter)
{
    public const byte NoteOff = 255, NoteCut = 254, NoteFade = 253;

    /// <summary>No volume in the column: XM's 0, Scream and Impulse Tracker's 255.</summary>
    public const byte NoVolume = 255;
}

/// <summary>A sample of a PC tracker: 16-bit, an 8-bit one scaled up.</summary>
public sealed class SongSample
{
    public string Name = "";
    public string FileName = "";
    public short[] Data = [];
    public int LoopStart, LoopEnd;

    /// <summary>0 none, 1 forward, 2 ping-pong.</summary>
    public int LoopMode;

    /// <summary>Impulse Tracker's sustain loop, played while the key is held.</summary>
    public int SustainStart, SustainEnd, SustainMode;

    /// <summary>0-64.</summary>
    public int Volume = 64;

    /// <summary>Impulse Tracker's sample global volume, 0-64.</summary>
    public int GlobalVolume = 64;

    /// <summary>0-256, or -1 to take the channel's.</summary>
    public int Pan = -1;

    /// <summary>Hertz the sample plays at for the format's middle note: XM's C-4 at its relative note
    /// and finetune, Scream Tracker's C-4 (its C2Spd), Impulse Tracker's C-5.</summary>
    public double BaseRate = 8363;

    /// <summary>Its bytes in the file, before any unpacking - for the list of samples.</summary>
    public int Bytes;

    // Auto-vibrato: XM's from the instrument, Impulse Tracker's from the sample.
    public int VibratoType, VibratoSweep, VibratoDepth, VibratoRate;
}

/// <summary>An envelope: points of (tick, value), sustained and looped as its flags say.</summary>
public sealed class SongEnvelope
{
    public (int Tick, int Value)[] Points = [];
    public bool Enabled, Loop, Sustain, Carry, Filter;
    public int LoopStart, LoopEnd, SustainStart, SustainEnd;
}

/// <summary>An instrument of XM or Impulse Tracker: which sample - and in Impulse Tracker which
/// note - each note plays, and its envelopes, fadeout and new note action.</summary>
public sealed class SongInstrument
{
    public string Name = "";

    /// <summary>For each note 0-119, the sample (from 1; 0 none) and the note it plays.</summary>
    public readonly int[] SampleOf = new int[120];
    public readonly int[] NoteOf = Enumerable.Range(0, 120).ToArray();

    public SongEnvelope VolumeEnvelope = new(), PanEnvelope = new(), PitchEnvelope = new();

    /// <summary>Taken from a 65536 fade a tick once the key is released.</summary>
    public int Fadeout;

    /// <summary>0 cut, 1 continue, 2 note off, 3 fade.</summary>
    public int NewNoteAction;

    /// <summary>Duplicate check: 0 off, 1 note, 2 sample, 3 instrument; and its action: 0 cut, 1 off, 2 fade.</summary>
    public int DuplicateCheck, DuplicateAction;

    /// <summary>0-128.</summary>
    public int GlobalVolume = 128;

    /// <summary>0-256, or -1 to take the channel's.</summary>
    public int Pan = -1;

    /// <summary>Impulse Tracker's filter as a note of it starts: cutoff and resonance 0-127; -1 to
    /// leave the channel's as it is.</summary>
    public int FilterCutoff = -1, FilterResonance = -1;
}

/// <summary>A pattern: its rows of cells, [row * channels + channel].</summary>
public sealed class SongPattern(int rows, int channels)
{
    public int Rows { get; } = rows;
    public SongCell[] Cells { get; } = new SongCell[rows * channels];
}

/// <summary>
/// A module of a PC tracker - Scream Tracker 3, FastTracker 2 or Impulse Tracker - read whole: its
/// patterns as written, its samples and instruments, and how it starts: speed, tempo, volumes and
/// each channel's panning. Played by <see cref="SongPlayer"/>.
/// </summary>
public sealed partial class Song : ITrackerModule
{
    private static readonly string[] NoteNames = ["C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-"];

    public SongFormat Type { get; private init; }

    public string Title { get; private init; } = "";

    public string Format => Type switch { SongFormat.S3m => "S3M", SongFormat.Xm => "XM", _ => "IT" };

    public string Kind => Type switch
    {
        SongFormat.S3m => "Scream Tracker 3 module",
        SongFormat.Xm => "FastTracker 2 module",
        _ => "Impulse Tracker module",
    };

    /// <summary>The tracker that made it, as the file says.</summary>
    public string Tracker { get; private init; } = "";

    public string MadeWith => Tracker;

    public string TrackerName => Type switch
    {
        SongFormat.S3m => "Scream Tracker 3",
        SongFormat.Xm => "FastTracker 2",
        _ => "Impulse Tracker",
    };

    public int Channels { get; private init; }

    public IReadOnlyList<int> Order { get; private init; } = [];

    public int Restart { get; private init; }

    public IReadOnlyList<SongPattern> Patterns { get; private init; } = [];

    public IReadOnlyList<SongSample> SampleList { get; private init; } = [];

    public IReadOnlyList<SongInstrument> Instruments { get; private init; } = [];

    /// <summary>Whether the patterns name instruments - XM always, Impulse Tracker when its flag says.</summary>
    public bool UsesInstruments => Instruments.Count > 0;

    public int InitialSpeed { get; private init; } = 6;

    public int InitialTempo { get; private init; } = 125;

    /// <summary>0-128.</summary>
    public int GlobalVolume { get; private init; } = 128;

    /// <summary>The song's own loudness, 0-128: Impulse Tracker's mixing volume, Scream Tracker's master volume.</summary>
    public int MixVolume { get; private init; } = 128;

    /// <summary>Pitch slides in steps of a sixty-fourth of a semitone, not of the Amiga's period.</summary>
    public bool LinearSlides { get; private init; }

    /// <summary>Impulse Tracker's "old effects" and "compatible Gxx".</summary>
    public bool OldEffects { get; private init; }

    public bool CompatibleGxx { get; private init; }

    /// <summary>Each channel's panning, 0-256, as it starts.</summary>
    public IReadOnlyList<int> ChannelPan { get; private init; } = [];

    /// <summary>Each channel's volume, 0-64, as it starts (Impulse Tracker's).</summary>
    public IReadOnlyList<int> ChannelVolume { get; private init; } = [];

    /// <summary>How loud it is mixed, against a ProTracker module: each tracker's own level, as
    /// libopenmpt plays them - and an Impulse Tracker file saved by IT 2.14 or later as compatible
    /// with an earlier one at a third of it, as libopenmpt has it.</summary>
    public double Level { get; private init; } = 1;

    /// <summary>Played in stereo; a mono Scream Tracker module is played in the middle.</summary>
    public bool Stereo { get; private init; } = true;

    public string Message { get; private init; } = "";

    /// <summary>Whether <paramref name="bytes"/> are a Scream Tracker 3, FastTracker 2 or Impulse
    /// Tracker module, by its signature.</summary>
    public static bool Looks(byte[] bytes) =>
        bytes.Length >= 64 && Encoding.ASCII.GetString(bytes, 0, 17) == "Extended Module: "
        || bytes.Length >= 0xC0 && bytes[0] == 'I' && bytes[1] == 'M' && bytes[2] == 'P' && bytes[3] == 'M'
        || bytes.Length >= 0x60 && bytes[0x2C] == 'S' && bytes[0x2D] == 'C' && bytes[0x2E] == 'R' && bytes[0x2F] == 'M';

    public static Song Parse(byte[] bytes)
    {
        if (bytes.Length >= 64 && Encoding.ASCII.GetString(bytes, 0, 17) == "Extended Module: ")
            return ParseXm(bytes);
        if (bytes.Length >= 0xC0 && bytes[0] == 'I' && bytes[1] == 'M' && bytes[2] == 'P' && bytes[3] == 'M')
            return ParseIt(bytes);
        if (bytes.Length >= 0x60 && bytes[0x2C] == 'S' && bytes[0x2D] == 'C' && bytes[0x2E] == 'R' && bytes[0x2F] == 'M')
            return ParseS3m(bytes);
        throw new InvalidDataException("Not a Scream Tracker 3, FastTracker 2 or Impulse Tracker module.");
    }

    // ------------------------------------------------------------------ as any module

    public int PatternCount => Patterns.Count;

    public int RowsIn(int pattern) => pattern < Patterns.Count ? Patterns[pattern].Rows : 64;

    public int CellChars => 13;

    public SongCell Cell(int pattern, int row, int channel) =>
        pattern < Patterns.Count && row < Patterns[pattern].Rows ? Patterns[pattern].Cells[row * Channels + channel] : Empty;

    private SongCell Empty => new(0, 0, Type == SongFormat.Xm ? (byte)0 : SongCell.NoVolume, 0, 0);

    public bool CellEmpty(int pattern, int row, int channel) => Cell(pattern, row, channel) == Empty;

    /// <summary>A cell as its tracker writes it: the note, the instrument, the volume column and the
    /// effect - "C-4 01 40 A0F" - dots where there is nothing.</summary>
    public string CellText(int pattern, int row, int channel)
    {
        var cell = Cell(pattern, row, channel);
        string note = cell.Note switch
        {
            0 => "...",
            SongCell.NoteOff => Type == SongFormat.Xm ? "===" : "===",
            SongCell.NoteCut => "^^^",
            >= SongCell.NoteFade => "~~~",
            _ when cell.Note <= 120 => $"{NoteNames[(cell.Note - 1) % 12]}{(cell.Note - 1) / 12}",
            _ => "~~~",
        };
        string instrument = cell.Instrument > 0 ? $"{cell.Instrument:X2}" : "..";
        if (Type != SongFormat.Xm && cell.Instrument > 0)
            instrument = cell.Instrument < 100 ? $"{cell.Instrument:00}" : $"{cell.Instrument:X2}";
        return $"{note} {instrument} {VolumeText(cell.Volume)} {EffectText(cell.Effect, cell.Parameter)}";
    }

    private string VolumeText(byte v)
    {
        if (Type == SongFormat.Xm)
        {
            return v switch
            {
                < 0x10 => "..",
                <= 0x50 => $"{v - 0x10:X2}",
                _ => $"{"-+dulvpLRM"[(v >> 4) - 6]}{v & 0x0F:X}",
            };
        }
        return v switch
        {
            SongCell.NoVolume => "..",
            <= 64 => $"{v:00}",
            <= 74 => $"a{v - 65}",
            <= 84 => $"b{v - 75}",
            <= 94 => $"c{v - 85}",
            <= 104 => $"d{v - 95}",
            <= 114 => $"e{v - 105}",
            <= 124 => $"f{v - 115}",
            >= 128 and <= 192 => $"p{Math.Min(9, (v - 128) / 7)}",
            >= 193 and <= 202 => $"g{v - 193}",
            >= 203 and <= 212 => $"h{v - 203}",
            _ => "..",
        };
    }

    private string EffectText(byte effect, byte parameter)
    {
        if (Type == SongFormat.Xm)
        {
            if (effect == 0 && parameter == 0)
                return "...";
            char letter = effect < 10 ? (char)('0' + effect) : (char)('A' + effect - 10);
            return $"{letter}{parameter:X2}";
        }
        return effect is 0 or > 26 ? (parameter == 0 ? "..." : $".{parameter:X2}") : $"{(char)('A' + effect - 1)}{parameter:X2}";
    }

    public string SamplesHeading => UsesInstruments ? "INSTRUMENTS" : "SAMPLES";

    /// <summary>The instruments - each with the bytes of its samples - or the samples.</summary>
    IReadOnlyList<ModuleSample> ITrackerModule.Samples => _listed ??= UsesInstruments
        ? Instruments.Select(i => new ModuleSample(i.Name, i.SampleOf.Where(s => s > 0).Distinct()
              .Sum(s => s <= SampleList.Count ? SampleList[s - 1].Data.Length * 2 : 0))).ToArray()
        : SampleList.Select(s => new ModuleSample(s.Name, s.Data.Length * 2)).ToArray();

    IReadOnlyList<ModuleSample> ITrackerModule.SoundSamples => SampleList.Select(s => new ModuleSample(s.Name, s.Data.Length * 2)).ToArray();

    private ModuleSample[]? _listed;

    public ITrackerPlayer CreatePlayer(int sampleRate = 44100, double stereoSeparation = 0.6) =>
        new SongPlayer(this, sampleRate, stereoSeparation);

    // ------------------------------------------------------------------ reading

    private static int Word(byte[] b, int at) => at + 1 < b.Length ? b[at] | (b[at + 1] << 8) : 0;

    private static int Long(byte[] b, int at) => at + 3 < b.Length ? b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24) : 0;

    private static string Text(byte[] b, int at, int length)
    {
        var text = new StringBuilder();
        for (int i = at; i < at + length && i < b.Length && b[i] != 0; i++)
            text.Append(b[i] is >= 32 and < 127 ? (char)b[i] : ' ');
        return text.ToString().TrimEnd();
    }
}
