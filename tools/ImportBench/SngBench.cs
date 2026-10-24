using System.Globalization;
using System.Text;
using MagSid.Core.Model;
using MagSid.Core.SidImport;
using MagSid.Playback;

/// <summary>
/// The import scored against the GoatTracker song its .sid was exported from: each of the
/// song's notes looked for on a row of the import, within half a row. One line per song in
/// <c>out</c>, one per GoatTracker instrument in <c>out.instruments</c>.
/// <list type="bullet">
/// <item><c>placed</c>: the song's notes that have a note row.</item>
/// <item><c>right</c>: placed on the .sng's own note; <c>rightT</c> allowing each instrument
/// one transpose; <c>rightA</c> "as heard" - also an instrument that sounds at one pitch
/// whatever the .sng writes, as the name of that note is never heard.</item>
/// <item><c>sound</c>: the row's note is the pitch the SID itself plays there.</item>
/// <item><c>extra</c>: note rows the .sng has no note for.</item>
/// </list>
/// </summary>
internal static class SngBench
{
    private static readonly string[] Names = ["C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-"];

    public static void Run(List<string> lines, string outPath, bool suggest)
    {
        var output = new StringBuilder();
        var detail = new StringBuilder();
        foreach (var line in lines)
        {
            var parts = line.Split('|');
            string title = Path.GetFileNameWithoutExtension(parts[0]);
            try
            {
                Score(title, GoatTracker.Read(File.ReadAllBytes(parts[0])), parts[1], suggest, output, detail);
            }
            catch (Exception ex)
            {
                output.AppendLine($"{title}\tFAILED\t{ex.GetType().Name}: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }

        File.WriteAllText(outPath, output.ToString());
        File.WriteAllText(outPath + ".instruments", detail.ToString());
    }

    private static void Score(string title, GoatTracker.Song sng, string sidPath, bool suggest, StringBuilder output, StringBuilder detail)
    {
        if (Tune.Load(sidPath, out string? why) is not { } tune)
        {
            output.AppendLine($"{title}\tSKIP\t{why![..Math.Min(40, why!.Length)]}");
            return;
        }

        var a = tune.Analysis;
        var heardPitch = SidPitches(tune.Trace);
        var (options, result) = tune.Import(suggest);
        var s = result.Song;
        int RowFrame(int r) => (int)s.FrameOfTick(r);
        int framesPerRow = Math.Min(RowFrame(1) - RowFrame(0), RowFrame(2) - RowFrame(1));

        int sub = Math.Clamp(tune.Song - 1, 0, sng.Orders.Count - 1);
        var events = sng.Events(sub, tune.Frames + 400);
        // GT timeline -> SID frames: the shift that puts most gate-ons on a gate rise.
        var rises = new HashSet<(int, int)>();
        for (int v = 0; v < 3; v++)
            foreach (var n in a.Notes[v])
                if (!n.Legato)
                    rises.Add((v, n.Start));
        var hard = events.Where(e => e.Kind == GoatTracker.EventKind.Note && !e.Legato).ToList();
        bool Rise(int v, int f) => rises.Contains((v, f)) || rises.Contains((v, f + 1)) || rises.Contains((v, f - 1));
        int Hits(int g) => hard.Count(e => e.Frame + g < tune.Frames && e.Frame + g >= 0 && Rise(e.Voice, e.Frame + g));
        int gtShift = Enumerable.Range(-2, 200).MaxBy(Hits);
        int inSid = hard.Count(e => e.Frame + gtShift >= 0 && e.Frame + gtShift < tune.Frames);
        double timeline = inSid == 0 ? 0 : Hits(gtShift) / (double)inSid;

        // The import's rows, in SID frames: row 0 is about where the first note starts.
        var lines = new List<List<PatternRow>>();
        foreach (var order in s.ChannelOrders)
        {
            var rows = new List<PatternRow>();
            foreach (var t in order.Tokens.OfType<PatternRefToken>())
            {
                var p = s.Patterns.First(x => x.Number == t.PatternNumber);
                rows.AddRange(PatternLoops.Unroll(p, 4096).Select(u => p.Rows[u.Row]));
            }
            lines.Add(rows);
        }
        int first = Enumerable.Range(0, 3).SelectMany(v => a.Notes[v]).Min(n => n.Start);
        int importOrigin = 0, bestImport = -1;
        for (int o = Math.Max(0, first - 2 * options.FramesPerRow); o <= first + options.FramesPerRow; o++)
        {
            int hit = 0;
            for (int v = 0; v < 3; v++)
                for (int r = 0; r < lines[v].Count; r++)
                    if (lines[v][r] is NoteRow && Rise(v, o + RowFrame(r)))
                        hit++;
            if (hit > bestImport)
                (bestImport, importOrigin) = (hit, o);
        }
        var imported = new Dictionary<(int Voice, int Frame), NoteRow>();
        for (int v = 0; v < 3; v++)
            for (int r = 0; r < lines[v].Count; r++)
                if (lines[v][r] is NoteRow nr)
                    imported[(v, importOrigin + RowFrame(r))] = nr;
        int stretchEnd = options.FrameCount;
        int tolerance = Math.Max(1, framesPerRow / 2);

        var fixedProgram = s.Instruments.Where(i => i.Arpeggio.Count > 0 && i.ArpeggioEnding != ArpeggioEnding.Root).Select(i => i.Number).ToHashSet();

        int want = 0, placed = 0, right = 0, octave = 0, legatoWant = 0, legatoPlaced = 0, judged = 0, soundRight = 0;
        var map = new Dictionary<int, Dictionary<int, int>>();
        var rightBy = new Dictionary<int, (int Want, int Placed, int Right)>();
        var wrongs = new Dictionary<int, List<string>>();
        var offsets = new Dictionary<int, Dictionary<int, int>>();
        var fixedHits = new Dictionary<int, int>();
        var gtNotes = new Dictionary<int, HashSet<int>>();
        var gotNotes = new Dictionary<int, Dictionary<int, int>>();
        var used = new HashSet<(int, int)>();
        foreach (var e in events.Where(e => e.Kind == GoatTracker.EventKind.Note).OrderBy(e => e.Frame))
        {
            int f = e.Frame + gtShift;
            if (f < 0 || f >= stretchEnd)
                continue;
            NoteRow? got = null;
            for (int d = 0; d <= tolerance && got is null; d++)
                foreach (int ff in new[] { f + d, f - d })
                    if (!used.Contains((e.Voice, ff)) && imported.TryGetValue((e.Voice, ff), out var nr))
                    {
                        got = nr;
                        used.Add((e.Voice, ff));
                        break;
                    }
            if (e.Legato)
            {
                legatoWant++;
                if (got is not null)
                    legatoPlaced++;
                continue;
            }

            want++;
            var rb = rightBy.GetValueOrDefault(e.Instrument);
            rb.Want++;
            if (got is not null)
            {
                placed++;
                rb.Placed++;
                if (!map.TryGetValue(e.Instrument, out var m))
                    map[e.Instrument] = m = [];
                m[got.InstrumentNumber] = m.GetValueOrDefault(got.InstrumentNumber) + 1;
                string gn = Names[((e.Note % 12) + 12) % 12] + (e.Note / 12);
                if (!gtNotes.TryGetValue(e.Instrument, out var gs))
                    gtNotes[e.Instrument] = gs = [];
                gs.Add(e.Note);
                int sidNote = heardPitch(e.Voice, f);
                if (sidNote >= 0)
                {
                    judged++;
                    if (fixedProgram.Contains(got.InstrumentNumber) || got.Note.TableIndex == sidNote)
                        soundRight++;
                }
                if (!gotNotes.TryGetValue(e.Instrument, out var gh))
                    gotNotes[e.Instrument] = gh = [];
                gh[got.Note.TableIndex] = gh.GetValueOrDefault(got.Note.TableIndex) + 1;
                if (fixedProgram.Contains(got.InstrumentNumber))
                    fixedHits[e.Instrument] = fixedHits.GetValueOrDefault(e.Instrument) + 1;
                else
                {
                    if (!offsets.TryGetValue(e.Instrument, out var os))
                        offsets[e.Instrument] = os = [];
                    int dn = got.Note.TableIndex - e.Note;
                    os[dn] = os.GetValueOrDefault(dn) + 1;
                }
                if (got.Note.ToString() == gn || fixedProgram.Contains(got.InstrumentNumber))
                {
                    right++;
                    rb.Right++;
                }
                else
                {
                    if (got.Note.ToString()[..2] == gn[..2])
                        octave++;
                    if (!wrongs.TryGetValue(e.Instrument, out var w))
                        wrongs[e.Instrument] = w = [];
                    if (w.Count < 5)
                        w.Add($"{gn}>{got.Note}@{got.InstrumentNumber}");
                }
            }
            rightBy[e.Instrument] = rb;
        }

        int Transposed(int i) => fixedHits.GetValueOrDefault(i) + (offsets.TryGetValue(i, out var o) && o.Count > 0 ? o.Values.Max() : 0);
        int rightT = map.Keys.Sum(Transposed);
        bool OnePitch(int i) => gotNotes.TryGetValue(i, out var g) && g.Values.Max() >= 0.9 * g.Values.Sum() && gtNotes[i].Count > 1;
        int rightA = map.Keys.Sum(i => OnePitch(i) ? map[i].Values.Sum() : Transposed(i));
        int extra = imported.Keys.Count(k => k.Frame < stretchEnd && !used.Contains(k));
        int pure = map.Values.Sum(m => m.Values.Max()), all = map.Values.Sum(m => m.Values.Sum());
        static string P(double x) => (x * 100).ToString("0.0", CultureInfo.InvariantCulture);
        string grid = s.HasFunk ? $"{s.Speed + 1}/{s.FunkSpeed + 1}" : $"{s.Speed + 1}";
        output.AppendLine($"{title}\ttimeline {P(timeline)}\tF{grid}\twant {want}\tplaced {P(placed / (double)Math.Max(1, want))}" +
                          $"\tright {P(right / (double)Math.Max(1, placed))}\toctave {octave}\trightT {P(rightT / (double)Math.Max(1, placed))}" +
                          $"\trightA {P(rightA / (double)Math.Max(1, placed))}\tsound {P(soundRight / (double)Math.Max(1, judged))}\tjudged {judged}" +
                          $"\tpurity {P(pure / (double)Math.Max(1, all))}\textra {extra}\tlegato {legatoPlaced}/{legatoWant}\tgtins {map.Count}" +
                          $"\tins {s.Instruments.Count}\tsounds {result.Report.SoundsFound}\tbytes {result.Report.TotalBytes}\troles {result.Report.Roles}");
        foreach (var (ins, m) in map.OrderBy(k => k.Key))
        {
            var top = m.MaxBy(k => k.Value);
            var mi = s.Instruments.FirstOrDefault(i => i.Number == top.Key);
            var rb = rightBy[ins];
            detail.AppendLine($"{title}\t{ins}\t{sng.InstrumentName(ins)}\t{mi?.Name}\twant {rb.Want}\tplaced {rb.Placed}\tright {rb.Right}" +
                              $"\tpurity {P(top.Value / (double)m.Values.Sum())}\tsplit {m.Count}\trightT {Transposed(ins)}" +
                              $"\trightA {(OnePitch(ins) ? m.Values.Sum() : Transposed(ins))}\tnotes {gtNotes[ins].Count}" +
                              $"\toffset {(offsets.TryGetValue(ins, out var oo) && oo.Count > 0 ? oo.MaxBy(k => k.Value).Key : 0)}" +
                              $"\t{(wrongs.TryGetValue(ins, out var ww) ? string.Join(" ", ww) : "")}\t{Describe(mi)}");
        }
    }

    /// <summary>The note the SID itself plays at a frame: the commonest tonal pitch with the gate on
    /// over the next twelve frames, in the tune's own tuning - read on its own, not the importer's way.</summary>
    private static Func<int, int, int> SidPitches(SidTrace trace)
    {
        int n = trace.Frames.Count;
        var pitch = new double[3][];
        var tonal = new bool[3][];
        for (int v = 0; v < 3; v++)
        {
            pitch[v] = new double[n];
            tonal[v] = new bool[n];
        }
        var registers = new int[32];
        foreach (var (r, value) in trace.Init)
            registers[r & 31] = value;
        double sin = 0, cos = 0;
        int count = 0;
        for (int f = 0; f < n; f++)
        {
            foreach (var (r, value) in trace.Frames[f])
                registers[r & 31] = value;
            for (int v = 0; v < 3; v++)
            {
                int control = registers[v * 7 + 4], frequency = registers[v * 7] | registers[v * 7 + 1] << 8;
                tonal[v][f] = (control & 1) != 0 && (control & 0x88) == 0 && (control & 0x70) != 0 && frequency > 0x40;
                pitch[v][f] = frequency > 0 ? 12 * Math.Log2(frequency / 274.0) : 0;    // MagSID's C-0 is $0112
                if (tonal[v][f] && f > 0 && pitch[v][f] == pitch[v][f - 1])
                {
                    double a = 2 * Math.PI * (pitch[v][f] - Math.Round(pitch[v][f]));
                    sin += Math.Sin(a);
                    cos += Math.Cos(a);
                    count++;
                }
            }
        }
        double tuning = count < 16 || Math.Sqrt(sin * sin + cos * cos) < count * 0.5 ? 0 : Math.Atan2(sin, cos) / (2 * Math.PI);
        return (v, f) =>
        {
            var votes = new Dictionary<int, int>();
            for (int k = f; k < Math.Min(n, f + 12); k++)
                if (tonal[v][k])
                {
                    double p = pitch[v][k] - tuning;
                    if (Math.Abs(p - Math.Round(p)) <= 0.25)
                    {
                        int note = (int)Math.Round(p);
                        votes[note] = votes.GetValueOrDefault(note) + 1;
                    }
                }
            return votes.Count == 0 ? -1 : votes.MaxBy(x => (x.Value, -x.Key)).Key;
        };
    }

    private static string Describe(Instrument? i) => i is null ? "" :
        $"W {Convert.ToHexString(i.Wavetable.ToArray())} ARP {string.Join(" ", i.Arpeggio)} {(i.HasArpeggio ? i.ArpeggioEnding.ToString() : "")} AD {i.AttackDecay:X2} SR {i.SustainRelease:X2}";
}
