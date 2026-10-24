using MagSid.Core.SidImport;

namespace MagSid.Playback;

/// <summary>
/// Import SID's choices made by ear: where the importer has more than one good guess, each is
/// imported, played through MagSID's player and compared frame by frame with the tune
/// (SidTranscriber.Heard), and the one that sounds most of it is kept. A guess that has to cut
/// the tune short to fit loses what it leaves out.
/// </summary>
public static class SidImportChoice
{
    public sealed record Choice(SidImportOptions Options, SidImportResult Result, SidTranscriber.HeardScore Heard);

    /// <summary>The row grids worth trying for a stretch: the one the note starts suggest -
    /// two row lengths with funk speed - and the longest single row length that fits them; and
    /// both again without the gate-ons that may be an instrument striking twice, which a grid
    /// that fits only without them folds into their notes.</summary>
    public static IReadOnlyList<(int FramesPerRow, int? Funk)> Grids(SidTranscriber.Analysis analysis, int firstFrame, int frameCount)
    {
        var grids = new List<(int, int?)>();
        foreach (bool restrikes in new[] { true, false })
        {
            var suggested = SidTranscriber.SuggestGrid(analysis, firstFrame, frameCount, restrikes);
            var straight = (SidTranscriber.SuggestFramesPerRow(analysis, firstFrame, frameCount, restrikes), (int?)null);
            foreach (var grid in new[] { suggested, straight })
                if (!grids.Contains(grid))
                    grids.Add(grid);
        }
        return grids;
    }

    /// <summary>The best of <see cref="Grids"/> for the stretch, each as much of it as fits.
    /// Null when none can be imported.</summary>
    public static Choice? Best(SidTranscriber.Analysis analysis, int firstFrame, int frameCount, bool filter = true, bool vibrato = true)
    {
        var tried = new List<Choice>();
        foreach (var (framesPerRow, funk) in Grids(analysis, firstFrame, frameCount))
        {
            var options = new SidImportOptions(framesPerRow, firstFrame, frameCount, filter, vibrato, funk);
            if (SidTranscriber.LongestThatFits(analysis, options) is not { } fits)
                continue;
            options = options with { FrameCount = Math.Min(frameCount, fits) };

            SidImportResult result;
            try
            {
                result = SidTranscriber.Import(analysis, options);
            }
            catch (SidImportException)
            {
                continue;
            }

            var heard = SidTranscriber.Heard(analysis, options,
                SidTranscriber.Analyze(SidImportTrace.Replay(result.Song, options.FrameCount)));

            tried.Add(new Choice(options, result, heard));
        }

        if (tried.Count == 0)
            return null;

        // Frames left out by a cut count as not heard: the whole stretch is what was asked for.
        // Of those that sound nearly as much as the best, the smallest song: memory comes first,
        // and a grid of one frame a row can cost three times the bytes for a few frames more.
        int most = tried.Max(c => c.Heard.Matched);
        return tried.Where(c => c.Heard.Matched >= most * NearlyAsMuch)
                    .MinBy(c => c.Result.Report.TotalBytes);
    }

    /// <summary>How much of the best's heard frames a smaller song may give up.</summary>
    private const double NearlyAsMuch = 0.97;
}
