using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using MagSid.Editor.App;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The About window: the Windows editor's AboutDocument - what the editor is, how a song is put
/// together, and every key binding - with this editor's own words where they differ: Cmd on a
/// Mac, the File menu, its menu bar, its bank folder and its audio and MIDI.
/// </summary>
internal sealed class AboutWindow : Window
{
    private static readonly IBrush WindowBackground = new SolidColorBrush(AvColor.FromRgb(0x10, 0x10, 0x20));
    private static readonly FontFamily Proportional = new("Segoe UI, Helvetica Neue, Inter, sans-serif");
    private static readonly FontFamily Mono = new("Consolas, Menlo, DejaVu Sans Mono, monospace");

    public static AboutDocument.Platform ThisPlatform
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string bank = EditorSettings.BankDirectory;
            if (home.Length > 0 && bank.StartsWith(home, StringComparison.Ordinal))
                bank = "~" + bank[home.Length..];

            return new AboutDocument.Platform(
                OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl",
                "File",
                "  File              New, Open, Import MIDI, Import SID, Save, Save As,\n" +
                "                    Load Instrument, Save Instrument, Export to .sid,\n" +
                "                    Load and Play .sid, Show High Hex-Values,\n" +
                "                    Swap Spacebar Behavior, Unroll Loops, Quit\n" +
                "  Edit              Undo, Redo, Optimize - shrink the song without\n" +
                "                    changing how it sounds\n" +
                "  Play              F1-F4, and Edit / Jam\n" +
                "  Tools             Signal Path: frame-by-frame trace of one instrument;\n" +
                "                    Digi to SID-osc: a .wav as ring-modulated instruments\n" +
                "  MIDI              Pick a MIDI input, forget knob bindings\n" +
                "  DAW / Retro       Switch view\n" +
                "  Help              About\n\n",
                bank,
                "Audio: PortAudio, through PortAudioSharp2. MIDI: the system's own - CoreMIDI, ALSA or " +
                "Windows MIDI. Windows and controls: Avalonia.");
        }
    }

    public AboutWindow()
    {
        Title = "About MagSID Editor";
        Width = 720;
        Height = 780;
        MinWidth = 520;
        MinHeight = 400;
        Background = WindowBackground;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 16, 12, 8) };
        foreach (var run in AboutDocument.Build(typeof(AboutWindow).Assembly.GetName().Version, ThisPlatform))
        {
            var (size, weight, colour, family) = run.Style switch
            {
                AboutDocument.Style.Title => (24.0, FontWeight.Bold, AvColor.FromRgb(0x66, 0xCC, 0xFF), Proportional),
                AboutDocument.Style.Subtitle => (12.7, FontWeight.Normal, AvColor.FromRgb(0x8A, 0x8A, 0xA8), Proportional),
                AboutDocument.Style.Heading => (16.0, FontWeight.Bold, AvColor.FromRgb(0xAA, 0xAA, 0xFF), Proportional),
                AboutDocument.Style.Mono => (13.3, FontWeight.Normal, AvColor.FromRgb(0x9E, 0xC8, 0xFF), Mono),
                _ => (13.3, FontWeight.Normal, AvColor.FromRgb(0xD2, 0xD2, 0xDE), Proportional),
            };

            // A line break as an inline of its own: a key table's alignment must not hang on
            // how a run's own newlines are laid out.
            var lines = run.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    text.Inlines!.Add(new LineBreak());
                if (lines[i].Length > 0)
                {
                    text.Inlines!.Add(new Run(lines[i])
                    {
                        FontSize = size,
                        FontWeight = weight,
                        FontFamily = family,
                        Foreground = new SolidColorBrush(colour),
                    });
                }
            }
        }

        var close = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8),
            Padding = new Thickness(14, 4),
        };
        close.Click += (_, _) => Close();

        var root = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer { Content = text });
        Content = root;

        // The button holds focus, so the text does not sit there with a caret nobody can type at.
        Opened += (_, _) => close.Focus();
    }
}
