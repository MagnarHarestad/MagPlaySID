using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.Validation;
using MagSid.Editor.App;
using MagSid.Editor.App.Controls;

namespace MagSid.Editor.Avalonia;

/// <summary>
/// The Retro view's instrument list, as the Windows editor's InstrumentListControl and its header:
/// how many of the 16 are in use; add, and a menu of the other ways to add - a sound of the bank,
/// a file - with changing the selected instrument's sound, duplicating it and saving it to a file,
/// and the bank folder; duplicate and delete. A "+ New instrument" row stands in the first empty
/// slot, Insert adds and Delete deletes, a right-click offers the same, and a drag up or down
/// moves an instrument to another number.
/// </summary>
public sealed partial class MainWindow
{
    private const string NewInstrumentText = "+ New instrument";

    private readonly TextBlock _instrumentCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _addInstrument = new() { Content = "+" };
    private readonly Button _moreInstrument = new() { Content = "▾" };
    private readonly Button _duplicateInstrument = new() { Content = "Duplicate" };
    private readonly Button _deleteInstrument = new() { Content = "Delete" };

    /// <summary>The list being refilled: the selection changes that makes are not the user's.</summary>
    private bool _listing;

    /// <summary>The row a drag to reorder is at, -1 when none is under way.</summary>
    private int _dragIndex = -1;

    private Control BuildInstrumentListHeader()
    {
        ToolTip.SetTip(_addInstrument, "New instrument (Ins)");
        ToolTip.SetTip(_moreInstrument, "More ways to add: from the sound bank, from a file, a duplicate");
        ToolTip.SetTip(_duplicateInstrument, "Duplicate the selected instrument");
        ToolTip.SetTip(_deleteInstrument, "Delete the selected instrument (Del)");
        _addInstrument.Click += (_, _) => AddInstrument();
        _duplicateInstrument.Click += (_, _) => DuplicateInstrument();
        _deleteInstrument.Click += (_, _) => DeleteInstrument();

        // Filled as it opens, so files dropped into the bank folder show up without a restart.
        var more = new MenuFlyout();
        more.Opening += (_, _) => FillInstrumentMenu(more.Items);
        _moreInstrument.Flyout = more;

        _instrumentCount.Foreground = Dim;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { _addInstrument, _moreInstrument, _duplicateInstrument, _deleteInstrument },
        };
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "Instruments", Foreground = Accent }, _instrumentCount },
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        return header;
    }

    private void WireInstrumentList()
    {
        _instruments.SelectionChanged += (_, _) =>
        {
            if (_listing)
                return;

            // The "+ New instrument" row is not an instrument to select: a click on it adds one
            // (below), and arrowing onto it stays on the last instrument.
            if (_instruments.SelectedIndex == _session.Song.Instruments.Count && _session.Song.Instruments.Count > 0)
            {
                _listing = true;
                _instruments.SelectedIndex = _session.Song.Instruments.Count - 1;
                _listing = false;
                return;
            }

            ShowSelectedInstrument();
            UpdateInstrumentButtons();
        };

        _instruments.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Insert)
            {
                AddInstrument();
                e.Handled = true;
            }
            else if (e.Key is Key.Delete or Key.Back && SelectedInstrument is not null)
            {
                DeleteInstrument();
                e.Handled = true;
            }
        };

        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            bool selected = SelectedInstrument is not null;
            menu.ItemsSource = new Control[]
            {
                MenuEntry("Add", AddInstrument, !InstrumentsFull),
                MenuEntry("Duplicate", DuplicateInstrument, selected && !InstrumentsFull),
                MenuEntry("Delete", DeleteInstrument, selected),
            };
        };
        _instruments.ContextMenu = menu;

        // A right-click picks the row under it first, so Delete acts on what was clicked; a left
        // press on an instrument arms the drag that moves it.
        _instruments.AddHandler(PointerPressedEvent, (_, e) =>
        {
            int index = InstrumentIndexAt(e);
            var point = e.GetCurrentPoint(_instruments);
            if (point.Properties.IsRightButtonPressed && index >= 0 && index < _session.Song.Instruments.Count)
                _instruments.SelectedIndex = index;

            // Posted, so the list is done with its own click before it is refilled.
            if (point.Properties.IsLeftButtonPressed && index == _session.Song.Instruments.Count && !InstrumentsFull)
                Dispatcher.UIThread.Post(AddInstrument);

            _dragIndex = point.Properties.IsLeftButtonPressed && index >= 0 && index < _session.Song.Instruments.Count
                ? index
                : -1;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A move each time the drag crosses into another instrument's row, not each pixel; the
        // list is refilled after every move, and the next row is found again by position.
        _instruments.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_dragIndex < 0)
                return;
            if (!e.GetCurrentPoint(_instruments).Properties.IsLeftButtonPressed)
            {
                _dragIndex = -1;
                return;
            }

            int index = InstrumentIndexAt(e);
            if (index < 0 || index == _dragIndex || index >= _session.Song.Instruments.Count
                || _dragIndex >= _session.Song.Instruments.Count)
                return;

            var dragged = _session.Song.Instruments[_dragIndex];
            _session.MoveInstrument(dragged, index + 1);
            SelectInstrumentNumber(dragged.Number);
            _dragIndex = index;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _instruments.AddHandler(PointerReleasedEvent, (_, _) => _dragIndex = -1, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The row under the pointer, or -1.</summary>
    private int InstrumentIndexAt(PointerEventArgs e)
    {
        var hit = _instruments.InputHitTest(e.GetPosition(_instruments)) as Visual;
        var item = hit as ListBoxItem ?? hit?.FindAncestorOfType<ListBoxItem>();
        return item is null ? -1 : _instruments.IndexFromContainer(item);
    }

    private bool InstrumentsFull => _session.Song.Instruments.Count >= SongValidator.MaxInstrumentNumber;

    private void RefreshInstrumentNames()
    {
        _listing = true;
        try
        {
            int selected = Math.Max(0, _instruments.SelectedIndex);
            var rows = _session.Song.Instruments.Select(Describe).ToList();
            if (!InstrumentsFull)
                rows.Add(NewInstrumentText);
            _instruments.ItemsSource = rows;
            if (_session.Song.Instruments.Count > 0)
                _instruments.SelectedIndex = Math.Min(selected, _session.Song.Instruments.Count - 1);
        }
        finally
        {
            _listing = false;
        }

        UpdateInstrumentButtons();
    }

    /// <summary>Number, name, and the programs it carries - an arpeggio or a filter sweep is
    /// otherwise invisible until the instrument is opened.</summary>
    private static string Describe(Instrument instrument)
    {
        var programs = new List<string>(3);
        if (instrument.Arpeggio.Count > 0)
            programs.Add($"ARP{instrument.Arpeggio.Count}");
        if (instrument.Filter.Count > 0)
            programs.Add($"FLT{instrument.Filter.Count}");
        if (!instrument.HasDefaultPulse)
            programs.Add("PW");

        string name = InstrumentNames.Display(instrument);
        return programs.Count == 0
            ? $"{instrument.Number:00}  {name}"
            : $"{instrument.Number:00}  {name,-22} {string.Join(" ", programs)}";
    }

    private void UpdateInstrumentButtons()
    {
        int max = SongValidator.MaxInstrumentNumber;
        bool selected = SelectedInstrument is not null;
        _instrumentCount.Text = $"{_session.Song.Instruments.Count}/{max}";
        _addInstrument.IsEnabled = !InstrumentsFull;
        _duplicateInstrument.IsEnabled = selected && !InstrumentsFull;
        _deleteInstrument.IsEnabled = selected;
        ToolTip.SetTip(_addInstrument, InstrumentsFull
            ? $"All {max} instruments are in use - a pattern names one with a single byte"
            : "New instrument (Ins)");
    }

    private void FillInstrumentMenu(ItemCollection items)
    {
        bool selected = SelectedInstrument is not null;
        var fromBank = new MenuItem { Header = "From sound _bank", IsEnabled = !InstrumentsFull };
        foreach (var entry in BankEntries(AddFromBank))
            fromBank.Items.Add(entry);
        var change = new MenuItem { Header = "_Change sound of selected", IsEnabled = selected };
        foreach (var entry in BankEntries(sound =>
                 {
                     if (SelectedInstrument is { } target)
                         ChangeSound(target, sound);
                 }))
            change.Items.Add(entry);

        items.Clear();
        items.Add(MenuEntry("_New instrument", AddInstrument, !InstrumentsFull));
        items.Add(fromBank);
        items.Add(MenuEntry("_Load from file...", () => _ = LoadInstrumentsAsync(), !InstrumentsFull));
        items.Add(MenuEntry("_Save selected to file...", () => _ = SaveInstrumentAsync(), selected));
        items.Add(new Separator());
        items.Add(change);
        items.Add(MenuEntry("_Duplicate selected", DuplicateInstrument, selected && !InstrumentsFull));
        items.Add(new Separator());
        items.Add(MenuEntry("Open bank _folder", InstrumentListHeader.OpenBankFolder, true));
    }

    /// <summary>One item per sound of the bank as it stands now - the built-in sounds with the
    /// user's own files over them - a separator between its sections.</summary>
    private static IEnumerable<Control> BankEntries(Action<GmBank.Sound> picked)
    {
        GmBank.BankSection? section = null;
        foreach (var sound in GmBank.Load(EditorSettings.BankDirectory).All)
        {
            if (section is not null && sound.Section != section)
                yield return new Separator();
            section = sound.Section;

            // An underscore in a menu header marks its access key; a name's own is doubled.
            string header = $"{GmBank.Label(sound.Key)}  -  {sound.Instrument.Name} ({sound.Origin})".Replace("_", "__");
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => picked(sound);
            yield return item;
        }
    }

    private static MenuItem MenuEntry(string header, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A sound of the bank onto an instrument the song has, in place of its own - its
    /// number stays, so every note on it plays the new sound. One undo step.</summary>
    private void ChangeSound(Instrument target, GmBank.Sound sound)
    {
        _session.ReplaceInstrumentSound(target, sound.Instrument);
        SelectInstrumentNumber(target.Number);
        ShowNotice($"Instrument {target.Number:00} is now {target.Name} from the sound bank ({sound.Origin})");
    }

    /// <summary>A copy of the selected instrument under the next free number, selected so it is
    /// ready to rename and edit.</summary>
    private void DuplicateInstrument()
    {
        if (SelectedInstrument is not { } source)
            return;

        if (_session.CopyInstrument(source) is { } copy)
            SelectInstrumentNumber(copy.Number);
        else
            ShowInstrumentLimit();
    }

    /// <summary>The instrument selected in whichever view is on screen.</summary>
    private Instrument? CurrentInstrument()
    {
        int? number = _dawShowing ? _daw.SelectedInstrumentNumber ?? SelectedInstrument?.Number : SelectedInstrument?.Number;
        return _session.Song.Instruments.FirstOrDefault(i => i.Number == number);
    }

    private async Task SaveInstrumentAsync()
    {
        if (CurrentInstrument() is not { } instrument)
        {
            await MessageWindow.ShowAsync(this, "Save instrument", "Select an instrument to save first.");
            return;
        }

        string name = instrument.Name.Length > 0 ? instrument.Name : $"Instrument {instrument.Number:D2}";
        var options = new FilePickerSaveOptions
        {
            Title = "Save instrument",
            SuggestedFileName = string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".json",
            DefaultExtension = "json",
            FileTypeChoices = [InstrumentFiles],
        };
        if (EditorSettings.InstrumentDirectory is { Length: > 0 } remembered)
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(remembered);

        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file?.TryGetLocalPath() is not { } path)
            return;

        try
        {
            File.WriteAllText(path, InstrumentFile.Write(instrument));
            EditorSettings.InstrumentDirectory = Path.GetDirectoryName(path) ?? "";
            ShowNotice($"Saved {name} to {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(this, "Cannot save instrument", ex.Message);
        }
    }
}
