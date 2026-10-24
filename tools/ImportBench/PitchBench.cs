using System.Globalization;
using MagSid.Core.SidImport;
using MagSid.Playback;

/// <summary>
/// The share of frames where a voice sounds a tone in the original that the imported song sounds
/// at the same pitch - within half a semitone, each in its own tuning, an octave folded, at the
/// best time shift. Frames an import leaves out, cut to fit, count as not heard: the whole tune
/// is what was asked for, and a shorter import is no better for being shorter.
/// </summary>
internal static class PitchBench
{
    public static void Run(List<string> lines, string outPath, bool suggest)
    {
        using var output = new StreamWriter(outPath);
        foreach (string path in lines)
        {
            string name = Path.GetFileName(path);
            try
            {
                if (Tune.Load(path, out _) is not { } tune)
                    continue;
                var (options, result) = tune.Import(suggest);
                var want = Pitches(tune.Trace);
                var got = Pitches(SidImportTrace.Replay(result.Song, options.FrameCount + 64));
                double tw = Tuning(want), tg = Tuning(got);
                int total = 0;
                for (int v = 0; v < 3; v++)
                    for (int f = 0; f < want[v].Length; f++)
                        if (want[v][f].Tonal)
                            total++;

                int best = 0, bestShift = 0;
                for (int shift = -8; shift <= 300; shift++)
                {
                    int matched = 0;
                    for (int v = 0; v < 3; v++)
                        for (int f = 0; f < options.FrameCount; f++)
                        {
                            int g = f - shift;
                            if (!want[v][f].Tonal || g < 0 || g >= got[v].Length || !got[v][g].Tonal)
                                continue;
                            double d = (want[v][f].Pitch - tw) - (got[v][g].Pitch - tg);
                            d -= 12 * Math.Round(d / 12);
                            if (Math.Abs(d) < 0.5)
                                matched++;
                        }
                    if (matched > best)
                        (best, bestShift) = (matched, shift);
                }

                var song = result.Song;
                string grid = song.HasFunk ? $"{song.Speed + 1}/{song.FunkSpeed + 1}" : $"{song.Speed + 1}";
                output.WriteLine($"{name,-44} tune {tw.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} pitch {(total == 0 ? 100 : 100.0 * best / total).ToString("0.0", CultureInfo.InvariantCulture)} shift {bestShift} " +
                                 $"grid {grid} frames {options.FrameCount}/{tune.Analysis.FrameCount} bytes {result.Report.TotalBytes}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"{name,-44} FAILED {ex.GetType().Name}: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }
    }

    private static (bool Tonal, double Pitch)[][] Pitches(SidTrace trace)
    {
        var registers = new int[32];
        foreach (var (r, v) in trace.Init)
            registers[r & 31] = v;
        var pitches = new (bool, double)[3][];
        for (int v = 0; v < 3; v++)
            pitches[v] = new (bool, double)[trace.Frames.Count];
        for (int f = 0; f < trace.Frames.Count; f++)
        {
            foreach (var (r, value) in trace.Frames[f])
                registers[r & 31] = value;
            for (int v = 0; v < 3; v++)
            {
                int control = registers[v * 7 + 4], frequency = registers[v * 7] | registers[v * 7 + 1] << 8;
                bool silent = (control & 0x08) != 0 || (control & 0xF0) == 0;
                bool tonal = (control & 1) != 0 && !silent && (control & 0x80) == 0 && frequency > 0x40;
                pitches[v][f] = (tonal, frequency > 0 ? 12 * Math.Log2(frequency / 274.0) : 0);
            }
        }
        return pitches;
    }

    /// <summary>How far off concert pitch the tune sits, as a circular mean of its held tones.</summary>
    private static double Tuning((bool Tonal, double Pitch)[][] pitches)
    {
        double sin = 0, cos = 0;
        int count = 0;
        foreach (var voice in pitches)
            for (int f = 1; f < voice.Length; f++)
                if (voice[f].Tonal && voice[f].Pitch == voice[f - 1].Pitch)
                {
                    double a = 2 * Math.PI * (voice[f].Pitch - Math.Round(voice[f].Pitch));
                    sin += Math.Sin(a);
                    cos += Math.Cos(a);
                    count++;
                }
        return count < 16 || Math.Sqrt(sin * sin + cos * cos) < count * 0.5 ? 0 : Math.Atan2(sin, cos) / (2 * Math.PI);
    }
}
