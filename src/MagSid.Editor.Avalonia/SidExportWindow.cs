using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Editor.App;
using MagSid.Editor.App.Export;
using MagSid.Sid;
using AvColor = Avalonia.Media.Color;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// Menu → Export to .sid: the header's name, author and release line, and the load address, with the
/// address checked as it is typed against the song's real size - the same rules as the Windows
/// editor's export dialog (SidLoadAddress). The file itself comes from SidExporter, which builds the
/// C64 image in C# - no assembler needed, on any system.
/// </summary>
internal sealed class SidExportWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(AvColor.FromRgb(0x8A, 0x8A, 0xA8));
    private static readonly IBrush Warning = new SolidColorBrush(AvColor.FromRgb(0xFF, 0xB0, 0x5C));

    private readonly Song _song;
    private readonly ChipModel _chip;
    private readonly int _bytes;
    private readonly TextBox _name = new();
    private readonly TextBox _author = new();
    private readonly TextBox _released = new();
    private readonly TextBox _address = new() { Width = 90, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
    private readonly TextBlock _range = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _export = new() { Content = "Export...", MinWidth = 90 };

    public SidExportWindow(Song song, string name, ChipModel chip)
    {
        _song = song;
        _chip = chip;
        _bytes = SongSizeCalculator.Measure(song.WithUnusedPatternsRemoved()).TotalBytes;

        Title = "Export to .sid";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = name;
        _author.Text = EditorSettings.SidAuthor;
        _released.Text = $"{DateTime.Now.Year} {EditorSettings.SidAuthor}".Trim();
        _address.Text = $"${EditorSettings.SidLoadAddress:X4}";
        _address.TextChanged += (_, _) => CheckAddress();

        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        _export.Click += async (_, _) => await ExportAsync();

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                Labelled("Name", _name),
                Labelled("Author", _author),
                Labelled("Released", _released),
                Labelled("Load address", _address),
                _range,
                new TextBlock { Text = $"{_bytes} bytes with the player, for the {(chip == ChipModel.Mos8580 ? "8580" : "6581")}.", Foreground = Dim },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _export, cancel },
                },
            },
        };

        CheckAddress();
    }

    private static Control Labelled(string label, Control field)
    {
        var row = new DockPanel();
        var caption = new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);
        row.Children.Add(field);
        return row;
    }

    private int? CheckAddress()
    {
        string? problem = SidLoadAddress.TryParse(_address.Text) is { } address
            ? SidLoadAddress.Validate(address, _bytes)
            : "Write the address in hex, as $1000.";

        if (problem is null && SidLoadAddress.TryParse(_address.Text) is { } ok)
        {
            _range.Text = $"Loads at ${ok:X4}-${ok + _bytes - 1:X4}; init ${ok:X4}, play ${ok + 3:X4}.";
            _range.Foreground = Dim;
            _export.IsEnabled = true;
            return ok;
        }

        _range.Text = problem;
        _range.Foreground = Warning;
        _export.IsEnabled = false;
        return null;
    }

    private async Task ExportAsync()
    {
        if (CheckAddress() is not { } address)
            return;

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to .sid",
            SuggestedFileName = (string.IsNullOrWhiteSpace(_name.Text) ? "Untitled" : _name.Text.Trim()) + ".sid",
            DefaultExtension = "sid",
            FileTypeChoices = [new FilePickerFileType("SID tunes") { Patterns = ["*.sid"] }],
        });
        if (target?.TryGetLocalPath() is not { } path)
            return;

        try
        {
            var metadata = new SidExporter.Metadata(_name.Text ?? "", _author.Text ?? "", _released.Text ?? "");
            File.WriteAllBytes(path, SidExporter.Export(_song, metadata, _chip, address));
            EditorSettings.SidAuthor = _author.Text ?? "";
            EditorSettings.SidLoadAddress = address;
            Close();
        }
        catch (Exception ex) when (ex is SidExportException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot export", ex.Message);
        }
    }
}
