using System.Security.Cryptography;
using System.Text;

namespace MagPlaySid;

/// <summary>
/// The High Voltage SID Collection: its list of tunes and each tune as it is first played - from the
/// user's own copy of HVSC when they have one (see <see cref="HvscArchive"/>), else from HVSC online -
/// kept in a folder laid out as HVSC is, so its DOCUMENTS (STIL and the bug list) are found above it
/// as in a copy on disk.
/// </summary>
internal static class Hvsc
{
    /// <summary>HVSC's own site first, then a mirror.</summary>
    private static readonly string[] Mirrors =
    [
        "https://hvsc.de/download/C64Music/",
        "https://hvsc.brona.dk/HVSC/C64Music/",
    ];

    private const string Source = "HVSC";

    /// <summary>The list and the documents fetched again once they are this old: a new HVSC comes
    /// out twice a year or so.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(7);

    /// <summary>Where HVSC's tunes are kept as they are played: C64Music, as HVSC lays it out.</summary>
    public static string Root { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagPlaySID", "HVSC", "C64Music");

    private static string ListPath => System.IO.Path.Combine(Root, "DOCUMENTS", "Songlengths.md5");

    private static Task<MusicCatalog>? _loading;
    private static readonly Dictionary<string, Task> Fetching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The list, once it has been read; null before.</summary>
    public static MusicCatalog? Catalog { get; private set; }

    /// <summary>Whether the list read is the user's own copy's, not HVSC online's.</summary>
    public static bool CatalogIsLocal { get; private set; }

    /// <summary>The list of HVSC's tunes, read off the UI thread: the user's own copy's when they have
    /// one - nothing asked of HVSC online then - else the one kept from HVSC online, fetched again behind
    /// it when it is old, or fetched first when there is none. A failure is tried again next time.</summary>
    public static Task<MusicCatalog> CatalogAsync()
    {
        if (_loading is { IsFaulted: false, IsCanceled: false } loading)
            return loading;
        return _loading = Task.Run(async () =>
        {
            if (HvscArchive.ListFile is { } own)
            {
                try
                {
                    var local = Read(own);
                    Catalog = local;
                    CatalogIsLocal = true;
                    _ = DocumentsAsync();
                    return local;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The copy has gone - a drive taken out: HVSC online, as with none.
                }
            }
            bool kept = File.Exists(ListPath);
            if (!kept)
                await MusicWeb.SaveAsync(ListPath, await MusicWeb.GetAsync(Source, Mirrors, "DOCUMENTS/Songlengths.md5", CancellationToken.None));
            var catalog = Read(ListPath);
            Catalog = catalog;
            CatalogIsLocal = false;
            if (kept && DateTime.UtcNow - File.GetLastWriteTimeUtc(ListPath) > Stale)
                _ = RefreshAsync();
            _ = DocumentsAsync();
            return catalog;
        });
    }

    private static MusicCatalog Read(string list)
    {
        MusicCatalog catalog;
        using (var reader = new StreamReader(list, Encoding.Latin1))
            catalog = MusicCatalog.ParseSonglengths(reader);
        catalog.Warm();
        return catalog;
    }

    /// <summary>The list read again next time it is asked for: the user's copy of HVSC has come, gone or
    /// been updated.</summary>
    public static void Reset()
    {
        _loading = null;
        Catalog = null;
        CatalogIsLocal = false;
    }

    /// <summary>The list fetched again, for the next time it is read: tunes come and go in each HVSC.</summary>
    private static async Task RefreshAsync()
    {
        try
        {
            await MusicWeb.SaveAsync(ListPath, await MusicWeb.GetAsync(Source, Mirrors, "DOCUMENTS/Songlengths.md5", CancellationToken.None));
        }
        catch (Exception ex) when (MusicWeb.Failed(ex))
        {
        }
    }

    /// <summary>STIL and the bug list, for the Info window to read about a tune as it does in HVSC
    /// on disk: copied from the user's own HVSC when it has them, else fetched when missing or old.</summary>
    private static async Task DocumentsAsync()
    {
        foreach (var name in new[] { "STIL.txt", "BUGlist.txt" })
        {
            string path = System.IO.Path.Combine(Root, "DOCUMENTS", name);
            try
            {
                if (HvscArchive.Document(name) is { } own)
                {
                    var from = new FileInfo(own);
                    var to = new FileInfo(path);
                    if (!to.Exists || to.Length != from.Length || to.LastWriteTimeUtc != from.LastWriteTimeUtc)
                    {
                        await MusicWeb.SaveAsync(path, await File.ReadAllBytesAsync(own));
                        File.SetLastWriteTimeUtc(path, from.LastWriteTimeUtc);
                    }
                    continue;
                }
                if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Stale)
                    continue;
                await MusicWeb.SaveAsync(path, await MusicWeb.GetAsync(Source, Mirrors, $"DOCUMENTS/{name}", CancellationToken.None));
            }
            catch (Exception ex) when (MusicWeb.Failed(ex))
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
    /// one kept is not the one the list names - a newer HVSC has mended it.</summary>
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

    /// <summary>A tune of HVSC kept at <paramref name="localPath"/>: copied from the user's own HVSC
    /// when it is there, else fetched from HVSC online; the same fetch shared by all who ask at once.
    /// Throws <see cref="MusicSourceException"/> saying why when it cannot be had.</summary>
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
                    if (HvscArchive.TunePath(path) is { } own && await File.ReadAllBytesAsync(own) is var kept && IsSid(kept))
                    {
                        await MusicWeb.SaveAsync(localPath, kept);
                        return;
                    }
                    var bytes = await MusicWeb.GetAsync(Source, Mirrors, path, CancellationToken.None);
                    if (!IsSid(bytes))
                        throw new MusicSourceException($"{path} came back as something other than a .sid");
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

    private static bool IsSid(byte[] bytes) => bytes.Length >= 4 && bytes[1] == 'S' && bytes[2] == 'I' && bytes[3] == 'D';
}
