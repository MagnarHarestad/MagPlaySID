namespace MagPlay.Score;

/// <summary>A row as it is played: the call it starts on, and where in the score it is.</summary>
public readonly record struct PlayedRow(int Call, int Entry, int Transpose, int Pattern, int Row);

/// <summary>
/// A score played once, silently, call by call, keeping where it is: the cycle of each call, each
/// voice's position after it, the filter program's step, and each voice's rows in the order they
/// are played. What a view that follows the music reads - the sound is the tune's own, the
/// timeline only says where in the score that sound is.
/// </summary>
public sealed class ScoreTimeline
{
    private readonly long[] _cycles;
    private readonly VoicePosition[][] _voices;
    private readonly int[][] _filterSteps;

    private ScoreTimeline(long[] cycles, VoicePosition[][] voices, int[][] filterSteps, IReadOnlyList<PlayedRow>[] rows)
    {
        _cycles = cycles;
        _voices = voices;
        _filterSteps = filterSteps;
        Rows = rows;
    }

    public int Calls => _cycles.Length;

    /// <summary>The voices: three a chip.</summary>
    public int Voices => _voices.Length;

    /// <summary>Each voice's rows, in the order they are played.</summary>
    public IReadOnlyList<PlayedRow>[] Rows { get; }

    /// <summary>The cycle call <paramref name="call"/> happens on.</summary>
    public long CycleOf(int call) => _cycles[Math.Clamp(call, 0, _cycles.Length - 1)];

    /// <summary>The call sounding at <paramref name="cycle"/>: the last one made by then; -1 before the first.</summary>
    public int CallAt(long cycle)
    {
        int at = Array.BinarySearch(_cycles, cycle);
        return at >= 0 ? at : ~at - 1;
    }

    public VoicePosition PositionOf(int voice, int call) => _voices[voice][Math.Clamp(call, 0, _cycles.Length - 1)];

    /// <summary>The step of <paramref name="chip"/>'s filter program sounding on <paramref name="call"/>.</summary>
    public int FilterStepAt(int call, int chip = 0) => _filterSteps[chip][Math.Clamp(call, 0, _cycles.Length - 1)];

    /// <summary>The row of <paramref name="voice"/>'s that is playing on <paramref name="call"/>,
    /// as an index into <see cref="Rows"/>; -1 before its first.</summary>
    public int RowAt(int voice, int call)
    {
        var rows = Rows[voice];
        int low = 0, high = rows.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (rows[mid].Call <= call)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return found;
    }

    public static ScoreTimeline Of(Score score, int calls)
    {
        calls = Math.Max(1, calls);
        var player = new ScorePlayer(score);
        var cycles = new long[calls];
        int count = score.Voices.Length;
        var voices = new VoicePosition[count][];
        var rows = new List<PlayedRow>[count];
        for (int v = 0; v < count; v++)
        {
            voices[v] = new VoicePosition[calls];
            rows[v] = [];
        }
        var filterSteps = new int[score.Chips][];
        for (int chip = 0; chip < score.Chips; chip++)
            filterSteps[chip] = new int[calls];

        for (int call = 0; call < calls; call++)
        {
            cycles[call] = player.Cycle;
            player.Call();
            for (int chip = 0; chip < score.Chips; chip++)
                filterSteps[chip][call] = player.FilterStepOf(chip);
            for (int v = 0; v < count; v++)
            {
                var position = player.PositionOf(v);
                voices[v][call] = position;

                // A row's first call leaves it one call in.
                if (!position.Ended && position.RowCall == 1 && position.Pattern >= 0)
                    rows[v].Add(new PlayedRow(call, position.Entry, position.Transpose, position.Pattern, position.Row));
            }
        }

        return new ScoreTimeline(cycles, voices, filterSteps, rows);
    }
}
