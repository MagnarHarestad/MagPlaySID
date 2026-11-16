using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;

namespace MagPlaySid;

/// <summary>
/// Typing over a list jumps in it: with the mouse over the playlists, a playlist's tunes or a
/// collection's folders and tunes, "mag" goes to the first whose name starts so, as in a file manager.
/// Away from the lists the letters are the window's - F fast-forwards. Letters typed within a second of each other are one name;
/// a single letter, typed again, goes on to the next that starts with it.
/// </summary>
internal sealed partial class PlaylistStrip
{
    /// <summary>How long after a letter the next is still part of the same name.</summary>
    private static readonly TimeSpan TypedTogether = TimeSpan.FromSeconds(1);

    private string _typed = "";
    private DateTime _typedAt;
    private ListBox? _typedIn;

    /// <summary>Whether a name is being typed into a list now: digits and spaces are then part of it,
    /// not the window's keys.</summary>
    private bool Typing => _typed.Length > 0 && DateTime.UtcNow - _typedAt < TypedTogether;

    /// <summary>The list typing goes to: the one the mouse is over; none while the strip is hidden.</summary>
    private ListBox? TypedList()
    {
        if (!IsEffectivelyVisible)
            return null;
        ListBox[] lists = [_musicBox, _tuneBox, _listBox];
        return lists.FirstOrDefault(l => l.IsEffectivelyVisible && l.IsPointerOver);
    }

    /// <summary>Whether a key pressed is to be typed into a list rather than taken as the window's:
    /// a letter, over a list; a digit, a space or a dash too once a name has been begun.</summary>
    public bool WantsKey(Key key, KeyModifiers modifiers)
    {
        if ((modifiers & ~KeyModifiers.Shift) != KeyModifiers.None || TypedList() is null)
            return false;
        bool letter = key is >= Key.A and <= Key.Z
            // Å, Ä, Ö and the like are on these keys, as are some marks.
            or Key.Oem1 or Key.Oem3 or Key.Oem4 or Key.Oem5 or Key.Oem6 or Key.Oem7 or Key.Oem8 or Key.Oem102;
        bool more = key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9
            or Key.Space or Key.OemMinus or Key.Subtract or Key.OemPeriod or Key.OemComma or Key.OemPlus or Key.Add;
        return letter || (more && Typing);
    }

    /// <summary>Text typed while the mouse is over a list: the list jumps to
    /// the first row whose name starts with what has been typed. False when no list takes it.</summary>
    public bool TypeAhead(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl) || TypedList() is not { } list)
            return false;
        // A space only goes on with a name: it does not begin one.
        if (!Typing && string.IsNullOrWhiteSpace(text))
            return false;
        if (!Typing || list != _typedIn)
            _typed = "";
        _typed += text;
        _typedAt = DateTime.UtcNow;
        _typedIn = list;

        var items = list.ItemsSource?.Cast<object>().ToList() ?? [];
        if (items.Count == 0)
            return true;
        string wanted = Plain(_typed);
        int at = list.SelectedItem is { } selected ? items.IndexOf(selected) : -1;
        // One letter - or the same letter again - goes on to the next that starts with it, round to
        // the top; more letters find the first from the top that starts with them all.
        bool again = wanted.Length > 1 && wanted.All(c => c == wanted[0]);
        if (again)
            wanted = wanted[..1];
        int from = wanted.Length == 1 ? at + 1 : 0;
        for (int n = 0; n < items.Count; n++)
        {
            var item = items[(from + n) % items.Count];
            if (!Plain(NameOf(item)).StartsWith(wanted, StringComparison.Ordinal))
                continue;
            if (item == list.SelectedItem)
                return true;
            list.SelectedItem = item;
            list.ScrollIntoView(item);
            // The keyboard in the list: Enter opens or plays what was found, the arrows go on from it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => (list.ContainerFromItem(item) as Control)?.Focus());
            return true;
        }
        return true;
    }

    /// <summary>A row's name as it is shown and sorted: a playlist's, a folder's, and a tune's title -
    /// or its composer or group, in a playlist sorted by them.</summary>
    private string NameOf(object item) => item switch
    {
        Playlist playlist => playlist.Name,
        MusicFolder folder => folder.Listed,
        PlaylistTune tune when !CollectionShown && !Searching && Shown?.Sort == TuneSort.ComposerAToZ => tune.Author,
        PlaylistTune tune when !CollectionShown && !Searching && Shown?.Sort == TuneSort.GroupAToZ => tune.Group,
        PlaylistTune tune => tune.Title,
        _ => item.ToString() ?? "",
    };

    /// <summary>Lower case, without accents: "Mötley" starts with "mot".</summary>
    private static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(char.ToLowerInvariant(c));
        return plain.ToString();
    }
}
