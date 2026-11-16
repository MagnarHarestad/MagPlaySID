using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MagPlaySid;

/// <summary>A line of an M3U playlist, read: a tune on this computer, a tune of HVSC or a module of
/// Modland - named by its path there, wherever the list's maker kept it - or one that cannot be found.</summary>
internal sealed record M3uEntry(string Line, string? Local, string? InHvsc, string? InModland = null);

/// <summary>An M3U playlist read: its name - its own #PLAYLIST line, else its file's - and its
/// lines in order.</summary>
internal sealed record M3uList(string Name, List<M3uEntry> Entries);

/// <summary>
/// M3U and M3U8 playlists, the lists other players open and save: a tune's path on each line -
/// absolute, or from the list's own folder - with #EXTINF lines saying its length and name.
/// MagPlaySID reads them as playlists of its own and saves its playlists as them.
/// </summary>
internal static partial class M3u
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]+://")]
    private static partial Regex WebAddress();

    /// <summary>Not a playlist, by its size: an M3U of 60,000 tunes is a few megabytes.</summary>
    private const long Largest = 32 << 20;

    private static readonly string[] HvscTops = ["DEMOS", "GAMES", "MUSICIANS"];

    public static bool IsPlaylistName(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase));

    /// <summary>The playlist in the file at <paramref name="path"/>, each line looked for on this
    /// computer.</summary>
    public static M3uList Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > Largest)
            throw new InvalidDataException($"{info.Name} is too large to be a playlist.");
        string text = Decode(File.ReadAllBytes(path));
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        return Parse(text, folder, Path.GetFileNameWithoutExtension(path), File.Exists, Directory.Exists);
    }

    /// <summary>The text of a list: by its byte order mark when it has one, else as UTF-8 - which
    /// every .m3u8 is - and when it is not UTF-8, as Windows wrote an .m3u, in its Western code page.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..] or [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return (CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1).GetString(bytes);
        }
    }

    /// <summary>A list's lines, each a tune found - a folder named is all the tunes in it - or one
    /// of HVSC, or one not found. Comments are passed over; a web address is not found.</summary>
    public static M3uList Parse(string text, string folder, string name, Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        var entries = new List<M3uEntry>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase) && line[10..].Trim() is { Length: > 0 } own)
                    name = own;
                continue;
            }
            entries.AddRange(Resolve(line, folder, fileExists, folderExists));
        }
        return new M3uList(name, entries);
    }

    private static IEnumerable<M3uEntry> Resolve(string line, string folder, Func<string, bool> fileExists, Func<string, bool> folderExists)
    {
        string path = line;
        // file:///C:/... is a path; http:// and the like a stream, no tune.
        if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile)
            path = uri.LocalPath;
        else if (WebAddress().IsMatch(line))
        {
            yield return new M3uEntry(line, null, null);
            yield break;
        }
        if (Path.DirectorySeparatorChar == '/')
            path = path.Replace('\\', '/');

        string? local = null;
        foreach (var tried in Candidates(path, folder))
        {
            if (fileExists(tried))
            {
                local = tried;
                break;
            }
            if (folderExists(tried))
            {
                foreach (var found in Playlist.FindTunes([tried]))
                    yield return new M3uEntry(line, found, null);
                yield break;
            }
        }
        yield return new M3uEntry(line, local, local is null ? HvscPathIn(path) : null, local is null ? ModlandPathIn(path) : null);
    }

    /// <summary>Where a line's tune may be: as written - from the list's folder unless it is a full
    /// path - and without a song number some players put after it, as in "Commando.sid,3".</summary>
    private static IEnumerable<string> Candidates(string path, string folder)
    {
        string Full(string p)
        {
            try
            {
                return Path.IsPathFullyQualified(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(folder, p));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "";
            }
        }
        if (Full(path) is { Length: > 0 } full)
            yield return full;
        int comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && Full(path[..comma]) is { Length: > 0 } unnumbered)
            yield return unnumbered;
    }

    /// <summary>A module's path in Modland, read from where someone kept it - "D:\Modland\pub\modules\Protracker\4-Mat\1978.mod",
    /// or MagPlaySID's own kept "...\Modland\modules\..." - from its format's folder on; null for a path
    /// with none, or that names no module.</summary>
    public static string? ModlandPathIn(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        int top = Array.FindIndex(parts, p => Modland.Formats.Contains(p));
        if (top < 0 || parts.Length - top < 3)
            return null;
        string found = string.Join('/', parts[top..].Select(Modland.Decode));
        return MagPlay.Amiga.TrackerModules.IsModuleName(found) ? found : null;
    }

    /// <summary>A tune's path in HVSC, read from where someone kept it - "C:\HVSC\C64Music\GAMES\..."
    /// or "/MUSICIANS/H/..." - from its first DEMOS, GAMES or MUSICIANS on; null for a path that
    /// has none, or names no .sid.</summary>
    public static string? HvscPathIn(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        int top = Array.FindIndex(parts, p => HvscTops.Contains(p, StringComparer.OrdinalIgnoreCase));
        if (top < 0 || parts.Length - top < 2)
            return null;
        string found = string.Join('/', parts[top..]);
        int comma = found.LastIndexOf(',');
        if (comma > 0 && int.TryParse(found.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            found = found[..comma];
        if (!found.EndsWith(".sid", StringComparison.OrdinalIgnoreCase))
            return null;
        return parts[top].ToUpperInvariant() + found[parts[top].Length..];
    }

    /// <summary>The tunes as an extended M3U, in the order given: each with its length and its
    /// composer and title, its path from <paramref name="folder"/> - the list's own - when it is in
    /// it or below it, so the list goes along with its tunes; else in full.</summary>
    public static string Write(string name, IEnumerable<PlaylistTune> tunes, string? folder)
    {
        var text = new StringBuilder();
        string end = OperatingSystem.IsWindows() ? "\r\n" : "\n";
        text.Append("#EXTM3U").Append(end);
        text.Append("#PLAYLIST:").Append(OneLine(name)).Append(end);
        foreach (var tune in tunes)
        {
            int seconds = tune.Length is { } length ? (int)Math.Round(length.TotalSeconds) : -1;
            string author = tune.Author.Trim() is "<?>" ? "" : tune.Author.Trim();
            string title = author.Length > 0 ? $"{author} - {tune.Title}" : tune.Title;
            text.Append("#EXTINF:").Append(seconds.ToString(CultureInfo.InvariantCulture)).Append(',').Append(OneLine(title)).Append(end);
            text.Append(Relative(folder, tune.Path)).Append(end);
        }
        return text.ToString();
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static string Relative(string? folder, string path)
    {
        string full = Path.GetFullPath(path);
        if (folder is null)
            return full;
        string relative = Path.GetRelativePath(folder, full);
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full : relative;
    }
}
