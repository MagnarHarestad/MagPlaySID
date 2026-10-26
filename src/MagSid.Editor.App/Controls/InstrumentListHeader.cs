using MagSid.Core.Midi;
using MagSid.Core.Validation;

namespace MagSid.Editor.App.Controls;

/// <summary>
/// The strip over an instrument list, the same in Retro and the DAW: its title, how many of the
/// 16 instruments are in use, and the buttons that act on the list - add, a menu of other ways to
/// add, duplicate and delete. Adding used to be only on a right-click menu, which nothing on
/// screen pointed to.
///
/// The menu offers a new plain instrument, any sound of the GM bank (read when the menu opens, so
/// files dropped into the user's bank folder show up without a restart), an instrument file, a
/// copy of the selected instrument, and the bank folder itself.
/// </summary>
public sealed class InstrumentListHeader : Panel
{
    public const int StripHeight = 28;

    // Windows' own icon font, on every Windows 10 and 11; plain characters where it is missing.
    private static readonly bool HasIconFont =
        new Font("Segoe MDL2 Assets", 9f).Name == "Segoe MDL2 Assets";

    private readonly Label _title = new() { AutoSize = true };
    private readonly Label _count = new() { AutoSize = true };
    private readonly Button _add = IconButton("", "+");
    private readonly Button _more = IconButton("", "v");
    private readonly Button _duplicate = IconButton("", "Dup");
    private readonly Button _delete = IconButton("", "Del");
    private readonly ToolTip _tips = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _menuDuplicate = new("&Duplicate selected");
    private readonly ToolStripMenuItem _menuNew = new("&New instrument") { ShortcutKeyDisplayString = "Ins" };
    private readonly ToolStripMenuItem _bankMenu = new("From sound &bank");
    private readonly ToolStripMenuItem _changeMenu = new("&Change sound of selected");
    private bool _full;

    public event Action? AddRequested;
    public event Action<GmBank.Sound>? AddFromBankRequested;

    /// <summary>A sound of the bank, onto the selected instrument in place of its own.</summary>
    public event Action<GmBank.Sound>? ChangeSoundRequested;
    public event Action? LoadRequested;
    public event Action? DuplicateRequested;
    public event Action? DeleteRequested;

    public InstrumentListHeader(string title, Color background, Color text, Color faint, Color hover)
    {
        Dock = DockStyle.Top;
        Height = StripHeight;
        BackColor = background;

        _title.Text = title;
        _title.ForeColor = text;
        _title.Location = new Point(4, 6);
        _count.ForeColor = faint;
        Controls.Add(_title);
        Controls.Add(_count);

        foreach (var button in new[] { _add, _more, _duplicate, _delete })
        {
            button.BackColor = background;
            button.ForeColor = text;
            button.FlatAppearance.MouseOverBackColor = hover;
            button.FlatAppearance.MouseDownBackColor = hover;
            Controls.Add(button);
        }
        _more.Width = 16;

        _tips.SetToolTip(_add, "New instrument (Ins)");
        _tips.SetToolTip(_more, "More ways to add: from the sound bank, from a file, a duplicate");
        _tips.SetToolTip(_duplicate, "Duplicate the selected instrument");
        _tips.SetToolTip(_delete, "Delete the selected instrument (Del)");

        _add.Click += (_, _) => AddRequested?.Invoke();
        _more.Click += (_, _) => _menu.Show(_more, new Point(0, _more.Height));
        _duplicate.Click += (_, _) => DuplicateRequested?.Invoke();
        _delete.Click += (_, _) => DeleteRequested?.Invoke();

        _menuNew.Click += (_, _) => AddRequested?.Invoke();
        _menuDuplicate.Click += (_, _) => DuplicateRequested?.Invoke();
        var load = new ToolStripMenuItem("&Load from file...", null, (_, _) => LoadRequested?.Invoke());
        var folder = new ToolStripMenuItem("Open bank &folder", null, (_, _) => OpenBankFolder());
        _menu.Items.AddRange([_menuNew, _bankMenu, load, new ToolStripSeparator(), _changeMenu, _menuDuplicate, new ToolStripSeparator(), folder]);
        _menu.Opening += (_, _) => FillBankMenu();
        DarkTheme.Apply(_menu);

        Resize += (_, _) => LayOut();
        LayOut();
        SetState(0, hasSelection: false);
    }

    /// <summary>How many instruments the song has, and whether one is selected - what decides
    /// which buttons can do anything.</summary>
    public void SetState(int count, bool hasSelection)
    {
        int max = SongValidator.MaxInstrumentNumber;
        _full = count >= max;
        _count.Text = $"{count}/{max}";
        _add.Enabled = _menuNew.Enabled = _bankMenu.Enabled = !_full;
        _duplicate.Enabled = _menuDuplicate.Enabled = hasSelection && !_full;
        _changeMenu.Enabled = hasSelection;                 // changes one in place: room or none
        _delete.Enabled = hasSelection;
        _tips.SetToolTip(_add, _full
            ? $"All {max} instruments are in use - a pattern names one with a single byte"
            : "New instrument (Ins)");
        LayOut();
    }

    private void LayOut()
    {
        int x = Width - 2;
        foreach (var button in new[] { _delete, _duplicate, _more, _add })
        {
            x -= button.Width;
            button.Location = new Point(x, (Height - button.Height) / 2);
            if (button == _more)
                x += 1;                                     // the arrow sits tight against +
        }
        _count.Location = new Point(Math.Max(_title.Right + 8, x - _count.PreferredWidth - 8), 7);
    }

    private void FillBankMenu()
    {
        _bankMenu.DropDownItems.Clear();
        AddBankItems(_bankMenu.DropDownItems, sound => AddFromBankRequested?.Invoke(sound));
        DarkTheme.Apply(_bankMenu.DropDown);
        _changeMenu.DropDownItems.Clear();
        AddBankItems(_changeMenu.DropDownItems, sound => ChangeSoundRequested?.Invoke(sound));
        DarkTheme.Apply(_changeMenu.DropDown);
    }

    /// <summary>One item per sound of the bank as it stands now - the built-in sounds with the
    /// user's own files over them - for this header's menu and the DAW's SOUNDBANK button alike.</summary>
    public static void AddBankItems(ToolStripItemCollection items, Action<GmBank.Sound> picked)
    {
        GmBank.BankSection? section = null;
        foreach (var sound in GmBank.Load(EditorSettings.BankDirectory).All)
        {
            if (section is not null && sound.Section != section)
                items.Add(new ToolStripSeparator());
            section = sound.Section;

            var item = new ToolStripMenuItem($"{SoundLabel(sound.Key)}  -  {sound.Instrument.Name} ({sound.Origin})");
            item.Click += (_, _) => picked(sound);
            items.Add(item);
        }
    }

    /// <summary>What a sound of the bank is, by its key - the menu's words and the sound bank
    /// cards' family line.</summary>
    public static string SoundLabel(string key) => GmBank.Label(key);

    /// <summary>Where files of the user's own replace the bank's sounds - made on first use, so
    /// there is somewhere to save them. From the Store's package it is the package's own copy of
    /// the folder that Explorer is shown, the one the editor reads.</summary>
    public static void OpenBankFolder()
    {
        Directory.CreateDirectory(EditorSettings.BankDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PackagedApp.Visible(EditorSettings.BankDirectory),
            UseShellExecute = true,
        });
    }

    private static Button IconButton(string glyph, string fallback)
    {
        var button = new Button
        {
            Text = HasIconFont ? glyph : fallback,
            Font = HasIconFont ? new Font("Segoe MDL2 Assets", 9f) : new Font("Segoe UI", 8f),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(HasIconFont ? 24 : 30, 22),
            TabStop = false,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
