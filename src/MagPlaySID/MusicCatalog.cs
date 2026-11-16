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
    private readonly Dictionary<string, MusicTune> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MusicFolder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _searchMade = new();
    private MusicTune[]? _all;
    private string[]? _searched;

    /// <summary>The folder the last tune added went into, by its path: a list is in order, so the next
    /// tune is most often in the same folder, found without a path made for each of its folders.</summary>
    private MusicFolder? _lastFolder;

    private MusicCatalog(Collection from, string name)
    {
        From = from;
        Root = new MusicFolder(from, name, "", null);
    }

    public Collection From { get; }

    public MusicFolder Root { get; }

    public int Count => _byPath.Count;

    public MusicTune? Tune(string path) => _byPath.GetValueOrDefault(path);

    public MusicFolder? Folder(string path) => path.Length == 0 ? Root : _folders.GetValueOrDefault(path);

    /// <summary>HVSC's Songlengths.md5 read: "; /MUSICIANS/H/Hubbard_Rob/Commando.sid" over each
    /// "md5=3:22 1:05" line.</summary>
    public static MusicCatalog ParseSonglengths(TextReader reader)
    {
        var catalog = new MusicCatalog(Collection.Hvsc, "HVSC");
        string? path = null;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (line.StartsWith("; /", StringComparison.Ordinal))
            {
                path = line[3..].Trim();
                continue;
            }
            int equals = line.IndexOf('=');
            if (path is null || equals != 32)
                continue;
            catalog.Add(new MusicTune(Collection.Hvsc, path, line[..32].ToUpperInvariant(), Lengths(line[(equals + 1)..])));
            path = null;
        }
        catalog.Arrange(catalog.Root);
        return catalog;
    }

    /// <summary>Modland's list read - "117992&lt;tab&gt;Protracker/4-Mat/1978.mod" for each file - keeping
    /// those <paramref name="wanted"/> says are tunes played here.</summary>
    public static MusicCatalog ParseModland(TextReader reader, Func<string, bool> wanted)
    {
        var catalog = new MusicCatalog(Collection.Modland, "Modland");
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1)
                continue;
            string path = line[(tab + 1)..];
            if (wanted(path))
                catalog.Add(new MusicTune(Collection.Modland, path, "", []));
        }
        catalog.Arrange(catalog.Root);
        return catalog;
    }

    /// <summary>"3:22 1:05.250(G)" - each song's length, any old mark after it let go.</summary>
    private static TimeSpan[] Lengths(string text)
    {
        var lengths = new List<TimeSpan>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string plain = word.Split('(')[0];
            var parts = plain.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int minutes)
                && double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
                lengths.Add(TimeSpan.FromSeconds((minutes * 60) + seconds));
            else
                lengths.Add(TimeSpan.Zero);
        }
        return [.. lengths];
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
            {
                _all = [.. _byPath.Values.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)];
                _searched = [.. _all.Select(t => Plain(t.Path))];
            }
        }
        var named = new List<MusicTune>();
        var inFolder = new List<MusicTune>();
        for (int i = 0; i < _searched.Length && named.Count < most; i++)
        {
            string path = _searched[i];
            if (!words.All(w => path.Contains(w, StringComparison.Ordinal)))
                continue;
            string name = path[(path.LastIndexOf('/') + 1)..];
            (words.All(w => name.Contains(w, StringComparison.Ordinal)) ? named : inFolder).Add(_all![i]);
        }
        return [.. named.Concat(inFolder).Take(most)];
    }

    /// <summary>The folders of tunes whose name holds every word typed - "hubbard" finds Hubbard_Rob -
    /// in the collection's order; at most <paramref name="most"/>. The folders that only sort others -
    /// HVSC's "H" - are not offered.</summary>
    public IReadOnlyList<MusicFolder> SearchFolders(string text, int most)
    {
        var words = Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        return [.. _folders.Values
            .Where(f => f.Tunes.Count > 0 && words.All(w => Plain(f.Name).Contains(w, StringComparison.Ordinal)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(most)];
    }

    /// <summary>The words of every path made ready, off the UI thread - after the list is shown: the
    /// first search is then as quick as the rest.</summary>
    public void Warm() => Search("warm", 1);

    /// <summary>Lower case, without accents, a word's parts apart: "Mötley_Crüe" is "motley crue".</summary>
    private static string Plain(string text)
    {
        // Most names are plain letters already: made so in one go, not taken apart for accents.
        if (System.Text.Ascii.IsValid(text))
        {
            return string.Create(text.Length, text, (to, from) =>
            {
                for (int i = 0; i < from.Length; i++)
                    to[i] = from[i] is '_' or '-' ? ' ' : char.ToLowerInvariant(from[i]);
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
