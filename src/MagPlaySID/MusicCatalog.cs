using System.Globalization;
using System.Text;

namespace MagPlaySid;

/// <summary>The collections MagPlaySID browses: HVSC's .sid tunes, and Modland's modules.</summary>
internal enum Collection
{
    Hvsc,
    Modland,
}

/// <summary>A tune in a collection: its path from the collection's top - HVSC's
/// "MUSICIANS/H/Hubbard_Rob/Commando.sid", Modland's "Protracker/4-Mat/1978.mod" - with, for HVSC,
/// the MD5 its lengths are listed under and each song's length.</summary>
internal sealed record MusicTune(Collection From, string Path, string Md5, TimeSpan[] Lengths)
{
    /// <summary>Its name from its file's: HVSC's "Monty_on_the_Run.sid" is "Monty on the Run", Modland's
    /// "crosswind.mod" "crosswind".</summary>
    public string Name
    {
        get
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(Path);
            return From == Collection.Hvsc ? name.Replace('_', ' ') : name;
        }
    }

    /// <summary>Who wrote it, as far as its folder says: HVSC's musician "Hubbard_Rob" is "Rob Hubbard",
    /// and a tune in GAMES or DEMOS says nothing; Modland's composer is the folder under the format -
    /// "Heywood/coop-Braintumour" is "Heywood & Braintumour" - and "_unknown" says nothing.</summary>
    public string Composer
    {
        get
        {
            var parts = Path.Split('/');
            if (From == Collection.Modland)
            {
                if (parts.Length < 3 || parts[1].Equals("_unknown", StringComparison.OrdinalIgnoreCase))
                    return "";
                return parts.Length >= 4 && parts[2].StartsWith("coop-", StringComparison.OrdinalIgnoreCase)
                    ? $"{parts[1]} & {parts[2][5..]}" : parts[1];
            }
            if (parts.Length < 4 || !parts[0].Equals("MUSICIANS", StringComparison.OrdinalIgnoreCase))
                return "";
            var words = parts[2].Split('_', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 2 ? $"{words[1]} {words[0]}" : string.Join(' ', words);
        }
    }

    /// <summary>The folder it is in, as a way there: "MUSICIANS › H › Hubbard_Rob".</summary>
    public string Folder => string.Join(" › ", Path.Split('/')[..^1]);
}

/// <summary>A folder of a collection: the folders and tunes in it, and how many tunes in all.</summary>
internal sealed class MusicFolder(Collection from, string name, string path, MusicFolder? parent)
{
    public Collection From { get; } = from;

    public string Name { get; } = name;

    /// <summary>From the collection's top: "MUSICIANS/H"; "" for the top itself.</summary>
    public string Path { get; } = path;

    public MusicFolder? Parent { get; } = parent;

    public List<MusicFolder> Folders { get; } = [];

    public List<MusicTune> Tunes { get; } = [];

    /// <summary>Its tunes and all its folders' tunes.</summary>
    public int Count { get; set; }

    /// <summary>What it is called over its tunes: an HVSC musician's folder by the musician's name -
    /// "Hubbard_Rob" is "Rob Hubbard" - any other HVSC folder by its own, its words apart; a Modland
    /// folder by its own.</summary>
    public string Title
    {
        get
        {
            if (From == Collection.Modland)
                return Name;
            var words = Name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            bool musician = Path.StartsWith("MUSICIANS/", StringComparison.OrdinalIgnoreCase) && Path.Count(c => c == '/') == 2;
            return musician && words.Length == 2 ? $"{words[1]} {words[0]}" : string.Join(' ', words);
        }
    }

    /// <summary>Its name in a list of folders: as the collection has it, HVSC's words apart.</summary>
    public string Listed => From == Collection.Hvsc ? Name.Replace('_', ' ') : Name;

    /// <summary>The way to it from the collection's top: "MUSICIANS › H".</summary>
    public string Way => Path.Length == 0 ? "" : Path.Replace("/", " › ");
}

/// <summary>
/// All of a collection's tunes - HVSC's from its Songlengths.md5, which lists every tune by its path
/// with each song's length; Modland's from its allmods.txt, every file by its size and path - as
/// folders to browse and words to search.
/// </summary>
internal sealed class MusicCatalog
{
    private readonly Dictionary<string, MusicTune> _byPath;
    private readonly Dictionary<string, MusicFolder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _searchMade = new();
    private MusicTune[]? _all;

    /// <summary>Every tune's path made plain for searching (<see cref="Plain"/>), one after another
    /// in <see cref="_all"/>'s order, tune i's from _starts[i] to _starts[i + 1]: one block, not a
    /// string for each of 166,000 tunes for the collector to go through again and again.</summary>
    private char[]? _searched;
    private int[]? _starts;

    /// <summary>The folder the last tune added went into, by its path: a list is in order, so the next
    /// tune is most often in the same folder, found without a path made for each of its folders.</summary>
    private MusicFolder? _lastFolder;

    private MusicCatalog(Collection from, string name, int expected)
    {
        From = from;
        Root = new MusicFolder(from, name, "", null);
        _byPath = new(Math.Max(0, expected), StringComparer.OrdinalIgnoreCase);
    }

    public Collection From { get; }

    public MusicFolder Root { get; }

    public int Count => _byPath.Count;

    public MusicTune? Tune(string path) => _byPath.GetValueOrDefault(path);

    public MusicFolder? Folder(string path) => path.Length == 0 ? Root : _folders.GetValueOrDefault(path);

    /// <summary>HVSC's Songlengths.md5 read: "; /MUSICIANS/H/Hubbard_Rob/Commando.sid" over each
    /// "md5=3:22 1:05" line. <paramref name="expected"/>, the tunes there are about, makes room for
    /// them at once.</summary>
    public static MusicCatalog ParseSonglengths(TextReader reader, int expected = 0)
    {
        var catalog = new MusicCatalog(Collection.Hvsc, "HVSC", expected);
        string? path = null;
        ForEachLine(reader, line =>
        {
            if (line.StartsWith("; /", StringComparison.Ordinal))
            {
                path = new string(line[3..].Trim());
                return;
            }
            int equals = line.IndexOf('=');
            if (path is null || equals != 32)
                return;
            catalog.Add(new MusicTune(Collection.Hvsc, path, Upper(line[..32]), Lengths(line[(equals + 1)..])));
            path = null;
        });
        catalog.Arrange(catalog.Root);
        return catalog;
    }

    /// <summary>Modland's list read - "117992&lt;tab&gt;Protracker/4-Mat/1978.mod" for each file - keeping
    /// those <paramref name="wanted"/> says are tunes played here. <paramref name="expected"/>, the
    /// tunes there are about, makes room for them at once.</summary>
    public static MusicCatalog ParseModland(TextReader reader, Func<string, bool> wanted, int expected = 0)
    {
        var catalog = new MusicCatalog(Collection.Modland, "Modland", expected);
        ForEachLine(reader, line =>
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                return;
            string path = new(line[(tab + 1)..]);
            if (wanted(path))
                catalog.Add(new MusicTune(Collection.Modland, path, "", []));
        });
        catalog.Arrange(catalog.Root);
        return catalog;
    }

    private delegate void LineReader(ReadOnlySpan<char> line);

    /// <summary>Each line of <paramref name="reader"/>, as <see cref="TextReader.ReadLine"/> splits
    /// them - at "\r\n", "\n" or "\r", the last one with or without - but read into one buffer: a
    /// string is made only of what is kept, not of every line.</summary>
    private static void ForEachLine(TextReader reader, LineReader take)
    {
        var buffer = new char[64 * 1024];
        int start = 0, end = 0;
        bool done = false;
        while (true)
        {
            int at = buffer.AsSpan(start, end - start).IndexOfAny('\r', '\n');
            // A line ends where its "\r" is only once it is known whether a "\n" follows it.
            if (at >= 0 && (buffer[start + at] == '\n' || start + at + 1 < end || done))
            {
                take(buffer.AsSpan(start, at));
                start += at + 1;
                if (buffer[start - 1] == '\r' && start < end && buffer[start] == '\n')
                    start++;
                continue;
            }
            if (done)
            {
                if (start < end)
                    take(buffer.AsSpan(start, end - start));
                return;
            }
            // More read in behind what is left: moved to the front, the buffer made bigger for a
            // line longer than it.
            int left = end - start;
            if (left == buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
            else if (start > 0)
                Array.Copy(buffer, start, buffer, 0, left);
            (start, end) = (0, left);
            int read = reader.Read(buffer, end, buffer.Length - end);
            if (read == 0)
                done = true;
            end += read;
        }
    }

    /// <summary>An MD5 in upper case: one string made, not two.</summary>
    private static string Upper(ReadOnlySpan<char> md5)
    {
        Span<char> upper = stackalloc char[md5.Length];
        md5.ToUpperInvariant(upper);
        return new string(upper);
    }

    /// <summary>"3:22 1:05.250(G)" - each song's length, any old mark after it let go.</summary>
    private static TimeSpan[] Lengths(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (var rest = text; NextWord(ref rest, out _);)
            count++;
        if (count == 0)
            return [];
        var lengths = new TimeSpan[count];
        int i = 0;
        for (var rest = text; NextWord(ref rest, out var word);)
        {
            int mark = word.IndexOf('(');
            var plain = mark >= 0 ? word[..mark] : word;
            int colon = plain.IndexOf(':');
            if (colon >= 0 && plain[(colon + 1)..].IndexOf(':') < 0 && int.TryParse(plain[..colon], out int minutes)
                && double.TryParse(plain[(colon + 1)..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
                lengths[i++] = TimeSpan.FromSeconds((minutes * 60) + seconds);
            else
                lengths[i++] = TimeSpan.Zero;
        }
        return lengths;
    }

    /// <summary>The next of the words <paramref name="rest"/> has between its spaces - as splitting
    /// at ' ' with no empty words finds them - and the rest after it.</summary>
    private static bool NextWord(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> word)
    {
        rest = rest.TrimStart(' ');
        if (rest.IsEmpty)
        {
            word = default;
            return false;
        }
        int space = rest.IndexOf(' ');
        word = space < 0 ? rest : rest[..space];
        rest = space < 0 ? default : rest[space..];
        return true;
    }

    private void Add(MusicTune tune)
    {
        if (!_byPath.TryAdd(tune.Path, tune))
            return;
        int slash = tune.Path.LastIndexOf('/');
        if (_lastFolder is { } last && slash == last.Path.Length && tune.Path.AsSpan(0, slash).Equals(last.Path, StringComparison.OrdinalIgnoreCase))
        {
            for (var up = last; up is not null; up = up.Parent)
                up.Count++;
            last.Tunes.Add(tune);
            return;
        }
        var folder = Root;
        var parts = tune.Path.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string path = string.Join('/', parts[..(i + 1)]);
            if (!_folders.TryGetValue(path, out var next))
            {
                next = new MusicFolder(From, parts[i], path, folder);
                _folders[path] = next;
                folder.Folders.Add(next);
            }
            folder.Count++;
            folder = next;
        }
        folder.Count++;
        folder.Tunes.Add(tune);
        _lastFolder = folder;
    }

    private void Arrange(MusicFolder folder)
    {
        folder.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folder.Tunes.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var inner in folder.Folders)
            Arrange(inner);
    }

    /// <summary>The tunes whose path holds every word typed - "rob hubbard" finds Hubbard_Rob's,
    /// "monty run" Monty_on_the_Run - in any case, with or without accents: those whose own name
    /// holds them all first, then those whose folder does; at most <paramref name="most"/>.</summary>
    public IReadOnlyList<MusicTune> Search(string text, int most)
    {
        var words = Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        lock (_searchMade)
        {
            if (_searched is null)
                MakeSearchable();
        }
        var all = _all!;
        var searched = _searched!;
        var starts = _starts!;
        var named = new List<MusicTune>();
        var inFolder = new List<MusicTune>();
        for (int i = 0; i < all.Length && named.Count < most; i++)
        {
            var path = searched.AsSpan(starts[i], starts[i + 1] - starts[i]);
            if (!HoldsAll(path, words))
                continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            (HoldsAll(name, words) ? named : inFolder).Add(all[i]);
        }
        return [.. named.Concat(inFolder).Take(most)];
    }

    private static bool HoldsAll(ReadOnlySpan<char> text, string[] words)
    {
        foreach (string word in words)
            if (text.IndexOf(word, StringComparison.Ordinal) < 0)
                return false;
        return true;
    }

    /// <summary>The tunes in order of their paths, and the paths made plain into one block.</summary>
    private void MakeSearchable()
    {
        var all = _byPath.Values.ToArray();
        Array.Sort(all, (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        // Most paths are plain letters, made plain as they go into the block; the few that are not
        // made plain first, as they may come out shorter.
        var odd = new Dictionary<int, string>();
        int length = 0;
        for (int i = 0; i < all.Length; i++)
        {
            string path = all[i].Path;
            if (Ascii.IsValid(path))
                length += path.Length;
            else
                length += (odd[i] = Plain(path)).Length;
        }
        var searched = new char[length];
        var starts = new int[all.Length + 1];
        int at = 0;
        for (int i = 0; i < all.Length; i++)
        {
            starts[i] = at;
            if (odd.TryGetValue(i, out var plain))
            {
                plain.CopyTo(searched.AsSpan(at));
                at += plain.Length;
                continue;
            }
            foreach (char c in all[i].Path)
                searched[at++] = PlainAscii(c);
        }
        starts[all.Length] = at;

        var folders = _folders.Values.Where(f => f.Tunes.Count > 0).ToArray();
        Array.Sort(folders, (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        _folderNames = [.. folders.Select(f => Plain(f.Name))];
        _searchedFolders = folders;
        (_all, _starts, _searched) = (all, starts, searched);
    }

    /// <summary>The folders of tunes whose name holds every word typed - "hubbard" finds Hubbard_Rob -
    /// in the collection's order; at most <paramref name="most"/>. The folders that only sort others -
    /// HVSC's "H" - are not offered.</summary>
    public IReadOnlyList<MusicFolder> SearchFolders(string text, int most)
    {
        var words = Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        lock (_searchMade)
        {
            if (_searched is null)
                MakeSearchable();
        }
        var folders = _searchedFolders!;
        var names = _folderNames!;
        var found = new List<MusicFolder>();
        for (int i = 0; i < folders.Length && found.Count < most; i++)
            if (HoldsAll(names[i], words))
                found.Add(folders[i]);
        return found;
    }

    /// <summary>The folders with tunes of their own in order of their paths, and their names made plain.</summary>
    private MusicFolder[]? _searchedFolders;
    private string[]? _folderNames;

    /// <summary>The words of every path made ready, off the UI thread - after the list is shown: the
    /// first search is then as quick as the rest.</summary>
    public void Warm() => Search("warm", 1);

    private static char PlainAscii(char c) => c is '_' or '-' ? ' ' : char.ToLowerInvariant(c);

    /// <summary>Lower case, without accents, a word's parts apart: "Mötley_Crüe" is "motley crue".</summary>
    private static string Plain(string text)
    {
        // Most names are plain letters already: made so in one go, not taken apart for accents.
        if (Ascii.IsValid(text))
        {
            return string.Create(text.Length, text, (to, from) =>
            {
                for (int i = 0; i < from.Length; i++)
                    to[i] = PlainAscii(from[i]);
            });
        }
        var plain = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(c is '_' or '-' ? ' ' : char.ToLowerInvariant(c));
        }
        return plain.ToString();
    }
}
