using MagSid.Core.Serialization;

namespace MagSid.Editor.App;

/// <summary>
/// Periodic safety-net copies of the open song, entirely separate from Save/Save As - never
/// touches <see cref="EditorSession.FilePath"/> or clears <see cref="EditorSession.IsDirty"/>,
/// and never something the user explicitly asked to happen in the moment. Exists because a
/// running instance can lose whatever isn't saved (a crash, a forced close, a rebuild-and-
/// relaunch) - see the incident this followed from, where a running editor got force-closed for
/// a rebuild without asking first.
///
/// Lives at %AppData%\MagSid.Editor\Autosaves - the same "application's own folder" convention
/// EditorSettings already established for settings.json, not next to the built .exe (a bin\Debug
/// folder gets wiped on a clean rebuild, which would silently erase the very safety net this
/// exists to provide).
/// </summary>
public sealed class AutosaveManager : IDisposable
{
    /// <summary>Oldest files beyond this count are deleted after every autosave - a rolling
    /// window across every song this editor has ever autosaved, not per-song, matching "max 20
    /// files here" literally rather than 20-per-song.</summary>
    private const int MaxAutosaves = 20;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MagSid.Editor", "Autosaves");

    private readonly EditorSession _session;
    private readonly System.Windows.Forms.Timer _timer;

    public AutosaveManager(EditorSession session)
    {
        _session = session;
        _timer = new System.Windows.Forms.Timer { Interval = (int)Interval.TotalMilliseconds };
        _timer.Tick += (_, _) => TrySave();
        _timer.Start();
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>Skips entirely when nothing has changed since the last explicit Save
    /// (<see cref="EditorSession.IsDirty"/> is false) - autosaving an already-safely-saved song
    /// on a timer would just spend the 20-file budget on identical copies. IsDirty naturally
    /// stays true across ticks while the user keeps editing without saving, so this fires on
    /// every 5-minute mark for as long as there's something at risk, and goes quiet the moment
    /// they do save - exactly the coverage a safety net needs, with no separate "changed since
    /// last autosave" tracking required.</summary>
    private void TrySave()
    {
        if (!_session.IsDirty)
            return;

        try
        {
            Directory.CreateDirectory(Folder);
            string songName = _session.FilePath is { } path
                ? Path.GetFileNameWithoutExtension(path)
                : "New Song";
            string fileName = $"{songName}_{DateTime.Now:yyyyMMdd_HHmmss}.s";
            PatternsFileWriter.Write(_session.Song, Path.Combine(Folder, fileName));
            TrimOldest();
        }
        catch
        {
            // Best-effort, same spirit as EditorSettings - a failed autosave must never interrupt
            // editing or pop an error dialog for something the user never asked to happen.
        }
    }

    private static void TrimOldest()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.s");
        if (files.Length <= MaxAutosaves)
            return;

        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxAutosaves))
        {
            try { file.Delete(); }
            catch { /* best-effort - a file left over past the cap is not worth surfacing */ }
        }
    }
}
