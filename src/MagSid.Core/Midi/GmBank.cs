using MagSid.Core.Model;
using MagSid.Core.Serialization;

namespace MagSid.Core.Midi;

/// <summary>
/// The sound a MIDI part gets when nobody picks one: its General MIDI program, by family, mapped
/// to an instrument from the demo songs. A General MIDI program is one of 128, in 16 families of
/// 8 - pianos, organs, guitars, basses and so on - and one demo-song sound stands for each family.
///
/// The sounds ship inside the editor as instrument files. A folder of the user's own can replace
/// any of them: a file there named after a sound's key - Piano.json, Bass.json - is used instead,
/// so saving an instrument under that name is all it takes to change what a piano becomes.
///
/// Beside the 14 family sounds the bank carries drums and arpeggios from the demo songs. No GM
/// program maps to them - they are there to be picked by hand from the sound bank.
/// </summary>
public sealed class GmBank
{
    /// <summary>One sound of the bank and the demo song it comes from.</summary>
    public sealed record Sound(string Key, string Origin, Instrument Instrument)
    {
        /// <summary>The group the sound bank shows it under.</summary>
        public BankSection Section => Sounds.First(s => s.Key == Key).Section;
    }

    public enum BankSection { Instruments, Drums, Arpeggios }

    private static readonly (string Key, string Origin, BankSection Section)[] Sounds =
    [
        ("Piano", "Magnificent", BankSection.Instruments), ("Bells", "Magnificent", BankSection.Instruments),
        ("Organ", "CityBomber4k", BankSection.Instruments), ("Guitar", "Breakdance", BankSection.Instruments),
        ("Bass", "Magnificent", BankSection.Instruments), ("SynthBass", "Clarity", BankSection.Instruments),
        ("Strings", "Fairlighty", BankSection.Instruments), ("Brass", "Airstrike", BankSection.Instruments),
        ("Reed", "BellaCiao", BankSection.Instruments), ("Flute", "Clarity", BankSection.Instruments),
        ("SynthLead", "Lightforce", BankSection.Instruments), ("SynthPad", "Clarity", BankSection.Instruments),
        ("SynthFx", "Breakdance", BankSection.Instruments),

        ("Kick", "Lightforce", BankSection.Drums), ("KickBass", "Magnificent", BankSection.Drums),
        ("Snare", "Magnificent", BankSection.Drums), ("Snare2", "Lightforce", BankSection.Drums),
        ("Snare3", "Clarity", BankSection.Drums), ("Noise", "Breakdance", BankSection.Drums),

        // The same chord at three speeds: each note held one, two or three frames.
        ("ArpMinor", "Magnificent", BankSection.Arpeggios), ("ArpMinor2", "Magnificent", BankSection.Arpeggios),
        ("ArpMinor3", "Magnificent", BankSection.Arpeggios), ("ArpMajor", "Magnificent", BankSection.Arpeggios),
        ("ArpMajor2", "Magnificent", BankSection.Arpeggios), ("ArpMajor3", "Magnificent", BankSection.Arpeggios),
    ];

    /// <summary>The 16 GM families in program order, eight programs each.</summary>
    private static readonly string[] Families =
    [
        "Piano", "Bells", "Organ", "Guitar", "Bass", "Strings", "Strings", "Brass",
        "Reed", "Flute", "SynthLead", "SynthPad", "SynthFx", "SynthFx", "Bells", "Noise",
    ];

    private readonly Dictionary<string, Sound> _sounds;

    private GmBank(Dictionary<string, Sound> sounds) => _sounds = sounds;

    /// <summary>The sounds as they ship.</summary>
    public static GmBank BuiltIn { get; } = Load(null);

    public IEnumerable<Sound> All => Sounds.Select(s => _sounds[s.Key]);

    /// <summary>The built-in sounds, each replaced by a readable file of the same key in
    /// <paramref name="overrideDirectory"/> when there is one. A file that cannot be read is
    /// passed over: a broken override must not stop an import.</summary>
    public static GmBank Load(string? overrideDirectory)
    {
        var assembly = typeof(GmBank).Assembly;
        var sounds = new Dictionary<string, Sound>();
        foreach (var (key, origin, _) in Sounds)
        {
            string? own = overrideDirectory is null ? null : Path.Combine(overrideDirectory, key + ".json");
            if (own is not null && File.Exists(own))
            {
                try
                {
                    sounds[key] = new Sound(key, "your bank", InstrumentFile.Read(File.ReadAllText(own)));
                    continue;
                }
                catch (Exception ex) when (ex is InstrumentFileException or IOException or UnauthorizedAccessException)
                {
                }
            }

            using var stream = assembly.GetManifestResourceStream($"Bank.{key}.json")
                ?? throw new InvalidOperationException($"The built-in sound {key} is missing.");
            sounds[key] = new Sound(key, origin, InstrumentFile.Read(new StreamReader(stream).ReadToEnd()));
        }
        return new GmBank(sounds);
    }

    /// <summary>The bank key a General MIDI program (0-127) plays as. Synth bass (38, 39) has a
    /// sound of its own; everything else goes by family.</summary>
    public static string KeyFor(int program) => program switch
    {
        38 or 39 => "SynthBass",
        _ => Families[Math.Clamp(program, 0, 127) / 8],
    };

    /// <summary>The sound for <paramref name="program"/>, or null when the part names none.</summary>
    public Sound? For(int? program) => program is { } p ? _sounds[KeyFor(p)] : null;

    /// <summary>What a sound of the bank is, by its key, for a menu or a card.</summary>
    public static string Label(string key) => key switch
    {
        "SynthBass" => "Synth bass",
        "SynthLead" => "Synth lead",
        "SynthPad" => "Synth pad",
        "SynthFx" => "Synth FX",
        "KickBass" => "Kick bass",
        "Snare2" or "Snare3" => "Snare",
        "Noise" => "Hi-hat",
        "ArpMinor" => "Arp minor, 1 frame",
        "ArpMinor2" => "Arp minor, 2 frames",
        "ArpMinor3" => "Arp minor, 3 frames",
        "ArpMajor" => "Arp major, 1 frame",
        "ArpMajor2" => "Arp major, 2 frames",
        "ArpMajor3" => "Arp major, 3 frames",
        _ => key,
    };

    /// <summary>General MIDI's names for its 16 families, for showing a program.</summary>
    public static string FamilyName(int program) => new[]
    {
        "Piano", "Chromatic perc.", "Organ", "Guitar", "Bass", "Strings", "Ensemble", "Brass",
        "Reed", "Pipe", "Synth lead", "Synth pad", "Synth FX", "Ethnic", "Percussive", "Sound FX",
    }[Math.Clamp(program, 0, 127) / 8];
}
