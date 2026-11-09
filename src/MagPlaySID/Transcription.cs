using MagPlay.Score;
using MagPlay.Transcribe;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>
/// A tune transcribed into a score for the Retro view (docs/PLAN.md, steps 3 and 4): recorded on
/// the C64, turned into notes, instruments and patterns over a raw layer that keeps the score
/// exact, and the score played once silently into a timeline of where it is on every call. The
/// view follows the tune by its cycle; nothing here is heard.
/// </summary>
internal sealed class Transcription
{
    public required MagPlay.Score.Score Score { get; init; }

    public required ScoreTimeline Timeline { get; init; }

    public required Transcriber.Report Report { get; init; }

    /// <summary>The samples of a digi the tune plays, as it plays them; empty for one with none.</summary>
    public IReadOnlyList<MagPlay.C64.DataRun> Samples { get; private init; } = [];

    /// <summary>The rows its notes fall on, for the whole tune to keep; null when it had too few
    /// notes to tell.</summary>
    public Music.Grid? Rows { get; private init; }

    /// <summary>No player found: the tune writes nothing that comes as a player's calls do - Reed's
    /// Flamethrower, for one, silences the voices and then only streams samples through the volume
    /// register, its whole song a recording. Nothing to make notes of.</summary>
    public bool NoPlayer { get; private init; }

    /// <summary>The first <paramref name="seconds"/> of the tune - quick to make, to show while the
    /// whole of it is.</summary>
    public static Transcription Opening(SidFile file, int song, double seconds, CancellationToken cancel) =>
        Of(Recording.Record(file, song, seconds), null, cancel);

    /// <summary>The whole tune, to its loop, and its length - measured and recorded in one run of
    /// the C64, keeping only what the tune's player writes. On <paramref name="rows"/> when given, so
    /// that the rows go on at the speed the opening showed them at.</summary>
    public static (Transcription Transcription, SidSongLength.Result? Length) Whole(SidFile file, int song, Music.Grid? rows, CancellationToken cancel) =>
        Whole(RecordWhole(file, song, cancel), rows, cancel);

    /// <summary>The whole tune played through to its loop on a thread of its own, while the opening
    /// is made on another: the whole needs the opening's rows only once it is played.</summary>
    public static Task<(Recording Recording, SidSongLength.Result? Length)> RecordWhole(SidFile file, int song, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<(Recording, SidSongLength.Result?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Given up as soon as the tune is left: measuring can play up to twenty minutes of it,
                // and a playlist skipped through left one running for each tune passed over.
                done.SetResult(Recording.RecordWhole(file, song, cancel: cancel));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Playing the whole tune" };
        thread.Start();
        return done.Task;
    }

    /// <summary>The whole tune made from its recording, once played, on the opening's rows.</summary>
    public static (Transcription Transcription, SidSongLength.Result? Length) Whole(Task<(Recording Recording, SidSongLength.Result? Length)> recorded,
                                                                                  Music.Grid? rows, CancellationToken cancel)
    {
        var (recording, length) = recorded.GetAwaiter().GetResult();
        cancel.ThrowIfCancellationRequested();
        return (Of(recording, rows, cancel), length);
    }

    private static Transcription Of(Recording recording, Music.Grid? rows, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var grid = rows ?? Music.RowsOf(recording);
        var (score, report) = Transcriber.Transcribe(recording, Music.Of(recording, grid));
        cancel.ThrowIfCancellationRequested();
        return new Transcription
        {
            Score = score,
            Timeline = ScoreTimeline.Of(score, Math.Max(1, recording.Calls.Count)),
            Report = report,
            Rows = grid,
            Samples = recording.Samples,
            NoPlayer = recording.Player is null,
        };
    }

    /// <summary>What the view says about it.</summary>
    public string Describe()
    {
        if (NoPlayer)
            return Samples.Count > 0
                ? "No notes to show: the whole tune is a digi - samples played through the volume register, not notes on the voices"
                : "No notes to show: nothing the tune writes comes as a player's notes do";
        var score = Score;
        string grid = score.FunkTempo is { } funk ? $"{score.Tempo}/{funk}" : $"{score.Tempo}";
        return $"{score.Instruments.Count} instruments, {score.Patterns.Count} patterns, rows of {grid} calls - " +
               $"the score makes {Report.Music:P1} of the tune's register changes, the rest is written raw";
    }
}
