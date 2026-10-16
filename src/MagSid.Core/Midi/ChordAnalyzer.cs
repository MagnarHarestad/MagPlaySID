using MagSid.Core.Model;

namespace MagSid.Core.Midi;

public enum ChordQuality { Major, Minor, Diminished, Augmented, Sus4, Dominant7, Major7, Minor7 }

/// <summary>
/// A chord as a root and a quality - the same chord in any inversion, so C-E-G, E-G-C and G-C-E
/// are one chord and become one instrument.
/// </summary>
public sealed record Chord(int Root, ChordQuality Quality)
{
    private static readonly string[] PitchNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public IReadOnlyList<int> Intervals => Quality switch
    {
        ChordQuality.Major => [0, 4, 7],
        ChordQuality.Minor => [0, 3, 7],
        ChordQuality.Diminished => [0, 3, 6],
        ChordQuality.Augmented => [0, 4, 8],
        ChordQuality.Sus4 => [0, 5, 7],
        ChordQuality.Dominant7 => [0, 4, 7, 10],
        ChordQuality.Major7 => [0, 4, 7, 11],
        _ => [0, 3, 7, 10],
    };

    public IEnumerable<int> PitchClasses => Intervals.Select(i => (Root + i) % 12);

    public string Name => PitchNames[Root] + Quality switch
    {
        ChordQuality.Major => "",
        ChordQuality.Minor => "m",
        ChordQuality.Diminished => "dim",
        ChordQuality.Augmented => "aug",
        ChordQuality.Sus4 => "sus4",
        ChordQuality.Dominant7 => "7",
        ChordQuality.Major7 => "maj7",
        _ => "m7",
    };

    /// <summary>The arpeggio, rising from a root between F#3 and F-4 - the middle of the range,
    /// where the demo songs keep theirs.</summary>
    public IReadOnlyList<NoteName> Notes
    {
        get
        {
            int root = Root <= 5 ? 48 + Root : 36 + Root;
            return Intervals.Select(i => NoteName.AllNotes[root + i]).ToList();
        }
    }
}

/// <summary>
/// Names the chord a stretch of music plays. Every pitch class sounding is weighted by how long
/// it sounds, the lowest note a little more - it is usually the root - and each possible chord
/// is scored by the weight it explains against the weight it leaves out. Every chord is major or
/// minor, and a seventh is added only when that note is properly there, which keeps the number
/// of different chords - and so instruments - down.
/// </summary>
public static class ChordAnalyzer
{
    /// <summary>
    /// Only the two shapes every chord is heard as: 0-4-7 and 0-3-7. Their inversions are the
    /// same chords - 0-3-8 and 0-5-9 are 0-4-7, 0-4-9 and 0-5-8 are 0-3-7 - because a chord is
    /// matched by its pitch classes, not by which is lowest. A sus4, diminished or augmented
    /// chord is read as the major or minor chord it shares most with: fewer kinds of chord,
    /// fewer instruments.
    /// </summary>
    private static readonly ChordQuality[] Triads = [ChordQuality.Major, ChordQuality.Minor];

    /// <summary>The chord <paramref name="weights"/> (twelve pitch classes) plays, or null when
    /// nothing sounds. Fewer than three pitch classes do not make a chord of their own, so they
    /// keep <paramref name="previous"/> when it holds them. The seventh is judged on
    /// <paramref name="seventhWeights"/> when given - the accompaniment alone, without a tune's
    /// passing notes.</summary>
    public static Chord? Identify(double[] weights, Chord? previous, int maxNotes = 4, double[]? seventhWeights = null)
    {
        double total = weights.Sum();
        if (total <= 0)
            return null;

        var present = Enumerable.Range(0, 12).Where(pc => weights[pc] > 0).ToList();
        if (present.Count < 3 && previous is not null && present.All(previous.PitchClasses.Contains))
            return previous;

        Chord? best = null;
        double bestScore = double.MinValue;
        foreach (var quality in Triads)
        {
            for (int root = 0; root < 12; root++)
            {
                var chord = new Chord(root, quality);
                var tones = chord.PitchClasses.ToHashSet();
                double inside = tones.Sum(pc => weights[pc]);
                double missing = tones.Count(pc => weights[pc] <= 0);

                // Leaving notes out costs, and so does a chord tone that is not played; major
                // wins a tie, being the order of Triads.
                double score = inside - 0.7 * (total - inside) - 0.3 * total / 3 * missing
                               + (weights[root] > 0 ? 0.05 * total : 0);
                if (score > bestScore + 1e-9)
                {
                    best = chord;
                    bestScore = score;
                }
            }
        }

        if (best is null || maxNotes < 4)
            return best;

        // A seventh only when it is a real voice of the chord, not a passing note.
        var voiced = seventhWeights ?? weights;
        double strong = 0.35 * voiced.Max();
        var seventh = best.Quality switch
        {
            ChordQuality.Major when strong > 0 && voiced[(best.Root + 10) % 12] >= strong => ChordQuality.Dominant7,
            ChordQuality.Major when strong > 0 && voiced[(best.Root + 11) % 12] >= strong => ChordQuality.Major7,
            ChordQuality.Minor when strong > 0 && voiced[(best.Root + 10) % 12] >= strong => ChordQuality.Minor7,
            _ => (ChordQuality?)null,
        };
        return seventh is { } q ? best with { Quality = q } : best;
    }

    /// <summary>
    /// How strongly a part carries the harmony: the beats in which it sounds three or more
    /// different pitch classes, held or broken. Used to find the part to read chords from.
    /// </summary>
    public static int ChordBeats(MidiSource source, int ticksPerQuarter)
    {
        var beats = new Dictionary<long, HashSet<int>>();
        foreach (var note in source.Notes)
        {
            for (long beat = note.Start / ticksPerQuarter; beat * ticksPerQuarter < note.End; beat++)
            {
                if (!beats.TryGetValue(beat, out var classes))
                    beats[beat] = classes = [];
                classes.Add(note.Key % 12);
            }
        }
        return beats.Values.Count(c => c.Count >= 3);
    }
}
