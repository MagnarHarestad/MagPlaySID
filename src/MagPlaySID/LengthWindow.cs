using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MagPlaySid;

/// <summary>What the user chose for a song's length: the song, and its length - null to take the
/// user's away, so it is measured again.</summary>
internal readonly record struct ChosenLength(int Song, TimeSpan? Length);

/// <summary>A tune's songs' lengths set by hand - from a tune's right-click in a playlist: the song,
/// its length as m:ss, Save, Clear to measure it again, and Cancel.</summary>
internal static class LengthWindow
{
    /// <summary>The length chosen; null when the window is closed or cancelled. <paramref name="lengthOf"/>
    /// gives each song's length known now, with whether it is the user's own.</summary>
    public static async Task<ChosenLength?> AskAsync(Window owner, string tune, int songs, int song,
                                                     Func<int, (TimeSpan? Length, bool Own)> lengthOf)
    {
        var box = new TextBox { Watermark = "m:ss", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 };
        var problem = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, Text = "Write the length as m:ss, such as 2:35." };
        var choose = new ComboBox { IsVisible = songs > 1, MinWidth = 120 };
        for (int s = 1; s <= songs; s++)
            choose.Items.Add($"Song {s}");
        choose.SelectedIndex = Math.Clamp(song, 1, songs) - 1;

        var save = new Button { Content = "Save", MinWidth = 80, IsDefault = true };
        var clear = new Button { Content = "Clear", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        ChosenLength? answer = null;
        int Chosen() => songs > 1 ? choose.SelectedIndex + 1 : 1;

        void ShowSong()
        {
            var (length, own) = lengthOf(Chosen());
            box.Text = length is { } known ? SongLengths.Format(known) : "";
            clear.IsEnabled = own;
            note.Text = own ? "Set by you. Clear it and the length is measured again."
                : length is null ? "No length known: it plays for three minutes in a playlist."
                : "The length found for it now. A length you set here comes before it.";
            problem.IsVisible = false;
        }
        choose.SelectionChanged += (_, _) => ShowSong();
        ShowSong();

        var window = new Window
        {
            Title = "Song length",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = tune, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    choose,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Length", VerticalAlignment = VerticalAlignment.Center }, box } },
                    problem,
                    note,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 6, 0, 0), Children = { save, clear, cancel },
                    },
                },
            },
        };
        save.Click += (_, _) =>
        {
            if (SongLengths.Parse(box.Text) is not { } length)
            {
                problem.IsVisible = true;
                return;
            }
            answer = new ChosenLength(Chosen(), length);
            window.Close();
        };
        clear.Click += (_, _) =>
        {
            answer = new ChosenLength(Chosen(), null);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        await window.ShowDialog(owner);
        return answer;
    }
}
