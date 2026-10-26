using MagSid.Core.Model;
using MagSid.Core.SidImport;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>A row of the transcription starting on one voice, and the frame of the tune it
/// starts on.</summary>
internal readonly record struct RowStart(int Channel, int Pattern, int Row, int OrderEntrySequence, int Frame);

/// <summary>
/// A tune transcribed into rows for the Retro view - by MagSID's Import SID, for now (step 0 of
/// docs/PLAN.md) - with when each row starts. Row 0 starts on <see cref="OriginFrame"/> of the
/// tune; <see cref="Rows"/> come from playing the transcription's own sequencer once, silently,
/// so the view follows the tune without rendering a second sound.
/// </summary>
internal sealed class Transcription
{
    public required Song Song { get; init; }

    /// <summary>The tune's frame row 0 starts on.</summary>
    public required int OriginFrame { get; init; }

    /// <summary>Every row start, in order, framed in the tune's own frames.</summary>
    public required IReadOnlyList<RowStart> Rows { get; init; }

    /// <summary>The tune's frames transcribed, from its start.</summary>
    public required int Frames { get; init; }

    /// <param name="frames">How much of the tune: its measured length.</param>
    public static Transcription Make(SidFile file, int song, int frames, CancellationToken cancel)
    {
        var trace = SidImportTrace.Record(file, song, frames);
        cancel.ThrowIfCancellationRequested();
        var analysis = SidTranscriber.Analyze(trace);
        var (options, result) = MostHeard(analysis, cancel);
        int origin = SidTranscriber.OriginFrame(analysis, options);

        var rows = new List<RowStart>();
        int frame = 0;
        var sequencer = new SongSequencer(result.Song, (_, _) => { });
        sequencer.RowTriggered += (channel, pattern, row, sequence, _) =>
            rows.Add(new RowStart(channel, pattern, row, sequence, origin + frame));
        for (; frame < analysis.FrameCount - origin; frame++)
            sequencer.Frame();

        return new Transcription { Song = result.Song, OriginFrame = origin, Rows = rows, Frames = analysis.FrameCount };
    }

    /// <summary>The grid the rows sound most like the tune on - the importer's candidates, as the
    /// editor's dialog weighs them, but each over the whole tune: the view has no memory budget, so
    /// nothing is cut to fit a C64, and the larger row wins a tie, being easier to read.</summary>
    private static (SidImportOptions Options, SidImportResult Result) MostHeard(SidTranscriber.Analysis analysis, CancellationToken cancel)
    {
        (SidImportOptions Options, SidImportResult Result, int Heard)? best = null;
        foreach (var (framesPerRow, funk) in SidImportChoice.Grids(analysis, 0, analysis.FrameCount))
        {
            cancel.ThrowIfCancellationRequested();
            var options = new SidImportOptions(framesPerRow, 0, analysis.FrameCount, FunkFramesPerRow: funk);
            SidImportResult result;
            try
            {
                result = SidTranscriber.Import(analysis, options);
            }
            catch (SidImportException)
            {
                continue;
            }

            var replay = SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, options.FrameCount));
            int heard = SidTranscriber.Heard(analysis, options, replay).Matched;
            if (best is not { } b || heard > b.Heard || (heard == b.Heard && framesPerRow > b.Options.FramesPerRow))
                best = (options, result, heard);
        }

        return best is { } found
            ? (found.Options, found.Result)
            : throw new SidImportException("No notes start in this tune - the gates never go on.");
    }
}
