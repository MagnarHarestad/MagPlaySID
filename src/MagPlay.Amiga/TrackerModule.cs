namespace MagPlay.Amiga;

/// <summary>A sample or instrument as a module lists it: its name - often a line of the composer's
/// message instead - and how many bytes of sound it has.</summary>
public readonly record struct ModuleSample(string Name, int Bytes);

/// <summary>What a channel is playing at a moment: its sample or instrument, its note - in
/// semitones, ProTracker's C-2 and a PC tracker's middle C as 48 - its volume 0-64, and whether it
/// sounds.</summary>
public readonly record struct ChannelState(int Sample, double Note, int Volume, bool Sounding);

/// <summary>
/// A tracker module of any kind the app plays - ProTracker's from the Amiga, Scream Tracker 3's,
/// FastTracker 2's and Impulse Tracker's from the PC - as the window, its views and its export see
/// one: its patterns as their tracker writes them, its samples or instruments, and a player.
/// </summary>
public interface ITrackerModule
{
    string Title { get; }

    /// <summary>What the file says it is: ProTracker's four-letter tag, "S3M", "XM" or "IT".</summary>
    string Format { get; }

    /// <summary>What kind of module it is, in words: "Amiga module", "FastTracker 2 module".</summary>
    string Kind { get; }

    /// <summary>The tracker it is played as: "ProTracker", "Scream Tracker 3", "FastTracker 2",
    /// "Impulse Tracker".</summary>
    string TrackerName { get; }

    /// <summary>The tracker that made the file, as it says; empty when it does not.</summary>
    string MadeWith { get; }

    int Channels { get; }

    /// <summary>The patterns in the order played - with, for Scream and Impulse Tracker, their
    /// markers: 254 skipped, 255 the song's end.</summary>
    IReadOnlyList<int> Order { get; }

    /// <summary>The order position the song goes back to at its end.</summary>
    int Restart { get; }

    int PatternCount { get; }

    /// <summary>How many rows a pattern has: 64 on the Amiga, up to 256 on the PC.</summary>
    int RowsIn(int pattern);

    /// <summary>Characters a cell's text takes.</summary>
    int CellChars { get; }

    /// <summary>A cell as its tracker writes it: "C-2 1F A0F" on the Amiga, "C-5 01 64 A0F" in
    /// Impulse Tracker; dots and dashes where there is nothing.</summary>
    string CellText(int pattern, int row, int channel);

    bool CellEmpty(int pattern, int row, int channel);

    /// <summary>"SAMPLES", or "INSTRUMENTS" for a module that plays its samples through them.</summary>
    string SamplesHeading { get; }

    /// <summary>The samples or instruments, numbered from 1 as its patterns name them.</summary>
    IReadOnlyList<ModuleSample> Samples { get; }

    /// <summary>The sound's own samples, when the list above is of instruments: their names too.</summary>
    IReadOnlyList<ModuleSample> SoundSamples { get; }

    /// <summary>The composer's message: Impulse Tracker's own; empty for the rest.</summary>
    string Message { get; }

    ITrackerPlayer CreatePlayer(int sampleRate = 44100, double stereoSeparation = 0.6);
}

/// <summary>A module playing: its sound, a stereo frame at a time, with what each channel plays.</summary>
public interface ITrackerPlayer
{
    /// <summary>Left and right apart, 0 mono to 1 as the module has them.</summary>
    double StereoSeparation { get; set; }

    void SetMuted(int channel, bool muted);

    /// <summary>The song came round to a row played before - its end, though it goes on.</summary>
    bool Looped { get; }

    /// <summary>The song stopped itself.</summary>
    bool Stopped { get; }

    /// <summary>Renders stereo frames, from -1 to 1. Given <paramref name="channels"/>, each
    /// channel's own sound from <paramref name="at"/> on; given <paramref name="states"/>, what it
    /// plays - see <see cref="TrackerModules.Pack"/>; given <paramref name="rows"/>, the place heard -
    /// see <see cref="TrackerModules.UnpackRow"/>.</summary>
    void Render(Span<float> left, Span<float> right, float[][]? channels = null, int[][]? states = null, int[]? rows = null, int at = 0);

    /// <summary>One tick played with no sound made: how long it lasts, in seconds.</summary>
    double SilentTick();

    /// <summary>On <paramref name="frames"/> frames, unheard - for a jump: every voice moved on through
    /// its sample as if it had sounded, so what plays from there is what would have.</summary>
    void Skip(long frames);
}

/// <summary>Modules of every kind: told apart, read, measured; their channels' states and places
/// packed into numbers for rings.</summary>
public static class TrackerModules
{
    /// <summary>Whether <paramref name="bytes"/> are a module of a kind played here.</summary>
    public static bool Looks(byte[] bytes) => Song.Looks(bytes) || ModFile.Looks(bytes);

    /// <summary>The module <paramref name="bytes"/> are; InvalidDataException when it is none.</summary>
    public static ITrackerModule Parse(byte[] bytes) => Song.Looks(bytes) ? Song.Parse(bytes) : ModFile.Parse(bytes);

    /// <summary>The file names modules have: *.mod, *.s3m, *.xm, *.it, *.mptm - and the Amiga's mod.*.</summary>
    public static bool IsModuleName(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("mod.", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(name).ToLowerInvariant() is ".mod" or ".s3m" or ".xm" or ".it" or ".mptm";
    }

    public static TimeSpan? Measure(ITrackerModule module) => MeasureEnd(module)?.Length;

    /// <summary>How long the song plays before it comes round to a row played before, or stops -
    /// played through with no sound made - and whether it stops there; null when it does neither
    /// within an hour.</summary>
    public static (TimeSpan Length, bool Stops)? MeasureEnd(ITrackerModule module)
    {
        var player = module.CreatePlayer(1000);
        double seconds = 0;
        while (!player.Looped && !player.Stopped && seconds < 3600)
            seconds += player.SilentTick();
        return player.Looped || player.Stopped ? (TimeSpan.FromSeconds(seconds), player.Stopped && !player.Looped) : null;
    }

    /// <summary>A channel's state as one number: its note in eighths of a semitone in bits 0-10,
    /// volume 0-64 in 11-17, whether it sounds in 18, its sample or instrument in 19-26, and in 27
    /// whether a note started on it this tick.</summary>
    public static int Pack(int sample, double note, int volume, bool sounding, bool struck) =>
        Math.Clamp((int)Math.Round(note * 8), 0, 2047) | (Math.Clamp(volume, 0, 64) << 11) | ((sounding ? 1 : 0) << 18)
        | ((sample & 0xFF) << 19) | ((struck ? 1 : 0) << 27);

    public static ChannelState Unpack(int state) =>
        new((state >> 19) & 0xFF, (state & 0x7FF) / 8.0, (state >> 11) & 0x7F, (state >> 18 & 1) != 0);

    /// <summary>Whether a packed state is a note's first tick.</summary>
    public static bool Struck(int state) => (state >> 27 & 1) != 0;

    /// <summary>The place heard as one number: the row in bits 0-7, the order position in 8-15, and
    /// how far into the row, 0-1, in 16-25 - for rows that scroll smoothly.</summary>
    public static int PackRow(int order, int row, double into) =>
        (int)(Math.Clamp(into, 0, 1) * 1023) << 16 | (order & 0xFF) << 8 | (row & 0xFF);

    public static (int Order, int Row, double Into) UnpackRow(int packed) =>
        ((packed >> 8) & 0xFF, packed & 0xFF, (packed >> 16 & 0x3FF) / 1023.0);
}
