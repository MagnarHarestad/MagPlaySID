using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace MagPlaySid;

/// <summary>A tune in HVSC: its path from C64Music - "MUSICIANS/H/Hubbard_Rob/Commando.sid" - the
/// MD5 HVSC lists its lengths under, and each song's length.</summary>
internal sealed record HvscTune(string Path, string Md5, TimeSpan[] Lengths)
{
    /// <summary>Its name from its file's: "Monty_on_the_Run.sid" is "Monty on the Run".</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path).Replace('_', ' ');

    /// <summary>Who wrote it, as far as its folder says: a musician's "Hubbard_Rob" is "Rob Hubbard";
    /// a tune in GAMES or DEMOS says nothing.</summary>
    public string Composer
    {
        get
        {
            var parts = Path.Split('/');
            if (parts.Length < 4 || !parts[0].Equals("MUSICIANS", StringComparison.OrdinalIgnoreCase))
                return "";
            var words = parts[2].Split('_', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 2 ? $"{words[1]} {words[0]}" : string.Join(' ', words);
        }
    }

    /// <summary>The folder it is in, as a way there: "MUSICIANS › H › Hubbard_Rob".</summary>
    public string Folder => string.Join(" › ", Path.Split('/')[..^1]);
}

/// <summary>A folder of HVSC: the folders and tunes in it, and how many tunes in all.</summary>
internal sealed class HvscFolder(string name, string path, HvscFolder? parent)
{
    public string Name { get; } = name;

    /// <summary>From C64Music: "MUSICIANS/H"; "" for C64Music itself.</summary>
    public string Path { get; } = path;

    public HvscFolder? Parent { get; } = parent;

    public List<HvscFolder> Folders { get; } = [];

    public List<HvscTune> Tunes { get; } = [];

    /// <summary>Its tunes and all its folders' tunes.</summary>
    public int Count { get; set; }

    /// <summary>What it is called: a musician's folder by the musician's name - "Hubbard_Rob" is
    /// "Rob Hubbard" - any other by its own, its words apart.</summary>
    public string Title
    {
        get
        {
            var words = Name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            bool musician = Path.StartsWith("MUSICIANS/", StringComparison.OrdinalIgnoreCase) && Path.Count(c => c == '/') == 2;
            return musician && words.Length == 2 ? $"{words[1]} {words[0]}" : string.Join(' ', words);
        }
    }

    /// <summary>The way to it from C64Music: "MUSICIANS › H".</summary>
    public string Way => Path.Length == 0 ? "" : Path.Replace("/", " › ");
}

/// <summary>
/// All of HVSC's tunes, from its Songlengths.md5 - which lists every tune by its path, with each
/// song's length - as folders to browse and words to search.
/// </summary>
internal sealed class HvscCatalog
{
    private readonly Dictionary<string, HvscTune> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HvscFolder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private HvscTune[]? _all;
    private string[]? _searched;

    public HvscFolder Root { get; } = new("HVSC", "", null);

    public int Count => _byPath.Count;

    public HvscTune? Tune(string path) => _byPath.GetValueOrDefault(path);

    public HvscFolder? Folder(string path) => path.Length == 0 ? Root : _folders.GetValueOrDefault(path);

    /// <summary>Songlengths.md5's text read: "; /MUSICIANS/H/Hubbard_Rob/Commando.sid" over each
    /// "md5=3:22 1:05" line.</summary>
    public static HvscCatalog Parse(TextReader reader)
    {
        var catalog = new HvscCatalog();
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
            catalog.Add(new HvscTune(path, line[..32].ToUpperInvariant(), Lengths(line[(equals + 1)..])));
            path = null;
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

    private void Add(HvscTune tune)
    {
        if (!_byPath.TryAdd(tune.Path, tune))
            return;
        var folder = Root;
        var parts = tune.Path.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string path = string.Join('/', parts[..(i + 1)]);
            if (!_folders.TryGetValue(path, out var next))
            {
                next = new HvscFolder(parts[i], path, folder);
                _folders[path] = next;
                folder.Folders.Add(next);
            }
            folder.Count++;
            folder = next;
        }
        folder.Count++;
        folder.Tunes.Add(tune);
    }

    private void Arrange(HvscFolder folder)
    {
        folder.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        folder.Tunes.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var inner in folder.Folders)
            Arrange(inner);
    }

    /// <summary>The tunes whose path holds every word typed - "rob hubbard" finds Hubbard_Rob's,
    /// "monty run" Monty_on_the_Run - in any case, with or without accents: those whose own name
    /// holds them all first, then those whose folder does; at most <paramref name="most"/>.</summary>
    public IReadOnlyList<HvscTune> Search(string text, int most)
    {
        var words = Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        if (_searched is null)
        {
            _all = [.. _byPath.Values.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)];
            _searched = [.. _all.Select(t => Plain(t.Path))];
        }
        var named = new List<HvscTune>();
        var inFolder = new List<HvscTune>();
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
    /// in HVSC's order; at most <paramref name="most"/>. The folders that only sort others - "H" -
    /// are not offered.</summary>
    public IReadOnlyList<HvscFolder> SearchFolders(string text, int most)
    {
        var words = Plain(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        return [.. _folders.Values
            .Where(f => f.Tunes.Count > 0 && words.All(w => Plain(f.Name).Contains(w, StringComparison.Ordinal)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(most)];
    }

    /// <summary>The words of every path made ready, off the UI thread: the first search is then as
    /// quick as the rest.</summary>
    public void Warm() => Search("warm", 1);

    /// <summary>Lower case, without accents, a word's parts apart: "Mötley_Crüe" is "motley crue".</summary>
    private static string Plain(string text)
    {
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

/// <summary>
/// The High Voltage SID Collection online: its list of tunes fetched from a mirror and kept, and
/// each tune fetched when it is first played - kept in a folder laid out as HVSC is, so its
/// DOCUMENTS (STIL and the bug list, fetched too) are found above it as in a copy on disk.
/// </summary>
internal static class Hvsc
{
    /// <summary>HVSC's own site first, then a mirror.</summary>
    private static readonly string[] Mirrors =
    [
        "https://hvsc.de/download/C64Music/",
        "https://hvsc.brona.dk/HVSC/C64Music/",
    ];

    /// <summary>The list and the documents fetched again once they are this old: a new HVSC comes
    /// out twice a year or so.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(7);

    /// <summary>Where HVSC's files are kept: C64Music, as HVSC lays it out.</summary>
    public static string Root { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagPlaySID", "HVSC", "C64Music");

    private static string ListPath => System.IO.Path.Combine(Root, "DOCUMENTS", "Songlengths.md5");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MagPlaySID", UpdateCheck.Describe(UpdateCheck.Current)[1..]));
        return http;
    }

    private static Task<HvscCatalog>? _loading;
    private static readonly Dictionary<string, Task> Fetching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list, once it has been read; null before.</summary>
    public static HvscCatalog? Catalog { get; private set; }

    /// <summary>The list of HVSC's tunes: the one kept, read off the UI thread - fetched again behind
    /// it when it is old - or fetched first when there is none. A failure is tried again next time.</summary>
    public static Task<HvscCatalog> CatalogAsync()
    {
        if (_loading is { IsFaulted: false, IsCanceled: false } loading)
            return loading;
        return _loading = Task.Run(async () =>
        {
            bool kept = File.Exists(ListPath);
            if (!kept)
                await Save(ListPath, await GetAsync("DOCUMENTS/Songlengths.md5", CancellationToken.None));
            HvscCatalog catalog;
            using (var reader = new StreamReader(ListPath, Encoding.Latin1))
                catalog = HvscCatalog.Parse(reader);
            catalog.Warm();
            Catalog = catalog;
            if (kept && DateTime.UtcNow - File.GetLastWriteTimeUtc(ListPath) > Stale)
                _ = RefreshAsync();
            _ = DocumentsAsync();
            return catalog;
        });
    }

    /// <summary>The list fetched again, for the next time it is read: tunes come and go in each HVSC.</summary>
    private static async Task RefreshAsync()
    {
        try
        {
            await Save(ListPath, await GetAsync("DOCUMENTS/Songlengths.md5", CancellationToken.None));
        }
        catch (Exception ex) when (Failed(ex))
        {
        }
    }

    /// <summary>STIL and the bug list, for the Info window to read about a tune as it does in HVSC
    /// on disk; fetched when missing or old.</summary>
    private static async Task DocumentsAsync()
    {
        foreach (var name in new[] { "STIL.txt", "BUGlist.txt" })
        {
            string path = System.IO.Path.Combine(Root, "DOCUMENTS", name);
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Stale)
                continue;
            try
            {
                await Save(path, await GetAsync($"DOCUMENTS/{name}", CancellationToken.None));
            }
            catch (Exception ex) when (Failed(ex))
            {
            }
        }
    }

    /// <summary>Where a tune of HVSC is kept: its path under <see cref="Root"/>.</summary>
    public static string LocalPath(string hvscPath) =>
        System.IO.Path.Combine([Root, .. hvscPath.Split('/')]);

    /// <summary>A kept tune's path in HVSC - "MUSICIANS/H/Hubbard_Rob/Commando.sid" - null for a file
    /// that is not one.</summary>
    public static string? HvscPath(string localPath)
    {
        string full = System.IO.Path.GetFullPath(localPath);
        string root = System.IO.Path.GetFullPath(Root) + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full[root.Length..].Replace(System.IO.Path.DirectorySeparatorChar, '/') : null;
    }

    /// <summary>Whether a tune of HVSC is to be fetched before it plays: it is not kept yet, or the
    /// one kept is not the one HVSC lists now - a newer HVSC has mended it.</summary>
    public static bool NeedsFetch(string localPath)
    {
        if (HvscPath(localPath) is not { } path)
            return false;
        if (!File.Exists(localPath))
            return true;
        if (Catalog?.Tune(path) is not { } tune)
            return false;
        try
        {
            return !string.Equals(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(localPath))), tune.Md5, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>A tune of HVSC fetched and kept at <paramref name="localPath"/>; the same fetch shared
    /// by all who ask at once. Throws <see cref="HvscException"/> saying why when it cannot be had.</summary>
    public static Task FetchAsync(string localPath)
    {
        string path = HvscPath(localPath) ?? throw new ArgumentException("Not a tune of HVSC", nameof(localPath));
        lock (Fetching)
        {
            if (Fetching.TryGetValue(path, out var running))
                return running;
            var fetch = Task.Run(async () =>
            {
                try
                {
                    var bytes = await GetAsync(path, CancellationToken.None);
                    if (!(bytes.Length >= 4 && bytes[1] == 'S' && bytes[2] == 'I' && bytes[3] == 'D'))
                        throw new HvscException($"{path} came back as something other than a .sid");
                    await Save(localPath, bytes);
                }
                finally
                {
                    lock (Fetching)
                        Fetching.Remove(path);
                }
            });
            Fetching[path] = fetch;
            return fetch;
        }
    }

    /// <summary>A file of HVSC from its site, or else from the mirror.</summary>
    private static async Task<byte[]> GetAsync(string path, CancellationToken cancel)
    {
        string url = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        Exception? last = null;
        foreach (var mirror in Mirrors)
        {
            try
            {
                using var response = await Http.GetAsync(mirror + url, cancel);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsByteArrayAsync(cancel);
                last = new HvscException($"{new Uri(mirror).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
        }
        throw last is HvscException ? last : new HvscException(last is TaskCanceledException ? "HVSC did not answer in time" : $"HVSC could not be reached: {last?.Message}");
    }

    /// <summary>Written whole, or not at all: to a file beside it first.</summary>
    private static async Task Save(string path, byte[] bytes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string part = path + ".part";
        await File.WriteAllBytesAsync(part, bytes);
        File.Move(part, path, overwrite: true);
    }

    private static bool Failed(Exception ex) => ex is HvscException or IOException or UnauthorizedAccessException;
}

/// <summary>HVSC, or a tune of it, could not be had - the message says why, for the user.</summary>
internal sealed class HvscException(string message) : Exception(message);
