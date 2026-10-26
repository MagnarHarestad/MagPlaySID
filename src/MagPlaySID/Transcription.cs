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

    /// <summary>The C64's clock the cycles count: PAL's or NTSC's.</summary>
    public required double ClockHz { get; init; }

    /// <param name="seconds">How much of the tune: its measured length.</param>
    public static Transcription Make(SidFile file, int song, double seconds, CancellationToken cancel)
    {
        var recording = Recording.Record(file, song, seconds);
        cancel.ThrowIfCancellationRequested();
        var (score, report) = Transcriber.Transcribe(recording, Music.Of(recording));
        cancel.ThrowIfCancellationRequested();
        return new Transcription
        {
            Score = score,
            Timeline = ScoreTimeline.Of(score, Math.Max(1, recording.Calls.Count)),
            Report = report,
            ClockHz = Machines.ClockHz(recording.Machine),
        };
    }

    /// <summary>What the view says about it.</summary>
    public string Describe()
    {
        var score = Score;
        string grid = score.FunkTempo is { } funk ? $"{score.Tempo}/{funk}" : $"{score.Tempo}";
        return $"{score.Instruments.Count} instruments, {score.Patterns.Count} patterns, rows of {grid} calls - " +
               $"the score makes {Report.Music:P1} of the tune's register changes, the rest is written raw";
    }
}
