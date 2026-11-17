using System.IO.Compression;
using System.Text;
using MagPlay.Amiga;

namespace MagPlaySid;

/// <summary>
/// Modland's modules: the list of the ones played here - Protracker, Soundtracker, Fasttracker 1 and 2,
/// Screamtracker 3, Impulse Tracker and OpenMPT's - from the list of every file Modland has, and each
/// module as it is first played, from Modland online. The modules played are kept, laid out as Modland
/// lays them out, and play again with no line; the list is kept too, and fetched again once a week.
/// </summary>
internal static class Modland
{
    /// <summary>Modland's own site, then its file server's.</summary>
    public static string[] Sites { get; set; } = ["https://modland.com/", "https://ftp.modland.com/"];

    private const string Source = "Modland";

    /// <summary>The formats - Modland's folders - whose modules MagPlaySID plays.</summary>
    public static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        "Protracker", "Soundtracker", "Fasttracker", "Fasttracker 2", "Screamtracker 3", "Impulsetracker", "OpenMPT MPTM",
    };

    /// <summary>The list fetched again once it is this old: Modland's is made anew each day, but its
    /// modules come a few a week.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(7);

    /// <summary>Where Modland's list and its modules are kept: the modules under "modules", as Modland
    /// lays them out.</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagPlaySID", "Modland");

    public static string Root => Path.Combine(Folder, "modules");

    /// <summary>The list kept: Modland's, with only the modules played here.</summary>
    public static string ListPath => Path.Combine(Folder, "allmods.txt");

    private static Task<MusicCatalog>? _loading;
    private static readonly Dictionary<string, Task> Fetching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list, once it has been read; null before.</summary>
    public static MusicCatalog? Catalog { get; private set; }

    /// <summary>When the list kept was fetched; null with none.</summary>
    public static DateTime? ListFetched => File.Exists(ListPath) ? File.GetLastWriteTime(ListPath) : null;

    /// <summary>Whether a module is one Modland lists that is played here: in one of <see cref="Formats"/>,
    /// with a module's name.</summary>
    public static bool Wanted(string path)
    {
        // Read for each of half a million lines: nothing made to tell.
        int slash = path.IndexOf('/');
        if (slash <= 0)
            return false;
        var format = path.AsSpan(0, slash);
        bool ours = false;
        foreach (var known in Formats)
            ours |= format.Equals(known, StringComparison.OrdinalIgnoreCase);
        if (!ours)
            return false;
        var name = path.AsSpan(path.LastIndexOf('/') + 1);
        return name.StartsWith("mod.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".mod", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".s3m", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".xm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".it", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".mptm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The list of Modland's modules, read off the UI thread: the one kept, fetched again
    /// behind it when it is old, or fetched first when there is none. A failure is tried again next time.</summary>
    public static Task<MusicCatalog> CatalogAsync()
    {
        if (_loading is { IsFaulted: false, IsCanceled: false } loading)
            return loading;
        return _loading = Task.Run(async () =>
        {
            bool kept = File.Exists(ListPath);
            if (!kept)
                await FetchListAsync(CancellationToken.None);
            var catalog = Read();
            Catalog = catalog;
            if (kept && DateTime.Now - File.GetLastWriteTime(ListPath) > Stale)
                _ = RefreshAsync();
            return catalog;
        });
    }

    private static MusicCatalog Read()
    {
        MusicCatalog catalog;
        using (var reader = new StreamReader(ListPath, Encoding.UTF8))
            catalog = MusicCatalog.ParseModland(reader, Wanted, (int)(reader.BaseStream.Length / 40));
        // Its words for searching made behind it: the list is shown without waiting for them.
        _ = Task.Run(catalog.Warm);
        return catalog;
    }

    /// <summary>The list fetched again now and read - from the Music archives window - for the strip
    /// to read the next time it is shown.</summary>
    public static async Task<MusicCatalog> UpdateAsync(CancellationToken cancel)
    {
        await FetchListAsync(cancel);
        var catalog = await Task.Run(Read, cancel);
        Catalog = catalog;
        _loading = Task.FromResult(catalog);
        return catalog;
    }

    /// <summary>The list read again next time it is asked for.</summary>
    public static void Reset()
    {
        _loading = null;
        Catalog = null;
    }

    private static async Task RefreshAsync()
    {
        try
        {
            await FetchListAsync(CancellationToken.None);
        }
        catch (Exception ex) when (MusicWeb.Failed(ex) || ex is InvalidDataException)
        {
        }
    }

    /// <summary>allmods.zip fetched - every file Modland has, 6 MB - and its list kept with only the
    /// modules played here in it.</summary>
    private static async Task FetchListAsync(CancellationToken cancel)
    {
        var zip = await MusicWeb.GetAsync(Source, Sites, "allmods.zip", cancel);
        var kept = new StringBuilder();
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("allmods.txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new MusicSourceException("Modland's list came without allmods.txt in it");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && Wanted(line[(tab + 1)..]))
                    kept.Append(line).Append('\n');
            }
        }
        catch (InvalidDataException ex)
        {
            throw new MusicSourceException($"Modland's list could not be unpacked: {ex.Message}");
        }
        if (kept.Length == 0)
            throw new MusicSourceException("Modland's list came with no modules in it");
        await MusicWeb.SaveAsync(ListPath, Encoding.UTF8.GetBytes(kept.ToString()));
    }

    /// <summary>Where a module of Modland is kept: its path under <see cref="Root"/>, each name made one
    /// every system can have - "A.N.D." keeps its last dot as "%2E", "?" is "%3F" - and read back so.</summary>
    public static string LocalPath(string modlandPath) =>
        Path.Combine([Root, .. modlandPath.Split('/').Select(Safe)]);

    /// <summary>A kept module's path in Modland - "Protracker/4-Mat/1978.mod" - null for a file that is
    /// not one.</summary>
    public static string? ModlandPath(string localPath)
    {
        string full = Path.GetFullPath(localPath);
        string root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        return string.Join('/', full[root.Length..].Split(Path.DirectorySeparatorChar).Select(Decode));
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A name of Modland's as a file's or folder's: what Windows cannot have in one - and "%"
    /// itself - written "%" and its number.</summary>
    private static string Safe(string name)
    {
        var safe = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool end = i == name.Length - 1;
            bool bad = c < ' ' || c is '<' or '>' or ':' or '"' or '\\' or '/' or '|' or '?' or '*' or '%'
                || (end && c is '.' or ' ') || (i == 0 && c == ' ')
                || (i == 0 && Reserved.Contains(name.Split('.')[0]));
            if (bad)
                safe.Append('%').Append(((int)c).ToString("X2"));
            else
                safe.Append(c);
        }
        return safe.ToString();
    }

    /// <summary>A name as it is kept on this computer read back as Modland's: "A.N.D%2E" is "A.N.D.".</summary>
    public static string Decode(string name)
    {
        if (!name.Contains('%'))
            return name;
        var plain = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '%' && i + 2 < name.Length && Uri.IsHexDigit(name[i + 1]) && Uri.IsHexDigit(name[i + 2]))
            {
                plain.Append((char)Convert.ToInt32(name.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
                plain.Append(name[i]);
        }
        return plain.ToString();
    }

    /// <summary>Whether a module of Modland is to be fetched before it plays: it is not kept yet.</summary>
    public static bool NeedsFetch(string localPath) => ModlandPath(localPath) is not null && !File.Exists(localPath);

    /// <summary>A module of Modland fetched and kept at <paramref name="localPath"/>; the same fetch shared
    /// by all who ask at once. Throws <see cref="MusicSourceException"/> saying why when it cannot be had.</summary>
    public static Task FetchAsync(string localPath)
    {
        string path = ModlandPath(localPath) ?? throw new ArgumentException("Not a module of Modland", nameof(localPath));
        lock (Fetching)
        {
            if (Fetching.TryGetValue(path, out var running))
                return running;
            var fetch = Task.Run(async () =>
            {
                try
                {
                    var bytes = await MusicWeb.GetAsync(Source, [.. Sites.Select(s => s + "pub/modules/")], path, CancellationToken.None);
                    if (!TrackerModules.Looks(bytes))
                        throw new MusicSourceException($"{path} came back as something other than a module played here");
                    await MusicWeb.SaveAsync(localPath, bytes);
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

    /// <summary>How many modules are kept, and how much room they take.</summary>
    public static (int Count, long Bytes) Kept()
    {
        if (!Directory.Exists(Root))
            return (0, 0);
        int count = 0;
        long bytes = 0;
        try
        {
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                bytes += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (count, bytes);
    }
}
