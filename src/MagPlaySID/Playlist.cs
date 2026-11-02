using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>The order a playlist shows and plays its tunes in. Kept by number in playlists.json, so
/// new ones go last.</summary>
internal enum TuneSort
{
    Own,
    YearNewest,
    YearOldest,
    TitleAToZ,
    TitleZToA,
    ComposerAToZ,
    GroupAToZ,
    FileNewest,
    FileOldest,
    SongsMost,
    SongsFewest,
    LengthLongest,
    LengthShortest,
    RecentlyPlayed,
    MostPlayed,
    Shuffle,
}

/// <summary>What a tune's file says of it.</summary>
internal sealed record TuneHeader(string Title, string Author, int Songs, string Released, string Key, DateTime Written);

/// <summary>A tune in a playlist: its file, named by the file until its header has been read.</summary>
internal sealed class PlaylistTune : Observable
{
    /// <summary>A year in a header's released text, or a decade: "199?" is as near as some are known.</summary>
    private static readonly Regex YearIn = new(@"\b((?:19|20)\d\d)\b|\b((?:19|20)\d)\?", RegexOptions.Compiled);

    private string _title;
    private string _author = "";
    private int _songs = 1;
    private int _number;
    private bool _playing;
    private TimeSpan? _length;

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

    /// <summary>Its length, once it has been played: measuring it is playing it.</summary>
    public TimeSpan? Length { get => _length; set => Set(ref _length, value); }

    /// <summary>The year it came out, the most its header can say; null when it does not.</summary>
    public int? Year { get; private set; }

    /// <summary>Who released it, from its header: the released text without the year.</summary>
    public string Group { get; private set; } = "";

    public DateTime? Written { get; private set; }

    /// <summary>What it is known by in <see cref="TuneHistory"/>; null until its header has been read.</summary>
    public string? Key { get; private set; }

    /// <summary>The tune's name, author and the rest from its file - null when the file cannot be
    /// read. Off the UI thread: a folder can hold thousands.</summary>
    public TuneHeader? ReadHeader()
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(Path);
            var file = SidFile.Parse(bytes);
            string title = string.IsNullOrWhiteSpace(file.Name) ? System.IO.Path.GetFileNameWithoutExtension(Path) : file.Name.Trim();
            return new TuneHeader(title, file.Author?.Trim() ?? "", Math.Max(1, file.Songs), file.Released?.Trim() ?? "",
                                  TuneHistory.KeyOf(bytes), File.GetLastWriteTimeUtc(Path));
        }
        catch (Exception ex) when (ex is SidPlaybackException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The header read, on the UI thread.</summary>
    public void Take(TuneHeader header)
    {
        (Title, Author, Songs) = (header.Title, header.Author, header.Songs);
        (Key, Written) = (header.Key, header.Written);
        var year = YearIn.Match(header.Released);
        Year = !year.Success ? null : year.Groups[1].Success ? int.Parse(year.Groups[1].Value) : int.Parse(year.Groups[2].Value) * 10;
        Group = (year.Success ? header.Released.Remove(year.Index, year.Length) : header.Released).Trim(' ', '?', '-', ',', '.');
        Length = TuneHistory.Of(Key)?.Length;
    }
}

/// <summary>The tunes in the order shown, put in another order in one go: moved one by one, a folder
/// of thousands would renumber itself thousands of times.</summary>
internal sealed class TuneList : ObservableCollection<PlaylistTune>
{
    public void Arrange(IEnumerable<PlaylistTune> order)
    {
        var tunes = order.ToList();
        Items.Clear();
        foreach (var tune in tunes)
            Items.Add(tune);
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// Tunes to play one after another: a folder dropped on the window, or a favourite playlist the
/// user made from tunes they played. The folder's is for this run only; the favourites are kept.
/// Sorted, it shows and plays its tunes in that order, with the user's own kept to go back to.
/// </summary>
internal sealed class Playlist : Observable
{
    private string _name;
    private List<PlaylistTune> _own = [];

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

    /// <summary>In the order shown and played.</summary>
    public TuneList Tunes { get; } = new();

    public TuneSort Sort { get; private set; }

    /// <summary>In the order the user put them, whatever they are sorted by.</summary>
    public IReadOnlyList<PlaylistTune> OwnOrder => Sort == TuneSort.Own ? Tunes : _own;

    /// <summary>Sorted anew, even by what it is sorted by already: a sort stays as it was made while
    /// tunes are played, or the tune after this one would keep changing.</summary>
    public void SortBy(TuneSort sort)
    {
        if (Sort == TuneSort.Own)
        {
            if (sort == TuneSort.Own)
                return;
            _own = [.. Tunes];
        }
        Sort = sort;
        Tunes.Arrange(sort == TuneSort.Own ? _own : Sorted(_own));
    }

    /// <summary>Sorted again now the tunes' headers are in; a shuffle stays as it fell.</summary>
    public void Resort()
    {
        if (Sort is not (TuneSort.Own or TuneSort.Shuffle))
            Tunes.Arrange(Sorted(_own));
    }

    public void Add(PlaylistTune tune)
    {
        Tunes.Add(tune);
        if (Sort != TuneSort.Own)
        {
            _own.Add(tune);
            Resort();
        }
    }

    public void Remove(PlaylistTune tune)
    {
        Tunes.Remove(tune);
        _own.Remove(tune);
    }

    /// <summary>Tunes in the playlist's sort, ties as they came. Those the sort knows nothing of -
    /// no year, never played - last, whichever way it runs.</summary>
    private IEnumerable<PlaylistTune> Sorted(IEnumerable<PlaylistTune> tunes)
    {
        double? Plays(PlaylistTune tune) => TuneHistory.Of(tune.Key)?.Plays is > 0 and var plays ? plays : null;
        double? Played(PlaylistTune tune) => TuneHistory.Of(tune.Key)?.LastPlayed?.Ticks;
        double? Seconds(PlaylistTune tune) => tune.Length?.TotalSeconds;
        double? Written(PlaylistTune tune) => tune.Written?.Ticks;
        switch (Sort)
        {
            case TuneSort.YearNewest: return Numbers(tunes, t => t.Year, descending: true);
            case TuneSort.YearOldest: return Numbers(tunes, t => t.Year, descending: false);
            case TuneSort.TitleAToZ: return Words(tunes, t => t.Title, descending: false);
            case TuneSort.TitleZToA: return Words(tunes, t => t.Title, descending: true);
            case TuneSort.ComposerAToZ: return Words(tunes, t => t.Author, descending: false).ThenBy(t => t.Year ?? int.MaxValue).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase);
            case TuneSort.GroupAToZ: return Words(tunes, t => t.Group, descending: false);
            case TuneSort.FileNewest: return Numbers(tunes, Written, descending: true);
            case TuneSort.FileOldest: return Numbers(tunes, Written, descending: false);
            case TuneSort.SongsMost: return Numbers(tunes, t => t.Songs, descending: true);
            case TuneSort.SongsFewest: return Numbers(tunes, t => t.Songs, descending: false);
            case TuneSort.LengthLongest: return Numbers(tunes, Seconds, descending: true);
            case TuneSort.LengthShortest: return Numbers(tunes, Seconds, descending: false);
            case TuneSort.RecentlyPlayed: return Numbers(tunes, Played, descending: true);
            case TuneSort.MostPlayed: return Numbers(tunes, Plays, descending: true);
            case TuneSort.Shuffle:
                var shuffled = tunes.ToArray();
                Random.Shared.Shuffle(shuffled);
                return shuffled;
            default: return tunes;
        }
    }

    private static IOrderedEnumerable<PlaylistTune> Numbers(IEnumerable<PlaylistTune> tunes, Func<PlaylistTune, double?> key, bool descending)
    {
        var known = tunes.OrderBy(t => key(t) is null);
        return descending ? known.ThenByDescending(key) : known.ThenBy(key);
    }

    /// <summary>Text no one has filled in, or the collections' "&lt;?&gt;" for not known, counts as none.</summary>
    private static IOrderedEnumerable<PlaylistTune> Words(IEnumerable<PlaylistTune> tunes, Func<PlaylistTune, string> key, bool descending)
    {
        string Filled(PlaylistTune tune) => key(tune).Trim() is "<?>" or "" ? "" : key(tune);
        var known = tunes.OrderBy(t => Filled(t).Length == 0);
        return descending ? known.ThenByDescending(Filled, StringComparer.OrdinalIgnoreCase) : known.ThenBy(Filled, StringComparer.OrdinalIgnoreCase);
    }

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

/// <summary>The favourite playlists, in playlists.json beside the settings: each a name, its
/// tunes' paths in the user's own order, and what it is sorted by; and beside them the song the user
/// chose to play first of a tune, wherever that tune is played from. A file that cannot be read is no favourites; one that cannot be written is let go.</summary>
internal static class Favourites
{
    private static readonly string FilePath = Path.Combine(Settings.Folder, "playlists.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly StringComparer SamePaths =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static Dictionary<string, int> _firstSongs = new(SamePaths);

    private sealed record Stored(string Name, List<string> Tunes, TuneSort Sort = TuneSort.Own);

    private sealed record Kept(List<Stored>? Playlists, Dictionary<string, int>? FirstSongs);

    /// <summary>The song the user chose to play first of the tune at <paramref name="path"/>; null
    /// when they have not chosen one.</summary>
    public static int? FirstSong(string path) => _firstSongs.TryGetValue(Path.GetFullPath(path), out int song) ? song : null;

    /// <summary>Plays <paramref name="song"/> of the tune first from now on; null, the tune's own again.</summary>
    public static void SetFirstSong(string path, int? song)
    {
        if (song is { } first)
            _firstSongs[Path.GetFullPath(path)] = first;
        else
            _firstSongs.Remove(Path.GetFullPath(path));
    }

    public static List<Playlist> Load()
    {
        try
        {
            // A file from before first songs were kept is the playlists' list alone.
            string text = File.ReadAllText(FilePath);
            var kept = text.TrimStart().StartsWith('[')
                ? new Kept(JsonSerializer.Deserialize<List<Stored>>(text, Json), null)
                : JsonSerializer.Deserialize<Kept>(text, Json);
            _firstSongs = new Dictionary<string, int>(kept?.FirstSongs ?? [], SamePaths);
            var playlists = new List<Playlist>();
            foreach (var s in (kept?.Playlists ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Name)))
            {
                var playlist = new Playlist(s.Name, isFolder: false, s.Tunes ?? []);
                playlist.SortBy(s.Sort);
                playlists.Add(playlist);
            }
            return playlists;
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
            var stored = playlists.Select(p => new Stored(p.Name, p.OwnOrder.Select(t => t.Path).ToList(), p.Sort)).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Kept(stored, _firstSongs), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
