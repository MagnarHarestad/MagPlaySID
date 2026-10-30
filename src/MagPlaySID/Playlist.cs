using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MagSid.Playback;

namespace MagPlaySid;

/// <summary>Raises <see cref="PropertyChanged"/> for the playlist views' bindings.</summary>
internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A tune in a playlist: its file, named by the file until its header has been read.</summary>
internal sealed class PlaylistTune : Observable
{
    private string _title;
    private string _author = "";
    private int _songs = 1;
    private int _number;
    private bool _playing;

    public PlaylistTune(string path)
    {
        Path = path;
        _title = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public string Path { get; }

    public string Title { get => _title; set => Set(ref _title, value); }

    public string Author { get => _author; set => Set(ref _author, value); }

    /// <summary>How many songs - sub-tunes - the tune holds, once its header has been read.</summary>
    public int Songs { get => _songs; set => Set(ref _songs, value); }

    /// <summary>Its place in the playlist, from 1.</summary>
    public int Number { get => _number; set => Set(ref _number, value); }

    public bool IsPlaying { get => _playing; set => Set(ref _playing, value); }

    /// <summary>The tune's name, author and number of songs from its header - null when the file
    /// cannot be read. Off the UI thread: a folder can hold thousands.</summary>
    public (string Title, string Author, int Songs)? ReadHeader()
    {
        try
        {
            var file = SidFile.Parse(File.ReadAllBytes(Path));
            string title = string.IsNullOrWhiteSpace(file.Name) ? System.IO.Path.GetFileNameWithoutExtension(Path) : file.Name.Trim();
            return (title, file.Author?.Trim() ?? "", Math.Max(1, file.Songs));
        }
        catch (Exception ex) when (ex is SidPlaybackException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Tunes to play one after another: a folder dropped on the window, or a favourite playlist the
/// user made from tunes they played. The folder's is for this run only; the favourites are kept.
/// </summary>
internal sealed class Playlist : Observable
{
    private string _name;

    public Playlist(string name, bool isFolder, IEnumerable<string> paths)
    {
        _name = name;
        IsFolder = isFolder;
        foreach (var path in paths)
            Tunes.Add(new PlaylistTune(path));
        Renumber();
        Tunes.CollectionChanged += (_, _) => Renumber();
    }

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>A dropped folder's - not one of the favourites, and not kept.</summary>
    public bool IsFolder { get; }

    public ObservableCollection<PlaylistTune> Tunes { get; } = [];

    public bool Contains(string path) => Tunes.Any(t => SamePath(t.Path, path));

    /// <summary>The tune after <paramref name="tune"/>; null after the last.</summary>
    public PlaylistTune? After(PlaylistTune tune)
    {
        int at = Tunes.IndexOf(tune);
        return at >= 0 && at + 1 < Tunes.Count ? Tunes[at + 1] : null;
    }

    /// <summary>The tune before <paramref name="tune"/>; null before the first.</summary>
    public PlaylistTune? Before(PlaylistTune tune)
    {
        int at = Tunes.IndexOf(tune);
        return at > 0 ? Tunes[at - 1] : null;
    }

    private void Renumber()
    {
        for (int i = 0; i < Tunes.Count; i++)
            Tunes[i].Number = i + 1;
    }

    /// <summary>Every .sid under the folders and the files dropped, folders searched to the bottom:
    /// a folder's own tunes by name, then its folders' the same way - an album before its extras.</summary>
    public static List<string> FindTunes(IEnumerable<string> dropped)
    {
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
        var found = new List<string>();

        void Walk(string folder)
        {
            found.AddRange(Directory.EnumerateFiles(folder, "*.sid", options).Order(StringComparer.OrdinalIgnoreCase));
            foreach (var sub in Directory.EnumerateDirectories(folder, "*", options).Order(StringComparer.OrdinalIgnoreCase))
                Walk(sub);
        }

        foreach (var path in dropped)
        {
            if (Directory.Exists(path))
                Walk(path);
            else if (File.Exists(path) && path.EndsWith(".sid", StringComparison.OrdinalIgnoreCase))
                found.Add(path);
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                      OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>The favourite playlists, in playlists.json beside the settings: each a name and its
/// tunes' paths. A file that cannot be read is no favourites; one that cannot be written is let go.</summary>
internal static class Favourites
{
    private static readonly string FilePath = Path.Combine(Settings.Folder, "playlists.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Stored(string Name, List<string> Tunes);

    public static List<Playlist> Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(FilePath), Json) ?? [];
            return stored.Where(s => !string.IsNullOrWhiteSpace(s.Name))
                         .Select(s => new Playlist(s.Name, isFolder: false, s.Tunes ?? []))
                         .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<Playlist> playlists)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            var stored = playlists.Select(p => new Stored(p.Name, p.Tunes.Select(t => t.Path).ToList())).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
