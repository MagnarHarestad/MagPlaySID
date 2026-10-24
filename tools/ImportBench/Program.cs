// Import SID's fixed benchmark: every change to the importer runs both halves before and after.
//   sng   <list> <out>  - "song.sng|song.sid" pairs, the import scored against the GoatTracker song
//                         the .sid was exported from: its own notes against the imported rows.
//   pitch <list> <out>  - one .sid a line, the frames the imported song sounds at the original's
//                         pitch, independent of how the importer reads notes.
// Each tune is imported as the dialog does (SidImportChoice.Best); --suggest takes SuggestGrid as
// it is instead. Lines that are empty or start with # are skipped.
using MagSid.Core.SidImport;
using MagSid.Playback;

if (args.Length < 3 || args[0] is not ("sng" or "pitch"))
{
    Console.Error.WriteLine("usage: ImportBench sng|pitch <list> <out> [--suggest]");
    return 1;
}

bool suggest = args.Contains("--suggest");
var lines = File.ReadAllLines(args[1]).Where(l => l.Trim().Length > 0 && !l.StartsWith('#')).ToList();
if (args[0] == "sng")
    SngBench.Run(lines, args[2], suggest);
else
    PitchBench.Run(lines, args[2], suggest);
return 0;

/// <summary>A tune as the dialog reads it: its start song, as long as it plays, up to four minutes.</summary>
internal sealed record Tune(SidFile File, int Song, int Frames, SidTrace Trace, SidTranscriber.Analysis Analysis)
{
    public static Tune? Load(string path, out string? unsupported)
    {
        var file = SidFile.Parse(System.IO.File.ReadAllBytes(path));
        unsupported = file.UnsupportedReason();
        if (unsupported is not null)
            return null;

        int song = file.StartSong;
        var length = SidSongLength.Measure(file, 50 * 60 * 6, song);
        int frames = length is null ? 50 * 120 : (int)Math.Ceiling(length.TotalFrames * 50 / length.CallsPerSecond);
        frames = Math.Clamp(frames, 50, 50 * 240);
        var trace = SidImportTrace.Record(file, song, frames);
        return new Tune(file, song, frames, trace, SidTranscriber.Analyze(trace));
    }

    /// <summary>The import: the dialog's measured choice, or the suggested grid as much as fits.</summary>
    public (SidImportOptions Options, SidImportResult Result) Import(bool suggest)
    {
        if (!suggest && SidImportChoice.Best(Analysis, 0, Analysis.FrameCount) is { } chosen)
            return (chosen.Options, chosen.Result);

        var (framesPerRow, funk) = SidTranscriber.SuggestGrid(Analysis);
        var options = new SidImportOptions(framesPerRow, 0, Analysis.FrameCount, FunkFramesPerRow: funk);
        if (SidTranscriber.LongestThatFits(Analysis, options) is { } fits && fits < Analysis.FrameCount)
            options = options with { FrameCount = fits };
        return (options, SidTranscriber.Import(Analysis, options));
    }
}
