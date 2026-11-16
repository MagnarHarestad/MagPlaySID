using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MagPlaySid;

/// <summary>One thing an HVSC update script does: its command, the line it is on, and the paths and
/// values it names.</summary>
internal sealed record HvscStep(string Command, int Line, string[] Args);

/// <summary>
/// An HVSC update applied to the user's copy, as HVSC's own Update Tool applies it: the update's script -
/// UpdateNN.hvs - read, every step of it known and its paths inside the collection before anything is
/// touched; then each step done, every file it changes kept first, so a failure, a cancel or a crash puts
/// the copy back as it was; then every tune checked against the new list's MD5s.
/// </summary>
internal static class HvscUpdate
{
    /// <summary>The commands that name paths in pairs - from, to.</summary>
    private static readonly HashSet<string> Pairs = ["MOVE", "REPLACE"];

    /// <summary>The changes to a tune's header, and how many lines of values follow its path.</summary>
    private static readonly Dictionary<string, int> Edits = new()
    {
        ["CREDITS"] = 3, ["TITLE"] = 1, ["NAME"] = 1, ["AUTHOR"] = 1, ["RELEASED"] = 1, ["COPYRIGHT"] = 1,
        ["SONGS"] = 1, ["SPEED"] = 1, ["INITPLAY"] = 1, ["CLOCK"] = 1, ["SIDMODEL"] = 1, ["FREEPAGES"] = 1,
    };

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>An update's script read into its steps. Throws <see cref="MusicSourceException"/> on a
    /// command MagPlaySID does not know that has anything to do, or a line it cannot place - nothing is
    /// done then, and the whole of HVSC is downloaded instead.</summary>
    public static List<HvscStep> Parse(string script)
    {
        var steps = new List<HvscStep>();
        var lines = script.Replace("\r\n", "\n").Split('\n');
        string? command = null;
        var pending = new List<string>();
        int pendingLine = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (pending.Count == 0 && IsCommand(line))
            {
                command = line.Trim();
                continue;
            }
            if (command is null)
                throw new MusicSourceException($"the update's script has {Quote(line)} before any command, on line {i + 1}");
            if (pending.Count == 0)
            {
                if (!line.StartsWith('/'))
                    throw new MusicSourceException($"the update's script has {Quote(line)} where a path should be, on line {i + 1}");
                pendingLine = i + 1;
            }
            pending.Add(line);
            int wanted = Pairs.Contains(command) ? 2
                : command == "DELETE" ? 1
                : Edits.TryGetValue(command, out int values) ? 1 + values
                : throw new MusicSourceException($"the update's script asks for {command}, which MagPlaySID cannot do yet (line {i + 1})");
            if (Pairs.Contains(command) && pending.Count == 2 && !line.StartsWith('/'))
                throw new MusicSourceException($"the update's script has {Quote(line)} where a path should be, on line {i + 1}");
            if (pending.Count == wanted)
            {
                steps.Add(new HvscStep(command, pendingLine, [.. pending]));
                pending.Clear();
            }
        }
        if (pending.Count > 0)
            throw new MusicSourceException($"the update's script ends in the middle of {command} on line {pendingLine}");
        foreach (var step in steps)
            foreach (var path in step.Command == "DELETE" || Pairs.Contains(step.Command) ? step.Args : step.Args[..1])
                if (path.Split('/').Any(part => part is "." or "..") || path.Contains('\\') || path.Contains(':'))
                    throw new MusicSourceException($"the update's script names {Quote(path)}, outside HVSC (line {step.Line})");
        return steps;
    }

    /// <summary>A line that is a command: a word of capitals alone.</summary>
    private static bool IsCommand(string line)
    {
        string word = line.Trim();
        return word.Length > 1 && word.All(c => c is >= 'A' and <= 'Z' or '_' or >= '0' and <= '9') && char.IsLetter(word[0]);
    }

    private static string Quote(string text) => $"“{(text.Length > 60 ? text[..60] + "..." : text)}”";

    /// <summary>
    /// The update unpacked in <paramref name="unpacked"/> - its update folder in it - applied to the copy
    /// in <paramref name="c64Music"/>. Every tune is checked against the new list after, and the copy
    /// put back as it was on any failure or a cancel. Throws <see cref="MusicSourceException"/> saying why.
    /// </summary>
    public static void Apply(string c64Music, string unpacked, int version, IProgress<(string Step, double Part)> progress, CancellationToken cancel)
    {
        string update = Path.Combine(unpacked, "update");
        string scriptFile = Path.Combine(update, $"Update{version}.hvs");
        if (!File.Exists(scriptFile))
            scriptFile = Directory.Exists(update) ? Directory.EnumerateFiles(update, "Update*.hvs").FirstOrDefault() ?? scriptFile : scriptFile;
        if (!File.Exists(scriptFile))
            throw new MusicSourceException($"the update holds no script for HVSC #{version}");
        var steps = Parse(File.ReadAllText(scriptFile, Latin1));

        var journal = new Journal(c64Music);
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                Do(steps[i], c64Music, unpacked, journal);
                if (i % 20 == 0)
                    progress.Report(($"Updating HVSC to #{version}", (double)i / steps.Count));
            }
            // The script kept with the documents, as the Update Tool keeps it: it says which HVSC this is.
            journal.Write(Path.Combine(c64Music, "DOCUMENTS", Path.GetFileName(scriptFile)), File.ReadAllBytes(scriptFile));

            progress.Report(($"Checking HVSC #{version}", 0));
            if (Check(c64Music, progress, $"Checking HVSC #{version}", cancel) is { } wrong)
                throw new MusicSourceException($"after the update, {wrong}");
            if (HvscArchive.VersionOf(c64Music) != version)
                throw new MusicSourceException($"after the update, the copy does not say it is HVSC #{version}");
            journal.Done();
        }
        catch
        {
            journal.Undo();
            throw;
        }
    }

    /// <summary>One step done, each file it touches kept in the journal first.</summary>
    private static void Do(HvscStep step, string c64Music, string unpacked, Journal journal)
    {
        string Where(string path) =>
            Path.Combine([path.StartsWith("/update/", StringComparison.OrdinalIgnoreCase) || path.Equals("/update", StringComparison.OrdinalIgnoreCase) ? unpacked : c64Music,
                          .. path.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        string Fail(string why) => throw new MusicSourceException($"{step.Command} on line {step.Line} of the update's script: {why}");

        switch (step.Command)
        {
            case "MOVE" or "REPLACE":
            {
                string from = Where(step.Args[0]);
                bool toFolder = step.Args[1].EndsWith('/');
                string to = Where(step.Args[1]);
                if (step.Args[0].EndsWith('/'))
                {
                    if (!Directory.Exists(from))
                        Fail($"there is no {step.Args[0]}");
                    if (!toFolder)
                        Fail($"a folder cannot become the file {step.Args[1]}");
                    journal.MakeFolder(to);
                    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
                        journal.Move(file, Path.Combine(to, Path.GetRelativePath(from, file)));
                }
                else
                {
                    if (!File.Exists(from))
                        Fail($"there is no {step.Args[0]}");
                    journal.Move(from, toFolder ? Path.Combine(to, Path.GetFileName(from)) : to);
                }
                break;
            }
            case "DELETE":
            {
                string path = Where(step.Args[0]);
                if (File.Exists(path))
                    journal.Delete(path);
                else if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList())
                        journal.Delete(file);
                    journal.RemoveFolder(path);
                }
                break;
            }
            default:
            {
                string path = Where(step.Args[0]);
                if (!File.Exists(path))
                    Fail($"there is no {step.Args[0]}");
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 0x76 || !(bytes[1] == 'S' && bytes[2] == 'I' && bytes[3] == 'D'))
                    Fail($"{step.Args[0]} is not a .sid");
                if (!Edit(bytes, step.Command, step.Args[1..]))
                    Fail($"“{string.Join(" / ", step.Args[1..])}” cannot be set in {step.Args[0]}");
                journal.Write(path, bytes);
                break;
            }
        }
    }

    /// <summary>A tune's header changed as the script says; false when what it says cannot be done.</summary>
    private static bool Edit(byte[] bytes, string command, string[] values)
    {
        bool Text(int at, string value)
        {
            if (value == "*")
                return true;
            var text = Latin1.GetBytes(value);
            Array.Clear(bytes, at, 32);
            Array.Copy(text, 0, bytes, at, Math.Min(32, text.Length));
            return true;
        }
        bool Word(int at, int value)
        {
            if (value is < 0 or > 0xFFFF)
                return false;
            bytes[at] = (byte)(value >> 8);
            bytes[at + 1] = (byte)value;
            return true;
        }
        bool Flags(int shift, string value)
        {
            if (bytes.Length < 0x7C || (bytes[4] << 8 | bytes[5]) < 2)
                return false;
            int bits = value.Trim().ToUpperInvariant() switch
            {
                "UNKNOWN" => 0, "PAL" or "6581" => 1, "NTSC" or "8580" => 2, "ANY" or "PAL/NTSC" or "BOTH" => 3, _ => -1,
            };
            if (bits < 0)
                return false;
            int flags = bytes[0x76] << 8 | bytes[0x77];
            flags = (flags & ~(3 << shift)) | (bits << shift);
            return Word(0x76, flags);
        }
        (int, int)? Two(string value)
        {
            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            return parts.Length == 2 && Number(parts[0], hex: true) is { } a && Number(parts[1], hex: true) is { } b ? (a, b) : null;
        }

        switch (command)
        {
            case "CREDITS":
                return Text(0x16, values[0]) && Text(0x36, values[1]) && Text(0x56, values[2]);
            case "TITLE" or "NAME":
                return Text(0x16, values[0]);
            case "AUTHOR":
                return Text(0x36, values[0]);
            case "RELEASED" or "COPYRIGHT":
                return Text(0x56, values[0]);
            case "SONGS":
            {
                var parts = values[0].Split(',', StringSplitOptions.TrimEntries);
                return Number(parts[0], hex: false) is { } songs && Word(0x0E, songs)
                    && (parts.Length < 2 || (Number(parts[1], hex: false) is { } first && Word(0x10, first)));
            }
            case "SPEED":
            {
                if (Number(values[0], hex: true) is not { } speed)
                    return false;
                bytes[0x12] = (byte)(speed >> 24);
                bytes[0x13] = (byte)(speed >> 16);
                bytes[0x14] = (byte)(speed >> 8);
                bytes[0x15] = (byte)speed;
                return true;
            }
            case "INITPLAY":
                return Two(values[0]) is var (init, play) && Word(0x0A, init) && Word(0x0C, play);
            case "CLOCK":
                return Flags(2, values[0]);
            case "SIDMODEL":
                return Flags(4, values[0]);
            case "FREEPAGES":
                if (bytes.Length < 0x7C || Two(values[0]) is not var (start, pages) || start > 0xFF || pages > 0xFF)
                    return false;
                (bytes[0x78], bytes[0x79]) = ((byte)start, (byte)pages);
                return true;
            default:
                return false;
        }
    }

    /// <summary>"1", "$1003", "0x1003" - in hexadecimal when <paramref name="hex"/>; null when it is no number.</summary>
    private static int? Number(string text, bool hex)
    {
        text = text.Trim();
        if (text.StartsWith('$'))
            (text, hex) = (text[1..], true);
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            (text, hex) = (text[2..], true);
        return int.TryParse(text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    /// <summary>
    /// Every tune in a copy checked against its list: each the list names there, with the MD5 the list
    /// gives, and no other .sid beside them. Null when all is as the list says; else what is not.
    /// </summary>
    public static string? Check(string c64Music, IProgress<(string Step, double Part)> progress, string step, CancellationToken cancel)
    {
        var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? path = null;
        foreach (var line in File.ReadLines(Path.Combine(c64Music, "DOCUMENTS", "Songlengths.md5"), Latin1))
        {
            if (line.StartsWith("; /", StringComparison.Ordinal))
                path = line[3..].Trim();
            else if (path is not null && line.Length > 33 && line[32] == '=')
            {
                listed[path] = line[..32];
                path = null;
            }
        }
        if (listed.Count == 0)
            return "its list of tunes is empty";

        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();
        var differ = new System.Collections.Concurrent.ConcurrentBag<string>();
        int done = 0;
        Parallel.ForEach(listed, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, tune =>
        {
            string file = Path.Combine([c64Music, .. tune.Key.Split('/')]);
            try
            {
                if (!string.Equals(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(file))), tune.Value, StringComparison.OrdinalIgnoreCase))
                    differ.Add(tune.Key);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Add(tune.Key);
            }
            if (Interlocked.Increment(ref done) % 1000 == 0)
                progress.Report((step, (double)done / listed.Count));
        });

        var extra = new List<string>();
        foreach (var top in new[] { "MUSICIANS", "GAMES", "DEMOS" })
        {
            string folder = Path.Combine(c64Music, top);
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.sid", SearchOption.AllDirectories))
            {
                string key = Path.GetRelativePath(c64Music, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!listed.ContainsKey(key))
                    extra.Add(key);
            }
        }

        if (missing.IsEmpty && differ.IsEmpty && extra.Count == 0)
            return null;
        var said = new List<string>();
        if (!missing.IsEmpty)
            said.Add($"{missing.Count:N0} of its tunes are missing - {missing.OrderBy(m => m, StringComparer.Ordinal).First()}");
        if (!differ.IsEmpty)
            said.Add($"{differ.Count:N0} differ from HVSC's - {differ.OrderBy(d => d, StringComparer.Ordinal).First()}");
        if (extra.Count > 0)
            said.Add($"{extra.Count:N0} are not in HVSC's list - {extra[0]}");
        return string.Join("; ", said);
    }

    /// <summary>
    /// What an update has done to the copy, written down as it is done - each file it changes or removes
    /// kept beside the copy first, each it makes noted - so it can be undone: when it fails, is cancelled,
    /// or MagPlaySID stopped half way, which <see cref="Recover"/> sees at the next start.
    /// </summary>
    private sealed class Journal
    {
        private readonly string _folder;
        private readonly string _root;
        private readonly string _log;
        private readonly HashSet<string> _touched = new(StringComparer.OrdinalIgnoreCase);
        private int _kept;

        public Journal(string c64Music)
        {
            _folder = UndoFolder(c64Music);
            _root = Path.GetFullPath(c64Music).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
            Directory.CreateDirectory(_folder);
            _log = Path.Combine(_folder, "journal.txt");
            File.WriteAllText(_log, "");
        }

        /// <summary>Whether a path is in the copy - not in the update's own folder, let go after.</summary>
        private bool InCopy(string path) => Path.GetFullPath(path).StartsWith(_root, StringComparison.OrdinalIgnoreCase);

        /// <summary>A file of the copy about to change: kept as it is, once; or noted as new.</summary>
        private void Touch(string path)
        {
            if (!InCopy(path) || !_touched.Add(Path.GetFullPath(path)))
                return;
            if (File.Exists(path))
            {
                string kept = Path.Combine(_folder, (++_kept).ToString(CultureInfo.InvariantCulture));
                File.Copy(path, kept);
                Log($"K\t{path}\t{kept}");
            }
            else
                Log($"N\t{path}");
        }

        public void MakeFolder(string folder)
        {
            if (Directory.Exists(folder))
                return;
            MakeFolder(Path.GetDirectoryName(folder)!);
            Directory.CreateDirectory(folder);
            if (InCopy(folder))
                Log($"F\t{folder}");
        }

        public void Move(string from, string to)
        {
            MakeFolder(Path.GetDirectoryName(to)!);
            Touch(to);
            Touch(from);
            File.Move(from, to, overwrite: true);
        }

        public void Delete(string path)
        {
            Touch(path);
            File.Delete(path);
        }

        public void RemoveFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var inner in Directory.EnumerateDirectories(folder))
                RemoveFolder(inner);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                if (InCopy(folder))
                    Log($"R\t{folder}");
            }
        }

        public void Write(string path, byte[] bytes)
        {
            MakeFolder(Path.GetDirectoryName(path)!);
            Touch(path);
            File.WriteAllBytes(path, bytes);
        }

        private void Log(string line)
        {
            using var log = new StreamWriter(_log, append: true, Encoding.UTF8);
            log.WriteLine(line);
            log.Flush();
        }

        public void Done() => Directory.Delete(_folder, true);

        public void Undo() => Rollback(_folder);
    }

    private static string UndoFolder(string c64Music) => Path.GetFullPath(c64Music).TrimEnd(Path.DirectorySeparatorChar) + ".undo";

    /// <summary>The folder an update is unpacked in, beside the copy.</summary>
    public static string UnpackFolder(string c64Music) => Path.GetFullPath(c64Music).TrimEnd(Path.DirectorySeparatorChar) + ".update";

    /// <summary>An update cut short - MagPlaySID stopped half way - undone, so the copy is as it was
    /// before it. True when there was one.</summary>
    public static bool Recover(string c64Music)
    {
        string folder = UndoFolder(c64Music);
        if (!File.Exists(Path.Combine(folder, "journal.txt")))
            return false;
        Rollback(folder);
        return true;
    }

    /// <summary>Everything in a journal undone, last first: new files taken away, kept ones put back,
    /// removed folders made again and new ones taken away if empty.</summary>
    private static void Rollback(string folder)
    {
        string log = Path.Combine(folder, "journal.txt");
        var lines = File.Exists(log) ? File.ReadAllLines(log, Encoding.UTF8) : [];
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var parts = lines[i].Split('\t');
            try
            {
                switch (parts[0])
                {
                    case "N" when File.Exists(parts[1]):
                        File.Delete(parts[1]);
                        break;
                    case "K":
                        Directory.CreateDirectory(Path.GetDirectoryName(parts[1])!);
                        File.Copy(parts[2], parts[1], overwrite: true);
                        break;
                    case "R":
                        Directory.CreateDirectory(parts[1]);
                        break;
                    case "F" when Directory.Exists(parts[1]) && !Directory.EnumerateFileSystemEntries(parts[1]).Any():
                        Directory.Delete(parts[1]);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Put back what can be: the rest is as the update left it, and the check says so next time.
            }
        }
        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
