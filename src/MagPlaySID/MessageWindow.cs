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
