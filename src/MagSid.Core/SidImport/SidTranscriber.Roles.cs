using MagSid.Core.Model;

namespace MagSid.Core.SidImport;

/// <summary>
/// What a sound is for in the tune - in the order the sixteen instrument places are handed out: a
/// snare first, then the bass and the kick-bass, then the lead, then arpeggios. A song without its
/// snare or its bass is no longer the song; one with its arpeggio played on the nearest lead still is.
/// </summary>
public enum SoundRole
{
    Snare,
    KickBass,
    Bass,
    Lead,
    Arp,
    Other,
}

public static partial class SidTranscriber
{
    /// <summary>C-3: a bass plays at or below it, a lead above.</summary>
    private const int BassCeiling = 3 * 12;

    /// <summary>E-3 - GoatTracker's absolute $A8: a kick's falling notes are below it.</summary>
    private const int KickCeiling = 3 * 12 + 4;

    /// <summary>$20..$30 of the note table, G#2..C-4: where a snare's tonal frames sit.</summary>
    private const int SnareLow = 0x20, SnareHigh = 0x30;

    /// <summary>Where a sound's notes fall, how high, and how long - what its role is read from
    /// besides the sound itself.</summary>
    private sealed class Usage
    {
        public List<int> Rows { get; } = [];
        public List<int> Roots { get; } = [];
        public List<int> Lengths { get; } = [];
    }

    private static int Priority(SoundRole role) => role switch
    {
        SoundRole.Snare => 0,
        SoundRole.KickBass or SoundRole.Bass => 1,
        SoundRole.Lead => 2,
        SoundRole.Arp => 3,
        _ => 4,
    };

    /// <summary>
    /// The sound's role, from what a musician would listen for:
    /// <list type="bullet">
    /// <item>a snare starts on noise, goes tonal around $20-$30 and ends on noise again (or loops
    /// back to it, or rings) - and sits on the same step of every bar: rows 4 and 12 of sixteen, or
    /// 3 and 9 of a 3/4 bar. Noise alone is a snare only on that step; elsewhere it is a hi-hat.</item>
    /// <item>a kick-bass strikes noise on a high fixed note, falls through fixed notes below E-3, and
    /// settles on a root at C-3 or below;</item>
    /// <item>a bass is rooted at C-3 or below, with or without a frame of hi-hat noise first;</item>
    /// <item>an arpeggio cycles its note program, or is played by hand - short notes, many pitches;</item>
    /// <item>a lead is rooted above C-3.</item>
    /// </list>
    /// </summary>
    /// <param name="family">Where every sound with this one's waveforms and envelope falls - its
    /// variants on other notes and pitch programs together. A snare's rhythm is read from them all:
    /// a variant heard once sits "on the backbeat" by chance.</param>
    private static SoundRole RoleOf(Sound sound, Usage usage, List<int> family)
    {
        int[] waves = [.. sound.Controls.Select(c => c & 0xF0).Where(w => w != 0)];
        bool Noise(int w) => (w & 0x80) != 0;
        bool Tonal(int w) => (w & 0x70) != 0 && !Noise(w);

        int root = Median([.. usage.Roots]);
        int length = Median([.. usage.Lengths]);
        bool startsOnNoise = waves.Length > 0 && Noise(waves[0]);
        int firstTonal = Array.FindIndex(waves, Tonal);
        bool tonal = firstTonal >= 0;
        bool rings = sound.Controls.Any(c => (c & 0x06) != 0);

        // Snare: noise, tonal, noise again - or a loop back to noise, ringing on the way - on the
        // backbeat. Its tonal frames, when it says them, are around $20-$30.
        bool backToNoise = tonal && (waves.Skip(firstTonal).Any(Noise)
            || sound.Ending == ArpeggioEnding.Loop && sound.Controls.Skip(sound.LoopPoint).Any(c => Noise(c & 0xF0)));
        bool snareNotes = sound.Notes is null
            || sound.Notes.Any(n => n.TableIndex is >= SnareLow and <= SnareHigh)
            || root is >= SnareLow and <= SnareHigh;
        double onBackbeat = family.Count >= 4 ? Backbeat(family) : 0;
        if (startsOnNoise && backToNoise && snareNotes && (onBackbeat >= 0.5 || family.Count < 4))
            return SoundRole.Snare;
        if (startsOnNoise && !tonal && onBackbeat >= 0.7)
            return SoundRole.Snare;

        // Kick-bass: a strike on noise at a high fixed note, falling to a root at C-3 or below.
        if (startsOnNoise && tonal && root <= BassCeiling && sound.Notes is { Length: >= 1 } steps
            && steps[0].TableIndex >= 5 * 12)
            return SoundRole.KickBass;

        if (root <= BassCeiling && tonal)
            return SoundRole.Bass;

        // Arpeggios: cycled by the instrument, or played by hand.
        if (sound.Notes is not null && sound.Ending == ArpeggioEnding.Loop)
            return SoundRole.Arp;
        if (length <= 3 && usage.Roots.Distinct().Count() >= 3)
            return SoundRole.Arp;

        if (tonal)
            return SoundRole.Lead;

        return SoundRole.Other;
    }

    /// <summary>A sound's waveforms and envelope, whatever notes it plays - its family.</summary>
    private static string FamilyOf(Sound sound) =>
        $"{Convert.ToHexString(sound.Controls)}|{sound.AttackDecay}|{sound.SustainRelease}";

    /// <summary>
    /// The sixteen places, handed out in turns: each round the snare, the kick-bass, the bass, the
    /// lead and the arpeggio take one each, in that order, the sound of the role used most first.
    /// So a tune with a dozen snare variants still keeps its bass and its lead, and when places run
    /// out it is the arpeggios' last variants that go.
    /// </summary>
    private static List<Sound> TakeInTurn(Dictionary<Sound, Sound> sounds, Dictionary<Sound, SoundRole> roles)
    {
        var queues = Enum.GetValues<SoundRole>().OrderBy(Priority).ThenBy(r => r)
            .Select(role => new Queue<Sound>(sounds.Keys.Where(k => roles[k] == role).OrderByDescending(k => sounds[k].Uses)))
            .ToList();

        var kept = new List<Sound>();
        while (kept.Count < Validation.SongValidator.MaxInstrumentNumber && queues.Any(q => q.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (queue.Count > 0 && kept.Count < Validation.SongValidator.MaxInstrumentNumber)
                    kept.Add(queue.Dequeue());
            }
        }

        return kept;
    }

    /// <summary>
    /// How much of a sound sits on one step of the bar: the share of its notes on the step most of
    /// them share, counted in bars of eight rows (4 and 12 of sixteen are the same step) or six (3
    /// and 9 of twelve). A snare is near 1; a part that plays everywhere is spread out.
    /// </summary>
    private static double Backbeat(List<int> rows)
    {
        if (rows.Count == 0)
            return 0;

        double Best(int bar) => rows.GroupBy(r => r % bar).Max(g => g.Count()) / (double)rows.Count;
        return Math.Max(Best(8), Best(6));
    }

    private static string RoleName(SoundRole role, Sound sound) => role switch
    {
        SoundRole.Snare => "Snare",
        SoundRole.KickBass => "KickBass",
        SoundRole.Bass => "Bass",
        SoundRole.Lead => "Lead",
        SoundRole.Arp => "Arp",
        _ => sound.Controls.All(c => (c & 0xF0) is 0 or 0x80) ? "Hihat" : "Sound",
    };

    /// <summary>"2 snares, 1 kick-bass, 3 basses, 5 leads, 2 arpeggios, 3 other" - for the report.</summary>
    private static string DescribeRoles(IEnumerable<SoundRole> roles)
    {
        var counts = roles.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        string Count(SoundRole role, string one, string many) =>
            counts.TryGetValue(role, out int n) ? $"{n} {(n == 1 ? one : many)}" : "";

        return string.Join(", ", new[]
        {
            Count(SoundRole.Snare, "snare", "snares"),
            Count(SoundRole.KickBass, "kick-bass", "kick-basses"),
            Count(SoundRole.Bass, "bass", "basses"),
            Count(SoundRole.Lead, "lead", "leads"),
            Count(SoundRole.Arp, "arpeggio", "arpeggios"),
            Count(SoundRole.Other, "other", "other"),
        }.Where(s => s.Length > 0));
    }
}
