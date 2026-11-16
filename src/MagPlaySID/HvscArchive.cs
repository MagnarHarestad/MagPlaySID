using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace MagPlaySid;

/// <summary>The newest HVSC, as HVSC's version API tells it: its number, the whole of it, and the
/// update from the one before - which needs <see cref="UpdateFrom"/> in place.</summary>
internal sealed record HvscRelease(int Version, string CompleteUrl, string? UpdateUrl, int? UpdateFrom);

/// <summary>
/// The user's own copy of HVSC on this computer - one they had, or one MagPlaySID downloaded for them
/// - used before HVSC online: its list, documents and tunes read from it, so nothing is asked of HVSC's
/// site while it is there. HVSC's version API says when a newer HVSC is out, and where it is.
/// </summary>
internal static partial class HvscArchive
{
    /// <summary>HVSC's own answer to which HVSC is the newest, and where to download it - asked for by
    /// HVSC, in place of reading its site.</summary>
    public static string VersionApi { get; set; } = "https://hvsc.de/api/v1/version";

    private const string Source = "HVSC";

    /// <summary>The C64Music folder of the user's copy; null when they have none.</summary>
    public static string? Folder { get; set; }

    [GeneratedRegex(@"^Update(\d+)\.hvs$", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateScript();

    /// <summary>HVSC's C64Music in the folder chosen: the folder itself, or one in it - a copy unpacked
    /// where its archive lay; null when there is none.</summary>
    public static string? Find(string chosen)
    {
        foreach (var folder in new[] { chosen, Path.Combine(chosen, "C64Music") })
        {
            if (File.Exists(Path.Combine(folder, "DOCUMENTS", "Songlengths.md5"))
                && !string.Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(Hvsc.Root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(folder);
        }
        return null;
    }

    /// <summary>Which HVSC a copy is: the number of the newest update script in its DOCUMENTS - each
    /// HVSC adds its own; null when it has none.</summary>
    public static int? VersionOf(string c64Music)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(c64Music, "DOCUMENTS"), "Update*.hvs")
                .Select(f => UpdateScript().Match(Path.GetFileName(f)))
                .Where(m => m.Success)
                .Select(m => (int?)int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How many tunes a copy's list names.</summary>
    public static int CountTunes(string c64Music)
    {
        try
        {
            return File.ReadLines(Path.Combine(c64Music, "DOCUMENTS", "Songlengths.md5")).Count(l => l.StartsWith("; /", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The user's copy's list of tunes, when it is there to read.</summary>
    public static string? ListFile => Document("Songlengths.md5");

    /// <summary>A file of the user's copy's DOCUMENTS, when it is there.</summary>
    public static string? Document(string name) =>
        Folder is { } folder && Path.Combine(folder, "DOCUMENTS", name) is var path && File.Exists(path) ? path : null;

    /// <summary>A tune of HVSC in the user's copy - "MUSICIANS/H/Hubbard_Rob/Commando.sid" - when it is there.</summary>
    public static string? TunePath(string hvscPath) =>
        Folder is { } folder && Path.Combine([folder, .. hvscPath.Split('/')]) is var path && File.Exists(path) ? path : null;

    /// <summary>The newest HVSC, from HVSC's version API.</summary>
    public static async Task<HvscRelease> LatestAsync(CancellationToken cancel)
    {
        string text = await MusicWeb.GetTextAsync(Source, VersionApi, cancel);
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            int version = root.GetProperty("version").GetInt32();
            string complete = root.GetProperty("complete").GetProperty("url").GetString() ?? throw new MusicSourceException("HVSC named no download");
            string? update = null;
            int? from = null;
            if (root.TryGetProperty("update", out var step) && step.ValueKind == JsonValueKind.Object)
            {
                update = step.TryGetProperty("url", out var url) ? url.GetString() : null;
                from = step.TryGetProperty("requiredVersion", out var needs) && needs.TryGetInt32(out int needed) ? needed : null;
            }
            return new HvscRelease(version, complete, update, from);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MusicSourceException($"HVSC's answer could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// HVSC put in <paramref name="parent"/>, as its C64Music folder: from an archive downloaded from
    /// <paramref name="archive"/> when it is a web address, else from the .7z file it names. Unpacked
    /// beside any copy already there, which is replaced only once the new one is whole. Tells each step
    /// and how far it is, 0 to 1. Returns the C64Music folder.
    /// </summary>
    public static async Task<string> InstallAsync(string archive, string parent, IProgress<(string Step, double Part)> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(parent);
        string? downloaded = null;
        string sevenZip = archive;
        try
        {
            if (Uri.TryCreate(archive, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                downloaded = Path.Combine(parent, Path.GetFileName(uri.LocalPath) is { Length: > 0 } name ? name : "HVSC.7z");
                var told = new Progress<double>(part => progress.Report(("Downloading HVSC", part)));
                await MusicWeb.DownloadAsync(Source, archive, downloaded, told, cancel);
                sevenZip = downloaded;
            }

            string fresh = Path.Combine(parent, "C64Music.new");
            if (Directory.Exists(fresh))
                Directory.Delete(fresh, true);
            await Task.Run(() => Extract(sevenZip, fresh, progress, cancel), cancel);
            string unpacked = Find(fresh) ?? throw new MusicSourceException($"{Path.GetFileName(sevenZip)} holds no HVSC: there is no DOCUMENTS{Path.DirectorySeparatorChar}Songlengths.md5 in it.");

            progress.Report(("Putting HVSC in place", 1));
            string target = Path.Combine(parent, "C64Music");
            string old = target + ".old";
            if (Directory.Exists(old))
                Directory.Delete(old, true);
            if (Directory.Exists(target))
                Directory.Move(target, old);
            Directory.Move(unpacked, target);
            if (Directory.Exists(fresh))
                Directory.Delete(fresh, true);
            if (Directory.Exists(old))
                Directory.Delete(old, true);
            return target;
        }
        catch (OperationCanceledException)
        {
            string fresh = Path.Combine(parent, "C64Music.new");
            try
            {
                if (Directory.Exists(fresh))
                    Directory.Delete(fresh, true);
            }
            catch (IOException)
            {
            }
            throw;
        }
        finally
        {
            if (downloaded is not null)
            {
                try
                {
                    File.Delete(downloaded);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Every file of a .7z into <paramref name="into"/>, in the order it is packed - read once
    /// from start to end, as a solid archive must be. A name that would land outside is refused.</summary>
    private static void Extract(string sevenZip, string into, IProgress<(string Step, double Part)> progress, CancellationToken cancel)
    {
        string root = Path.GetFullPath(into) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        try
        {
            // Opened here and closed here: the archive opened by its name leaves the file open, and a
            // downloaded one could not be deleted.
            using var file = File.OpenRead(sevenZip);
            using var archive = ArchiveFactory.OpenArchive(file, new ReaderOptions { LeaveStreamOpen = true });
            long total = Math.Max(1, archive.TotalUncompressedSize);
            long done = 0;
            int told = -1;
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancel.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is not { Length: > 0 } key)
                    continue;
                string path = Path.GetFullPath(Path.Combine(root, key.Replace('\\', '/').TrimStart('/')));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new MusicSourceException($"{Path.GetFileName(sevenZip)} would put {key} outside its folder");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                reader.WriteEntryToFile(path, new ExtractionOptions { Overwrite = true, PreserveFileTime = true });
                done += entry.Size;
                // Told a few hundred times, not once for each of 60,000 files.
                if ((int)(done * 500 / total) is var now && now != told)
                {
                    told = now;
                    progress.Report(("Unpacking HVSC", (double)done / total));
                }
            }
        }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("SharpCompress", StringComparison.Ordinal) == true
                                   || ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            throw new MusicSourceException($"{Path.GetFileName(sevenZip)} could not be unpacked: {ex.Message}");
        }
    }
}
