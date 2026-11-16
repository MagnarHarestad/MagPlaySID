using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MagPlaySid;

/// <summary>A small modal message with an OK button, or a question with Yes and No - Avalonia has
/// no MessageBox of its own.</summary>
internal static class MessageWindow
{
    public static Task ShowAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, message, "OK", null);

    /// <summary>True for Yes; No, or the window closed, is false.</summary>
    public static Task<bool> AskAsync(Window owner, string title, string message, string yes = "Yes", string no = "No") =>
        ShowAsync(owner, title, message, yes, no);

    /// <summary>A line of text asked for - a playlist's name - or null when the window is closed or
    /// left empty.</summary>
    public static async Task<string?> PromptAsync(Window owner, string title, string message, string text = "", string ok = "OK")
    {
        var box = new TextBox { Text = text };
        var save = new Button { Content = ok, MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        string? answer = null;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 4, 0, 0), Children = { save, cancel },
                    },
                },
            },
        };
        save.Click += (_, _) =>
        {
            answer = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
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

    /// <summary>How long a song's sound is exported for and how many seconds it fades out over after
    /// that, asked with <paramref name="length"/> and <paramref name="fade"/> filled in; null when the
    /// window is closed or cancelled.</summary>
    public static async Task<(TimeSpan Length, double Fade)?> ExportLengthAsync(Window owner, string title, TimeSpan length, double fade)
    {
        var lengthBox = new TextBox { Text = SongLengths.Format(TimeSpan.FromSeconds(Math.Round(length.TotalSeconds, 3))), Width = 120 };
        var fadeBox = new TextBox { Text = fade.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), Width = 120 };
        var wrong = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var save = new Button { Content = "Export...", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        (TimeSpan, double)? answer = null;
        static Control Row(string label, Control box, string after) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, Width = 70, VerticalAlignment = VerticalAlignment.Center },
                box,
                new TextBlock { Text = after, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "How long the song plays in the file, and how long it then fades out.", TextWrapping = TextWrapping.Wrap },
                    Row("Length", lengthBox, "m:ss"),
                    Row("Fade-out", fadeBox, "seconds, 0 for none"),
                    wrong,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 4, 0, 0), Children = { save, cancel },
                    },
                },
            },
        };
        save.Click += (_, _) =>
        {
            var chosen = SongLengths.Parse(lengthBox.Text);
            bool fadeRead = double.TryParse((fadeBox.Text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint,
                                            System.Globalization.CultureInfo.InvariantCulture, out double seconds);
            if (chosen is not { } played || played > MostExported)
            {
                wrong.Text = "The length is written as m:ss - 3:20 - up to an hour.";
                wrong.IsVisible = true;
                return;
            }
            if (!fadeRead || seconds > 60)
            {
                wrong.Text = "The fade-out is a number of seconds, 0 to 60.";
                wrong.IsVisible = true;
                return;
            }
            answer = (played, seconds);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) =>
        {
            lengthBox.Focus();
            lengthBox.SelectAll();
        };

        await window.ShowDialog(owner);
        return answer;
    }

    /// <summary>The longest sound exported: an hour, so the samples fit in memory.</summary>
    public static readonly TimeSpan MostExported = TimeSpan.FromHours(1);

    private static async Task<bool> ShowAsync(Window owner, string title, string message, string yes, string? no)
    {
        bool answer = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    buttons,
                },
            },
        };

        var ok = new Button { Content = yes, MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) =>
        {
            answer = true;
            window.Close();
        };
        buttons.Children.Add(ok);
        if (no is not null)
        {
            var cancel = new Button { Content = no, MinWidth = 80, IsCancel = true };
            cancel.Click += (_, _) => window.Close();
            buttons.Children.Add(cancel);
        }

        await window.ShowDialog(owner);
        return answer;
    }
}
