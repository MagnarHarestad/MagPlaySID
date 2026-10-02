using System.ComponentModel;
using System.Diagnostics;
using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Core.Serialization;
using MagSid.Core.Validation;
using MagSid.Editor.App.Controls;
using MagSid.Editor.App.Export;
using MagSid.Editor.App.Daw;
using MagSid.Editor.App.Midi;
using MagSid.Editor.App.Player;
using MagSid.Playback;
using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Editor.App;

public sealed class MainForm : Form
{
    private readonly EditorSession _session = new();
    private readonly AutosaveManager _autosave;

    private readonly InstrumentListControl _instrumentList = new();
    private readonly InstrumentEditorPanel _instrumentEditorPanel = new();
    private readonly TrackerGridControl _trackerGrid = new();

    /// <summary>The DAW view, built once and swapped in beside the retro layout rather than
    /// rebuilt on every switch - it carries selection and scroll state that should survive going
    /// away and coming back.</summary>
    private readonly DawView _dawView = new();

    /// <summary>MIDI input, shared by the knobs and the piano roll. Null until a device opens.</summary>
    private MidiInputService? _midi;
    private MidiKnobBindings? _midiBindings;
    private ToolStripMenuItem? _midiMenu;
    private ToolStripMenuItem? _viewToggleItem;
    private bool _dawShowing;

    // Minimum 0, not 1: Song.Speed=0 is a real, legal value - "tick every frame", which
    // e.g. the Lightforce port uses deliberately (its original has no internal speed divider
    // at all). A Minimum of 1 clamped a loaded Speed=0 up to a displayed "1" - silently wrong
    // even before touching the control - and because the spinner could then never go back down
    // to 0, saving afterwards permanently replaced the song's real speed with 1.
    private readonly NumericUpDown _speed = new()
    {
        Minimum = 0,
        Maximum = 255,
        Width = 50,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly ToolStripStatusLabel _pathLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _dirtyLabel = new();
    private readonly ToolStripButton _playStopButton = new("▶ Play");
    /// <summary>Move the edit cursor a page at a time. Not a song-order position: the three
    /// channels' order lists are independent, so there is no single index that means the same
    /// moment on all of them - a fixed row count does.</summary>
    private const int RowsPerPage = 16;

    private readonly ToolStripButton _prevPosition = new("◀");

    // Held as fields so HistoryChanged can grey them out when there is nothing left to undo.
    private readonly ToolStripMenuItem _undoItem = new("&Undo") { ShortcutKeys = Keys.Control | Keys.Z };
    private readonly ToolStripMenuItem _redoItem = new("&Redo") { ShortcutKeys = Keys.Control | Keys.Y };

    /// <summary>How big this song becomes when exported, live. Recomputed from the model on
    /// every edit rather than by running the assembler - see SongSizeCalculator, whose figures
    /// are pinned to real c6510 builds by SongSizeTests. Hovering breaks the total down.</summary>
    private readonly ToolStripLabel _songLengthLabel = new("Song Length: $000");
    private readonly ToolStripButton _nextPosition = new("▶");
    // Only needs to be fast enough to notice a new pattern/row-boundary reasonably promptly for
    // labels (PAT NN, marker prefixes) - the actual smooth-scroll animation no longer depends on
    // this timer's cadence at all, see TrackerGridControl's AudioClockSamples/_playbackHistory.
    // 10 for the same reason TrackerGridControl's own timer uses it: WM_TIMER rounds up to whole
    // 15.6ms system ticks, and 15 sits close enough to the boundary to land on two ticks part of
    // the time (measured p90 of 29.9ms against 16.0ms at Interval=10).
    private readonly System.Windows.Forms.Timer _playbackPollTimer = new() { Interval = 10 };

    // Mirrors TrackerGridControl's own Edit/Jam state (Space toggles it there) so it's visible
    // regardless of which control currently has focus. A Button, not a Label, so clicking it
    // toggles the state too - the same action as Space, just reachable with the mouse.
    private readonly ToolStripButton _editModeLabel = new();

    // Which chip reSID emulates for both song playback and note preview. A button rather than a
    // dropdown - there are only two models, and a click that visibly flips the label is faster
    // to operate than opening a list to pick between two items.
    private readonly ToolStripButton _chipModelButton = new();
    private ChipModel _chipModel = ChipModel.Mos8580;

    /// <summary>Width of the Instruments column. Also the root splitter's starting distance, so
    /// the window can be sized to fit both panes exactly - see the constructor. Widened from the
    /// original 260 for InstrumentEditorPanel's own fixed content width (596) plus room for its
    /// AutoScroll vertical scrollbar - first-draft numbers, both worth revisiting together once
    /// the panel's layout settles.</summary>
    private const int LeftPanelWidth = 620;

    // Not docked: it is sized to exactly what its two halves need and centred in the window, so
    // the Instruments column and the Song grid stay side by side and the spare width of a
    // maximised window becomes margin either side of the pair rather than stretch inside it.
    // FixedPanel.Panel2 keeps the grid's fixed pixel columns the constant half if the splitter
    // is ever dragged, and stops the two panes rescaling proportionally on a resize - which
    // quietly moved the grid's left edge and made screen positions unpredictable.
    private readonly SplitContainer _rootSplit = new() { FixedPanel = FixedPanel.Panel2 };

    /// <summary>What <see cref="_rootSplit"/> is centred inside. Its own background is what shows
    /// in the margins, so they are the window's dark blue rather than the splitter's lighter
    /// border colour.</summary>
    private Panel? _retroHost;

    /// <summary>Instruments, the splitter, and the Song grid, with no slack anywhere in it. The
    /// same figure the window's own starting ClientSize is built from.</summary>
    private int NaturalRetroWidth =>
        LeftPanelWidth + _rootSplit.SplitterWidth + TrackerGridControl.ContentWidth + 1;

    /// <summary>Holds the Instruments list and the instrument editor at their own width inside
    /// the left pane, so widening the window leaves margin either side instead of stretching
    /// fields that have nothing to put in the extra space.</summary>
    private readonly Panel _retroColumn = new() { BackColor = EditorPalette.Background };

    private bool _suppressSpeedEvent;
    private WaveOutEvent? _waveOut;
    private SongPlaybackEngine? _playbackEngine;

    /// <summary>F3's own output device - see <see cref="PlayCurrentPattern"/>. Separate from
    /// <see cref="_waveOut"/> because it plays a synthetic song, not the one <see cref="_session"/>
    /// owns, so it must never be mistaken for real playback by anything that reasons about the
    /// song's own position (the resume machinery, the tracker grid's live-follow).</summary>
    private WaveOutEvent? _patternPreviewOutput;

    /// <summary>Kept only so <see cref="TrackerGridControl.ChannelMuteChanged"/> has an engine to
    /// apply a live mute toggle to while an F3 preview is playing - <see cref="_patternPreviewOutput"/>
    /// is the WaveOutEvent, not the engine that actually owns the SidChip.</summary>
    private SongPlaybackEngine? _patternPreviewEngine;
    private JamEngine? _jamEngine;
    private string? _lastExportedSidPath;


    /// <summary>Where each channel was when playback last stopped, for Space to resume from,
    /// together with where the edit cursor sat at that moment. The cursor is the test for
    /// whether the resume is still meaningful: leave it alone and Space carries on exactly,
    /// move it and Space plays from the new spot instead - which is what moving it meant.</summary>
    private SongSequencer.SequencerPosition? _resumeFrom;
    private (int Position, int Row)? _resumeCursor;

    public MainForm()
    {
        Text = "MagSID Editor";
        Icon = AppIcon.Value;

        // Sized to the content rather than a round number: the grid draws a fixed
        // TrackerGridControl.ContentWidth and nothing beyond it, so anything wider is empty
        // margin to the right of CH3. +1 keeps the grid's last vertical separator off the
        // very edge pixel. ClientSize (not Width/Height) so the border/title chrome doesn't
        // eat into the calculation.
        ClientSize = new Size(NaturalRetroWidth, 941);
        StartPosition = FormStartPosition.CenterScreen;

        // The tracker grid and every dialog were already dark; the shell around them was not, so
        // a dark grid sat in a light frame next to a light instrument list. See DarkTheme for
        // why the strips need a renderer rather than just a BackColor.
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;
        DarkTheme.ApplyTitleBar(this);

        var menu = BuildMenu();
        var toolbar = BuildToolbar();
        var status = BuildStatusBar();
        var body = BuildLayout();

        DarkTheme.Apply(menu);
        DarkTheme.Apply(toolbar);
        DarkTheme.Apply(status);

        Controls.Add(body);
        Controls.Add(toolbar);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;

        WireEvents();
        RefreshAll();
        _autosave = new AutosaveManager(_session);

        Shown += (_, _) =>
        {
            ApplyInitialSplitterDistances();
            ApplyMinimumSize();
            // Same reason as ApplyInitialSplitterDistances below: at the point BuildLayout() set
            // this the first time, _instrumentList's own Label/DataGridView hadn't been through
            // a real layout pass yet, so PreferredHeight's numbers weren't necessarily final -
            // re-applying it here, once the form is actually showing at its real size, is what
            // closed the gap Magnar found between the 16th instrument row and the editor
            // panel's own "Instrument NN" heading underneath it.
            _instrumentList.Height = _instrumentList.PreferredHeight;
        };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&Menu");

        // A&bout, not &About: "Save &As..." already owns A in this menu, and two items sharing a
        // mnemonic makes the key cycle the highlight instead of activating either one.
        var aboutItem = new ToolStripMenuItem("A&bout", null, (_, _) => DoAbout());
        var newItem = new ToolStripMenuItem("&New", null, (_, _) => DoNew());
        var openItem = new ToolStripMenuItem("&Open...", null, (_, _) => DoOpen());
        var saveItem = new ToolStripMenuItem("&Save", null, (_, _) => DoSave());
        var saveAsItem = new ToolStripMenuItem("Save &As...", null, (_, _) => DoSaveAs());
        var exportSidItem = new ToolStripMenuItem("&Export to .sid...", null, (_, _) => DoExportSid());
        var playInVsidItem = new ToolStripMenuItem("&Load and Play .sid...", null, (_, _) => DoLoadAndPlaySid());

        // Moved here from a checkbox inside the instrument editor panel itself - it is a
        // once-in-a-while display preference (see EditorPalette... no, EditorSettings.ShowHighHex
        // and InstrumentEditorPanel's own doc comment on the Wavetable/Filter grid), not something
        // that needs to sit in the middle of the fields it affects.
        var showHighHexItem = new ToolStripMenuItem("Show &High Hex-Values") { CheckOnClick = true };
        showHighHexItem.Checked = EditorSettings.ShowHighHex;
        showHighHexItem.CheckedChanged += (_, _) =>
        {
            EditorSettings.ShowHighHex = showHighHexItem.Checked;
            _instrumentEditorPanel.RefreshHighHexDisplay();
        };

        // Under the hex switch and checked the same way: both are one-line preferences about how
        // the editor behaves rather than actions, and this one is the kind a musician sets once
        // depending on which tracker taught them what Space means.
        var swapSpacebarItem = new ToolStripMenuItem("Swap &Spacebar Behavior") { CheckOnClick = true };
        swapSpacebarItem.Checked = EditorSettings.SwapSpacebar;
        swapSpacebarItem.CheckedChanged += (_, _) =>
        {
            EditorSettings.SwapSpacebar = swapSpacebarItem.Checked;
            UpdateEditModeLabel();
        };

        _undoItem.Click += (_, _) => StepHistory(undo: true);
        _redoItem.Click += (_, _) => StepHistory(undo: false);

        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());

        // Set as menu shortcuts rather than caught in ProcessCmdKey: WinForms then dispatches
        // them itself from anywhere in the window, and prints them beside the item so they are
        // discoverable instead of being folklore. F10 would otherwise pop the menu bar open -
        // claiming it here takes it before that default gets a chance.
        openItem.ShortcutKeys = Keys.F10;
        saveAsItem.ShortcutKeys = Keys.F11;
        exportSidItem.ShortcutKeys = Keys.F9;

        file.DropDownItems.AddRange([aboutItem, new ToolStripSeparator(), _undoItem, _redoItem,
            new ToolStripSeparator(), newItem, openItem, new ToolStripSeparator(), saveItem, saveAsItem, new ToolStripSeparator(), exportSidItem, playInVsidItem, new ToolStripSeparator(), showHighHexItem, swapSpacebarItem, new ToolStripSeparator(), exitItem]);
        menu.Items.Add(file);

        var signalPathItem = new ToolStripMenuItem("&Signal Path", null, (_, _) => DoSignalPath());
        menu.Items.Add(signalPathItem);

        var digiItem = new ToolStripMenuItem("&Digi to SID-osc", null, (_, _) => DoDigiToSidOsc());
        menu.Items.Add(digiItem);

        var optimizeItem = new ToolStripMenuItem("&Optimize", null, (_, _) => DoOptimize());
        menu.Items.Add(optimizeItem);

        // Beside Optimize, as asked. It swaps the middle of the window and nothing else: the menu,
        // the toolbar, the transport and the status bar stay exactly where they were, so switching
        // never moves what you were reaching for.
        _viewToggleItem = new ToolStripMenuItem("&DAW", null, (_, _) => ToggleView());
        menu.Items.Add(_viewToggleItem);

        _midiMenu = new ToolStripMenuItem("&MIDI");
        _midiMenu.DropDownOpening += (_, _) => BuildMidiMenu();
        menu.Items.Add(_midiMenu);

        return menu;
    }

    /// <summary>Opens the SID signal-path trace tool - a debug view, so it reads the live
    /// instrument list rather than a snapshot, but is otherwise independent of the main window
    /// (its own synthetic one-note song, its own SidChip).</summary>
    private void DoSignalPath() => new SignalPathForm(_session.Song.Instruments).Show(this);

    /// <summary>Opens the wav-to-ring-mod-instrument generator. Takes the whole session, not
    /// just the song, since "Add to Song" appends straight onto the live Song and needs
    /// EditorSession.NotifyStructureChanged() to make the rest of the UI notice.</summary>
    private void DoDigiToSidOsc() => new DigiToSidOscForm(_session).Show(this);

    /// <summary>The exported size, recomputed from the model. Cheap enough to run on every
    /// keystroke - it walks the patterns and pools once - so it is driven off the same two
    /// events as the rest of the UI rather than a timer, and is never stale.</summary>
    private void RefreshSongLength()
    {
        var size = SongSizeCalculator.Measure(_session.Song);

        // The last byte's offset from $1000, not the byte count - one less, and what lines up
        // with the "$1000-$11ec" ranges the assembler's memory map and VICE print. The tooltip
        // still gives the true count.
        _songLengthLabel.Text = $"Song Length: ${size.EndAddress - 0x1000:X3}";

        // The range follows whatever address the last export chose, since the length itself does
        // not depend on it - the player relocates without changing size.
        int loadAddress = EditorSettings.SidLoadAddress;
        _songLengthLabel.ToolTipText = string.Join(Environment.NewLine,
            $"{size.TotalBytes} bytes exported, ${loadAddress:X4}-${loadAddress + size.TotalBytes - 1:X4}",
            $"  player      {size.PlayerBytes,5}",
            $"  patterns    {size.PatternBytes,5}",
            $"  pattern ptr {size.PatternPointerBytes,5}",
            $"  instruments {size.InstrumentTableBytes,5}",
            $"  wavetables  {size.WavetablePoolBytes,5}",
            $"  arpeggios   {size.ArpeggioPoolBytes,5}",
            $"  filters     {size.FilterPoolBytes,5}",
            $"  song order  {size.OrderBytes,5}",
            $"  note table  {size.NoteTableBytes,5}");
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip();
        toolbar.Items.Add(new ToolStripButton("Open", null, (_, _) => DoOpen()));
        toolbar.Items.Add(new ToolStripButton("Save", null, (_, _) => DoSave()));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Song Speed:"));

        var speedHost = new ToolStripControlHost(_speed);
        toolbar.Items.Add(speedHost);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_playStopButton);
        toolbar.Items.Add(_editModeLabel);
        toolbar.Items.Add(_chipModelButton);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Scroll:"));
        toolbar.Items.Add(_prevPosition);
        toolbar.Items.Add(_nextPosition);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_songLengthLabel);

        return toolbar;
    }

    private StatusStrip BuildStatusBar()
    {
        var status = new StatusStrip();
        status.Items.Add(_pathLabel);
        status.Items.Add(_dirtyLabel);
        return status;
    }

    /// <summary>Centres Instruments and Song as one block, at the width the two of them actually
    /// need. A window narrower than that gets the whole width instead, and the splitter gives way
    /// on the Instruments side since the grid's columns are fixed.</summary>
    private void CentreRetroPanes()
    {
        if (_retroHost is not { } host)
        {
            return;
        }

        int width = Math.Min(NaturalRetroWidth, host.ClientSize.Width);
        _rootSplit.SetBounds(
            Math.Max(0, (host.ClientSize.Width - width) / 2), 0, width, host.ClientSize.Height);
    }

    /// <summary>Centres the Instruments column in the left pane, and lets it shrink below its own
    /// width rather than overflow when the pane is narrower than it - a window dragged small
    /// should clip evenly, not hide one edge.</summary>
    private void CentreRetroColumn()
    {
        var host = _rootSplit.Panel1;
        int width = Math.Min(LeftPanelWidth, host.ClientSize.Width);
        _retroColumn.SetBounds(
            Math.Max(0, (host.ClientSize.Width - width) / 2), 0, width, host.ClientSize.Height);
    }

    private Control BuildLayout()
    {
        // The splitter bar itself is painted by the SplitContainer, not by either panel, so
        // without this it stays a light grey seam between two dark halves.
        _rootSplit.BackColor = EditorPalette.Border;

        // Instruments keeps the top of the left column, sized to its own content (all 16 rows
        // plus Add/Delete) rather than Dock=Fill now that it shares the column with the editor
        // panel below - Fill would let it eat the space the editor needs.
        // InstrumentEditorPanel then takes the rest - the only instrument editor there is now
        // (see its own doc comment for why the old separate dialog was removed), following
        // whichever instrument is currently selected above. Docked children lay out in the
        // *reverse* of Add order (see InstrumentListControl's own header/buttons/grid comment) -
        // editor added first so it's processed last and gets what's left, instrumentList added
        // last so it's processed first and claims its own height off the top.
        // Both go inside a column of their own rather than straight into Panel1. Panel1 is the
        // panel that absorbs the extra width when the window is maximised, and neither of these
        // can use it: the editor's content is a fixed width and the list's columns only get
        // emptier as they stretch. The column keeps their width and CentreRetroColumn puts the
        // spare space on both sides of it instead.
        _instrumentEditorPanel.Dock = DockStyle.Fill;
        _retroColumn.Controls.Add(_instrumentEditorPanel);

        _instrumentList.Dock = DockStyle.Top;
        _instrumentList.Height = _instrumentList.PreferredHeight;
        _retroColumn.Controls.Add(_instrumentList);

        _rootSplit.Panel1.Controls.Add(_retroColumn);
        _rootSplit.Panel1.Resize += (_, _) => CentreRetroColumn();
        CentreRetroColumn();

        // The tracker grid gets the whole right-hand side: song-order editing lives in its own
        // per-channel order-track column now, so there's no separate order panel to split with.
        _trackerGrid.Dock = DockStyle.Fill;
        _rootSplit.Panel2.Controls.Add(_trackerGrid);

        _dawView.Dock = DockStyle.Fill;
        _dawView.Visible = false;
        _dawView.InstrumentEdited += _ =>
        {
            _session.MarkDirty();
            _instrumentList.SetInstruments(_session.Song.Instruments);
        };

        // A click in the piano roll is a short tap, not a held key: gate on at mouse-down, gate
        // off at mouse-up, with a fixed release so the tail is the same whatever the instrument's
        // own release is - see RollPreviewRelease.
        _dawView.NotePreviewRequested += (note, instrument) => PreviewNote(note, instrument, RollPreviewRelease);
        _dawView.NotePreviewReleased += ReleaseJamNote;

        // Through the session, the same as the retro list - so an instrument added here is added
        // the same way, with the same undo behaviour and the same numbering rules.
        _dawView.InstrumentAddRequested += () => _session.AddInstrument();
        _dawView.InstrumentCopyRequested += instrument => _session.CopyInstrument(instrument);
        _dawView.InstrumentDeleteRequested += instrument => _session.DeleteInstrument(instrument);

        _dawView.PatternEdited += () =>
        {
            _session.MarkDirty();
            _trackerGrid.SetSong(_session.Song);
            RefreshSongLength();
        };

        _dawView.OrderEdited += () =>
        {
            _session.MarkDirty();

            // The tracker shows the same order in its own column, and MarkDirty deliberately does
            // not repopulate controls - so it is told here rather than left a view behind.
            _trackerGrid.SetSong(_session.Song);
            RefreshSongLength();
        };

        // Both fill the same host. Added DAW first so the docked tracker is laid out last and keeps
        // the space it already had; only one is ever visible.
        var host = new Panel { Dock = DockStyle.Fill, BackColor = EditorPalette.Background };
        host.Controls.Add(_dawView);
        host.Controls.Add(_rootSplit);

        _retroHost = host;
        host.Resize += (_, _) => CentreRetroPanes();
        CentreRetroPanes();

        return host;
    }

    /// <summary>
    /// Lists the MIDI inputs, built each time it opens so a keyboard plugged in mid-session shows
    /// up without restarting.
    /// </summary>
    private void BuildMidiMenu()
    {
        if (_midiMenu is null)
            return;

        _midiMenu.DropDownItems.Clear();
        var devices = MidiInputService.Devices();

        if (devices.Count == 0)
        {
            _midiMenu.DropDownItems.Add(new ToolStripMenuItem("No MIDI inputs found") { Enabled = false });
            return;
        }

        for (int i = 0; i < devices.Count; i++)
        {
            int index = i;
            string name = devices[i];
            _midiMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => OpenMidi(index))
            {
                Checked = _midi?.DeviceName == name,
            });
        }

        _midiMenu.DropDownItems.Add(new ToolStripSeparator());
        _midiMenu.DropDownItems.Add(new ToolStripMenuItem("Close MIDI", null, (_, _) => CloseMidi())
        {
            Enabled = _midi?.IsOpen == true,
        });

        _midiMenu.DropDownItems.Add(new ToolStripMenuItem("Forget all knob bindings", null, (_, _) =>
        {
            EditorSettings.MidiKnobBindings = string.Empty;
            MessageBox.Show(this, "Knob bindings cleared. They take effect next time MagSID starts.",
                "MIDI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));
    }

    private void OpenMidi(int deviceIndex)
    {
        CloseMidi();

        var midi = new MidiInputService(this);
        if (!midi.Open(deviceIndex))
        {
            midi.Dispose();
            MessageBox.Show(this,
                "That MIDI input could not be opened. Another program usually has it.",
                "MIDI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _midi = midi;
        EditorSettings.MidiDevice = midi.DeviceName ?? string.Empty;

        _midiBindings ??= CreateBindings();

        midi.ControlChanged += (controller, value) => _midiBindings!.HandleControlChange(controller, value);
        midi.NotePressed += OnMidiNote;
        midi.NoteReleased += ReleaseJamNote;
    }

    private MidiKnobBindings CreateBindings()
    {
        var bindings = new MidiKnobBindings(EditorSettings.MidiKnobBindings);
        bindings.Changed += () => EditorSettings.MidiKnobBindings = bindings.Save();

        foreach (var knob in _dawView.Knobs())
            bindings.Register(knob);

        return bindings;
    }

    /// <summary>A key on the MIDI keyboard. It always sounds - hearing what you played is not
    /// conditional on it being recorded - and it is written down only in Edit mode, in whichever
    /// view is on screen.</summary>
    private void OnMidiNote(NoteName note, int velocity)
    {
        if (_dawShowing)
        {
            // Edit mode writes it into the roll, Jam mode only sounds it - the same rule the
            // tracker's own entry path follows, decided by the same toolbar switch, so flipping to
            // Jam means the same thing in both views.
            if (_trackerGrid.EditMode)
            {
                _dawView.StepInput(note);
            }

            int instrument = _dawView.SelectedInstrumentNumber
                             ?? _instrumentList.SelectedInstrument?.Number
                             ?? 1;
            PreviewNote(note, instrument);
            return;
        }

        // The tracker's own entry path, which already decides whether edit mode writes the note or
        // only plays it, advances the cursor, and picks the instrument. It raises NoteEntered on
        // the way, and that is already wired to PreviewNote - so the audition is not repeated here.
        _trackerGrid.EnterNote(note);
    }

    private void CloseMidi()
    {
        _midi?.Dispose();
        _midi = null;
    }

    /// <summary>
    /// Swaps the centre of the window between the tracker and the DAW view.
    ///
    /// Both edit the same Song object, so there is nothing to convert and nothing that can fall out
    /// of step - the two views are two ways of looking at one thing. The controls are kept alive on
    /// both sides rather than rebuilt, so selection and scroll survive a round trip.
    /// </summary>
    private void ToggleView()
    {
        _dawShowing = !_dawShowing;

        if (_dawShowing)
        {
            _dawView.LoadSong(_session.Song);
            if (_instrumentList.SelectedInstrument is { } selected)
                _dawView.SelectInstrument(selected.Number);
        }
        else if (_dawView.SelectedInstrumentNumber is { } number)
        {
            // Carry the DAW's own selection back, so the retro view opens on whatever was just
            // being worked on rather than jumping somewhere else.
            _instrumentList.SelectInstrumentNumber(number);
        }

        _rootSplit.Visible = !_dawShowing;
        _dawView.Visible = _dawShowing;

        if (_viewToggleItem is not null)
            _viewToggleItem.Text = _dawShowing ? "&Retro" : "&DAW";
    }

    /// <summary>SplitterDistance is a pixel offset into a control that, at the point
    /// BuildLayout() runs, hasn't been through layout yet and only has SplitContainer's tiny
    /// unparented default Size - setting it there produces an unpredictable, way-off-target
    /// splitter position once real Dock/Fill sizing kicks in. Setting it here, once the form
    /// has actually been shown at its real size, is the standard fix.</summary>
    private void ApplyInitialSplitterDistances()
    {
        _rootSplit.SplitterDistance = ClampSplitterDistance(_rootSplit, LeftPanelWidth);
    }

    /// <summary>
    /// Stops the window being dragged narrower than the three channels it exists to show. The
    /// tracker grid's columns are fixed pixel widths, so a narrower window does not reflow
    /// anything - it slices CH3 off the right edge, and there is no horizontal scrollbar to get
    /// it back. Height is left far more generous: fewer visible rows is a real trade-off someone
    /// might want, unlike a missing channel.
    ///
    /// Set from Shown rather than the constructor for the same reason the splitter distance is:
    /// MinimumSize is an outer-window size, so converting the client width the grid needs into
    /// one requires the border metrics, and those are not settled until the form has actually
    /// been shown.
    /// </summary>
    private void ApplyMinimumSize()
    {
        int chromeWidth = Width - ClientSize.Width;
        int chromeHeight = Height - ClientSize.Height;
        int neededClientWidth = NaturalRetroWidth;

        MinimumSize = new Size(neededClientWidth + chromeWidth, 420 + chromeHeight);
    }

    private static int ClampSplitterDistance(SplitContainer split, int desired)
    {
        int extent = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        int max = Math.Max(split.Panel1MinSize, extent - split.Panel2MinSize - split.SplitterWidth);
        return Math.Clamp(desired, split.Panel1MinSize, Math.Max(split.Panel1MinSize, max));
    }

    private void WireEvents()
    {
        _session.StructureChanged += RefreshAll;

        // Reopen whatever was used last, by name - a device's index moves when another is plugged
        // in, so reopening by number would quietly attach to the wrong keyboard.
        if (EditorSettings.MidiDevice is { Length: > 0 } wanted)
        {
            var devices = MidiInputService.Devices();
            int index = devices.ToList().FindIndex(name => name == wanted);
            if (index >= 0)
                OpenMidi(index);
        }
        _session.HistoryChanged += RefreshHistoryMenuState;
        _session.DirtyChanged += RefreshStatusBar;

        _instrumentList.AddRequested += () =>
        {
            if (_session.AddInstrument() is null)
            {
                MessageBox.Show(this,
                    $"This song already has all {SongValidator.MaxInstrumentNumber} instruments a " +
                    "pattern can select - a row names its instrument with a single $C0..$CF byte, " +
                    "so there is no seventeenth to name.",
                    "Instrument limit reached", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _instrumentList.CopyRequested += i =>
        {
            if (_session.CopyInstrument(i) is { } copy)
            {
                // StructureChanged (raised inside CopyInstrument) already repopulated the list
                // with the PREVIOUS selection restored (see InstrumentListControl.SetInstruments'
                // own doc comment) - moving to the new copy afterward is what actually makes
                // "Copy" land you on the thing you just made, ready to rename/edit, rather than
                // leaving you looking at the instrument you copied FROM.
                _instrumentList.SelectInstrumentNumber(copy.Number);
            }
            else
            {
                MessageBox.Show(this,
                    $"This song already has all {SongValidator.MaxInstrumentNumber} instruments a " +
                    "pattern can select - a row names its instrument with a single $C0..$CF byte, " +
                    "so there is no seventeenth to copy into.",
                    "Instrument limit reached", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _instrumentList.MoveRequested += (instrument, toNumber) =>
        {
            _session.MoveInstrument(instrument, toNumber);
            // instrument.Number was rewritten in place by MoveInstrument (it mutates the same
            // object), so this reads its NEW position - overriding SetInstruments' own generic
            // "restore the previously selected number" restore, which would otherwise follow the
            // OLD row position (now a different instrument) instead of the one that just moved.
            _instrumentList.SelectInstrumentNumber(instrument.Number);
        };
        _instrumentList.DeleteRequested += i => _session.DeleteInstrument(i);
        _instrumentList.InstrumentEdited += _ => _session.MarkDirty();
        _instrumentList.InstrumentSelected += () => _trackerGrid.Focus();
        _instrumentList.CurrentInstrumentChanged += instrument =>
        {
            if (instrument is not null)
            {
                _instrumentEditorPanel.LoadInstrument(instrument);
            }
        };
        _instrumentEditorPanel.Saved += _ =>
        {
            _session.MarkDirty();
            _instrumentList.SetInstruments(_session.Song.Instruments);
        };

        _trackerGrid.Edited += () => _session.MarkDirty();
        _trackerGrid.NoteEntered += PreviewNote;
        _trackerGrid.NoteReleased += ReleaseJamNote;
        _trackerGrid.InstrumentStepRequested += StepSelectedInstrument;
        _trackerGrid.JamSilenceRequested += () =>
        {
            _jamEngine?.SilenceAll();
            _dawView.ClearSoundingNotes();
        };
        _trackerGrid.EditModeChanged += UpdateEditModeLabel;
        _trackerGrid.SelectedInstrumentNumber = () =>
            _instrumentList.SelectedInstrument?.Number ?? _session.Song.Instruments.FirstOrDefault()?.Number ?? 1;
        _trackerGrid.AudioClockSamples = LivePlaybackSample;
        _trackerGrid.ChannelMuteChanged += (channel, muted) =>
        {
            _playbackEngine?.SetVoiceMuted(channel, muted);
            _patternPreviewEngine?.SetVoiceMuted(channel, muted);
        };
        UpdateEditModeLabel();

        // Refocus the grid after the click, or the very next keystroke (another Space, a note
        // key) would go to the toolbar button instead of where it's actually meant to land.
        _editModeLabel.Click += (_, _) =>
        {
            _trackerGrid.ToggleEditMode();
            _trackerGrid.Focus();
        };

        _chipModelButton.Click += (_, _) => ToggleChipModel();
        UpdateChipModelButton();

        _speed.ValueChanged += (_, _) =>
        {
            if (_suppressSpeedEvent)
                return;
            _session.Song.Speed = (int)_speed.Value;
            _session.MarkDirty();
        };

        _prevPosition.Click += (_, _) => _trackerGrid.MoveCursorRowPage(-RowsPerPage);
        _nextPosition.Click += (_, _) => _trackerGrid.MoveCursorRowPage(RowsPerPage);

        // Always from the top - unlike F2, a click here isn't "continue where I was", it's the
        // same "play the whole thing" action as F1, just reachable with the mouse.
        _playStopButton.Click += (_, _) =>
        {
            if (_waveOut is not null)
            {
                StopAnyPlayback();
            }
            else
            {
                StartFromBeginning();
            }
        };
        _playbackPollTimer.Tick += (_, _) => DrainPlaybackEvents();
        FormClosing += (_, _) => StopPlayback();
    }

    /// <summary>Mirrors TrackerGridControl.EditMode onto the toolbar. Colored rather than just
    /// relabeled, since a color flip reads at a glance while music is playing and eyes are on
    /// the scrolling grid, not the toolbar text.</summary>
    private void UpdateEditModeLabel()
    {
        bool edit = _trackerGrid.EditMode;
        // The key is on the label, not only in the menu: Space changes meaning silently when the
        // switch is flipped, and a song that starts playing because of a setting made weeks ago
        // reads as a fault.
        string key = EditorSettings.SwapSpacebar ? "F5" : "Space";
        _editModeLabel.Text = (edit ? "● EDIT" : "○ JAM") + " · " + key;
        _editModeLabel.ToolTipText = $"Edit/Jam ({key}) - click to flip";
        _editModeLabel.ForeColor = edit ? Color.OrangeRed : Color.MediumSeaGreen;
    }

    private void UpdateChipModelButton()
    {
        _chipModelButton.Text = _chipModel == ChipModel.Mos6581 ? "SID: 6581" : "SID: 8580";
    }

    /// <summary>Flips which chip reSID emulates. Live playback is restarted on the new model
    /// immediately rather than waiting for the next Play - otherwise the button would visibly
    /// change while the ear kept hearing the old chip, which reads as broken rather than
    /// deferred. The note-preview player is just dropped; it reopens lazily on the next
    /// keystroke, same as when Play takes the sound card from it.</summary>
    private void ToggleChipModel()
    {
        _chipModel = _chipModel == ChipModel.Mos6581 ? ChipModel.Mos8580 : ChipModel.Mos6581;
        UpdateChipModelButton();

        _jamEngine?.Dispose();
        _jamEngine = null;
        StopPatternPreview();

        if (_waveOut is not null)
        {
            // Captures the exact resume point (StopTransport, not the raw StopPlayback this used
            // to call directly) before restarting on the new chip - otherwise ResumePosition had
            // nothing fresh to go on and fell back to wherever the edit cursor was instead of
            // where playback had actually reached.
            StopTransport();
            StartFromCurrentPosition();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // All three outputs hold an audio device open, and none is owned by the control tree, so
        // none is torn down by the form's own Dispose.
        StopPlayback();
        StopPatternPreview();
        _jamEngine?.Dispose();
        _jamEngine = null;
        _autosave.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// F1-F4, from anywhere in the window. Handled here rather than in the tracker grid because
    /// the grid only sees a key while it holds focus - and clicking Play with the mouse leaves
    /// focus on the toolbar button, so a key handled only in the grid would miss it. That is also
    /// why the grid's own handler (the one that parks the edit cursor where playback reached)
    /// otherwise never ran on stop, throwing the cursor back to the top of the song.
    ///
    /// The speed spinner is left alone: it is the one place in this window where a keystroke
    /// belongs to a control rather than to the transport.
    /// </summary>
    /// <summary>True where a space is a space: the speed spinner, a hex field, a button, and a
    /// grid cell being renamed - the instrument list is a DataGridView, and an instrument called
    /// "Lead 2" has to be typeable. Everywhere else the window's own Space wins.</summary>
    private bool SpaceBelongsToTheFocusedControl() => ActiveControl switch
    {
        NumericUpDown or TextBoxBase or ButtonBase => true,
        DataGridView grid => grid.IsCurrentCellInEditMode,
        _ => false,
    };

    /// <summary>Play from the top, or stop if something is already playing - what Space does once
    /// the menu switch hands it the transport. The same start as F1 and the toolbar's Play, not
    /// F2's resume: a play button that begins somewhere other than the beginning is the kind of
    /// difference that gets blamed on the song.</summary>
    private void ToggleTransport()
    {
        if (_waveOut is null)
        {
            StartFromBeginning();
        }
        else
        {
            StopAnyPlayback();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F1 && ActiveControl is not NumericUpDown)
        {
            StartFromBeginning();
            return true;
        }
        if (keyData == Keys.F2 && ActiveControl is not NumericUpDown)
        {
            if (_waveOut is null)
            {
                StartFromCurrentPosition();
            }
            return true;
        }
        if (keyData == Keys.F3 && ActiveControl is not NumericUpDown)
        {
            PlayCurrentPattern();
            return true;
        }
        if (keyData == Keys.F4 && ActiveControl is not NumericUpDown)
        {
            StopAnyPlayback();
            return true;
        }

        // Space and F5 are a pair, and the menu switch decides which of them carries which job.
        // Both are claimed here rather than in the tracker grid so they work from the DAW view
        // too - the grid is hidden there and never sees a keystroke. A control that owns the
        // space key keeps it, because inside those a space is a space.
        if (keyData == Keys.Space && !SpaceBelongsToTheFocusedControl())
        {
            if (EditorSettings.SwapSpacebar)
            {
                ToggleTransport();
            }
            else
            {
                _trackerGrid.ToggleEditMode();
            }

            return true;
        }

        if (keyData == Keys.F5 && ActiveControl is not NumericUpDown)
        {
            _trackerGrid.ToggleEditMode();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>F1, and the ▶ Play button: always starts from the top of the song, whatever the
    /// cursor or a previous stop left behind - the way back to "play the whole thing" without
    /// first having to put the cursor somewhere.</summary>
    private void StartFromBeginning()
    {
        StopTransport();
        _resumeFrom = null;
        _resumeCursor = null;
        _trackerGrid.SetSongPosition(0);
        StartFromCurrentPosition();
    }

    /// <summary>F2: starts (or, mid-song, restarts) from wherever <see cref="ResumePosition"/>
    /// says - the exact spot playback last stopped if the cursor hasn't moved since, or the
    /// cursor's own position otherwise. No-op if already playing; F4 stops first.</summary>
    private void StartFromCurrentPosition()
    {
        StopPatternPreview();

        try
        {
            // Hand the sound card back before the song takes it: the note audition holds its own
            // output open for the session, and two WaveOut devices playing at once is asking for
            // trouble on a machine with an exclusive-mode driver. It reopens on the next note.
            _jamEngine?.Dispose();
            _jamEngine = null;

            var engine = new SongPlaybackEngine(_session.Song, _chipModel, ResumePosition());
            ApplyChannelMutes(engine);
            _waveOut = new WaveOutEvent();
            _waveOut.PlaybackStopped += (_, _) => BeginInvoke(StopPlayback);
            _waveOut.Init(engine);
            _waveOut.Play();
            _playbackEngine = engine;
            _trackerGrid.SetPlaybackActive(true);
            _playbackPollTimer.Start();
            _playStopButton.Text = "■ Stop";
        }
        catch (Exception ex)
        {
            _waveOut?.Dispose();
            _waveOut = null;
            MessageBox.Show(this, ex.Message, "Cannot start playback", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>F4, and the ■ Stop button (via the ▶ Play/■ Stop toggle): stops whichever is
    /// actually running, real song playback or an F3 pattern preview. No-op if neither
    /// is.</summary>
    private void StopAnyPlayback()
    {
        if (_patternPreviewOutput is not null)
        {
            StopPatternPreview();
            return;
        }

        StopTransport();
    }

    /// <summary>The real-playback half of <see cref="StopAnyPlayback"/> - parks the edit cursor
    /// where playback reached and records it as the F2 resume point.</summary>
    private void StopTransport()
    {
        if (_waveOut is null)
        {
            return;
        }

        // Both of these read the live clock and anchor history, so they have to happen before
        // StopPlayback tears the engine down. Taken at the position the listener last *heard*,
        // not the engine's produced frontier - the same clock the scrolling view follows, so the
        // resume and the parked cursor describe the same moment.
        _resumeFrom = _playbackEngine?.PositionAt(LivePlaybackSample());
        _trackerGrid.JumpEditCursorToLivePlaybackPosition();
        StopPlayback();
        // Recorded after the jump, since that is what decides where the cursor ends up.
        _resumeCursor = (_trackerGrid.SongPosition, _trackerGrid.CursorRow);
    }

    /// <summary>F3: loops just what's currently on screen - each channel's own pattern at the
    /// cursor's row, from that pattern's own start, not from the cursor's row within it - so a
    /// sound can be iterated on without playing the whole song up to it first. Self-contained:
    /// its own output device, not wired into the tracker grid's live-follow/scrolling the way
    /// real playback is, since a synthetic loop of borrowed patterns has no place in the song's
    /// own order for that to mean anything.</summary>
    private void PlayCurrentPattern()
    {
        if (_session.Song.Instruments.Count == 0)
        {
            return;
        }

        StopTransport();
        StopPatternPreview();
        _jamEngine?.Dispose();
        _jamEngine = null;

        var patterns = _trackerGrid.CurrentPatternsAtCursor();
        if (Array.TrueForAll(patterns, p => p is null))
        {
            return;
        }

        var preview = new Song { Speed = _session.Song.Speed };
        preview.Instruments.AddRange(_session.Song.Instruments);

        var seenNumbers = new HashSet<int>();
        foreach (var pattern in patterns)
        {
            if (pattern is not null && seenNumbers.Add(pattern.Number))
            {
                preview.Patterns.Add(pattern);
            }
        }

        // A channel whose order doesn't reach this row yet (a short/empty order) gets a silent
        // one-row loop instead of being left without a channel order at all - SongSequencer
        // requires every channel to have one.
        var silent = new Pattern { Number = preview.Patterns.Max(p => p.Number) + 1 };
        silent.Rows.Add(new HoldRow());
        preview.Patterns.Add(silent);

        for (int ch = 0; ch < 3; ch++)
        {
            int patternNumber = patterns[ch]?.Number ?? silent.Number;
            preview.ChannelOrders[ch].Tokens.Add(new PatternRefToken(patternNumber));
        }

        try
        {
            var engine = new SongPlaybackEngine(preview, _chipModel);
            ApplyChannelMutes(engine);
            _patternPreviewEngine = engine;
            _patternPreviewOutput = new WaveOutEvent();
            _patternPreviewOutput.Init(engine);
            _patternPreviewOutput.Play();
        }
        catch (Exception ex)
        {
            _patternPreviewEngine = null;
            _patternPreviewOutput?.Dispose();
            _patternPreviewOutput = null;
            MessageBox.Show(this, ex.Message, "Cannot play pattern", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopPatternPreview()
    {
        _patternPreviewOutput?.Stop();
        _patternPreviewOutput?.Dispose();
        _patternPreviewOutput = null;
        _patternPreviewEngine = null;
    }

    /// <summary>Carries the tracker grid's own per-channel mute state (see
    /// TrackerGridControl.ChannelMuteChanged) onto a freshly-created engine, so muting a channel
    /// and then pressing Play (or F3) doesn't silently forget it.</summary>
    private void ApplyChannelMutes(SongPlaybackEngine engine)
    {
        for (int ch = 0; ch < 3; ch++)
        {
            engine.SetVoiceMuted(ch, _trackerGrid.IsChannelMuted(ch));
        }
    }

    /// <summary>
    /// Where playback has actually reached, in engine samples - the clock the tracker grid
    /// scrolls to. See <see cref="AuditionClock.SamplesPlayed"/> for why this reads the device's
    /// play position rather than SongPlaybackEngine.SamplesProduced (measured: the difference is
    /// what made the grid's scroll look like it was snapping from note to note).
    /// </summary>
    private long LivePlaybackSample()
    {
        if (_waveOut is null || _playbackEngine is null)
        {
            return 0L;
        }

        return AuditionClock.SamplesPlayed(_waveOut, _playbackEngine) ?? 0L;
    }

    /// <summary>
    /// Where the next Play should begin.
    ///
    /// Unmoved cursor: the exact instant playback stopped at - every channel's order entry, row
    /// and loop bookkeeping together, so the parts pick up in the same relation to each other.
    ///
    /// Moved cursor: "play from here", which for this format cannot be done by handing every
    /// channel the same song position. The three orders are independent and need not cover the
    /// same span of time - Lightforce's channel 3 is 1024 ticks against 2048 on channels 1 and 2,
    /// so it runs its whole order twice per pass of the others, and its entry 4 is a different
    /// moment in the music than their entry 4. Lining the channels up on a shared index is what
    /// put the bass out of time.
    ///
    /// So the position is found by playing the song silently from the start up to the row under
    /// the cursor, and taking the state there. Every channel then holds whatever it genuinely
    /// holds at that moment, loop counters and all, without this having to model pattern lengths,
    /// row durations or LS/LE repeats - the sequencer already knows all of it.
    /// </summary>
    private SongSequencer.SequencerPosition? ResumePosition()
    {
        if (_resumeFrom is not null && _resumeCursor is { } parked &&
            parked.Position == _trackerGrid.SongPosition && parked.Row == _trackerGrid.CursorRow)
        {
            return _resumeFrom;
        }

        if (_trackerGrid.CursorSongLocation() is not { } target ||
            (target.OrderIndex == 0 && target.RowIndex == 0))
        {
            return null; // the top of the song needs no winding forward
        }

        return SongSequencer.FindPosition(
            _session.Song, target.Channel, target.OrderIndex, target.RowIndex);
    }

    private void StopPlayback()
    {
        if (_waveOut is null)
        {
            return;
        }

        var waveOut = _waveOut;
        _waveOut = null; // clear first so the PlaybackStopped handler above doesn't re-enter
        waveOut.Stop();
        waveOut.Dispose();

        _playbackPollTimer.Stop();
        _playbackEngine = null;
        _trackerGrid.SetPlaybackActive(false);
        _playStopButton.Text = "▶ Play";
        _instrumentEditorPanel.SetPlaybackCoverage(null);
        _dawView.ClearPlayback();
    }

    private void DrainPlaybackEvents()
    {
        if (_playbackEngine is null)
        {
            return;
        }
        while (_playbackEngine.TryDequeueRowEvent(out var rowEvent))
        {
            _trackerGrid.SetChannelPlaybackAnchor(
                rowEvent.Channel, rowEvent.PatternNumber, rowEvent.RowIndex, rowEvent.OrderEntrySequence,
                rowEvent.SampleIndex);
        }

        // The DAW's three views follow the same clock the tracker does - the device's play
        // position, not the sequencer's, which runs a whole output buffer ahead.
        if (_playbackEngine.PositionAt(LivePlaybackSample()) is { Channels.Count: 3 } position)
        {
            _dawView.SetPlayheads(
                position.Channels[0].OrderPos, position.Channels[1].OrderPos, position.Channels[2].OrderPos);
            _dawView.SetPlayRow(
                position.Channels[0].CurrentPatternNumber, position.Channels[0].RowIndex,
                position.Channels[1].CurrentPatternNumber, position.Channels[1].RowIndex,
                position.Channels[2].CurrentPatternNumber, position.Channels[2].RowIndex);
        }

        // The engine accumulates this itself, frame by frame, so polling at whatever rate this
        // timer happens to run at cannot miss a step - see ProgramCoverage.
        // Each view follows its own list. They are separate selections once either has been
        // clicked, and feeding the DAW the retro list's instrument lit the steps of whichever
        // instrument the other view happened to be on.
        int selected = _instrumentList.SelectedInstrument?.Number ?? -1;
        int dawSelected = _dawView.SelectedInstrumentNumber ?? selected;

        if (selected > 0)
        {
            // Against the device's play position, not the sequencer's - see PlayheadAt.
            _playbackEngine.PlayheadAt(LivePlaybackSample(), selected,
                out int waveStep, out int arpStep, out int filterStep);
            _instrumentEditorPanel.SetPlaybackCoverage(
                _playbackEngine.CoverageFor(selected), waveStep, arpStep, filterStep);
        }

        if (dawSelected > 0)
        {
            _playbackEngine.PlayheadAt(LivePlaybackSample(), dawSelected,
                out int dawWave, out int dawArp, out int dawFilter);
            _dawView.SetProgramPlayheads(dawWave, dawArp, dawFilter);
        }
        else
        {
            _instrumentEditorPanel.SetPlaybackCoverage(null);
        }
    }

    private void RefreshAll()
    {
        var song = _session.Song;
        RefreshSongLength();
        RefreshHistoryMenuState();

        _suppressSpeedEvent = true;
        _speed.Value = Math.Clamp(song.Speed, (int)_speed.Minimum, (int)_speed.Maximum);
        _suppressSpeedEvent = false;

        _instrumentList.SetInstruments(song.Instruments);
        _trackerGrid.SetSong(song);

        // Undo restores a whole-song snapshot rather than reversing an action, so the DAW view has
        // to be pointed at the new object too - it would otherwise go on editing the song that was
        // just undone, and the two views would silently disagree about what the song is.
        _dawView.LoadSong(song);

        RefreshStatusBar();
    }

    /// <summary>Undo restores a whole-song snapshot, so anything holding the old Song has to let
    /// go of it first - playback keeps its own reference and would carry on driving a song the
    /// editor has replaced. Stopping is the same thing opening a file does.</summary>
    private void StepHistory(bool undo)
    {
        if (undo ? !_session.CanUndo : !_session.CanRedo)
        {
            return;
        }

        StopPlayback();
        StopPatternPreview();

        if (undo)
            _session.Undo();
        else
            _session.Redo();
    }

    /// <summary>What every instrument's programs reach over one full pass of the song, as the
    /// plain summary the optimizer takes.</summary>
    private Dictionary<int, InstrumentUsage> BuildInstrumentUsage()
    {
        // Played here rather than relying on what was last heard: a live play-through is only as
        // complete as the listener made it, and a pass that stopped early reports steps as
        // unreached that the song does use. This runs the sequencer with no SID and no audio
        // device until every channel has been round its order list once - milliseconds, and
        // complete by construction.
        var analyzed = SongCoverageAnalyzer.Run(_session.Song);

        var usage = new Dictionary<int, InstrumentUsage>();
        foreach (var instrument in _session.Song.Instruments)
        {
            if (instrument.Number < 0 || instrument.Number >= analyzed.Count)
            {
                continue;
            }

            var coverage = analyzed[instrument.Number];
            usage[instrument.Number] = new InstrumentUsage(
                HighestSet(coverage.Wavetable), HighestSet(coverage.Arpeggio), HighestSet(coverage.Filter));
        }

        return usage;

        static int HighestSet(bool[] reached)
        {
            for (int i = reached.Length - 1; i >= 0; i--)
            {
                if (reached[i])
                    return i;
            }
            return -1;
        }
    }

    private void RefreshHistoryMenuState()
    {
        _undoItem.Enabled = _session.CanUndo;
        _redoItem.Enabled = _session.CanRedo;
    }

    /// <summary>Shrinks the pattern data without changing what the song plays - see
    /// SongOptimizer. Reports first and applies only on a yes, because it rewrites rows: the
    /// change lands on the undo stack like any other edit, but seeing what it will do beats
    /// finding out afterwards.</summary>
    private void DoOptimize()
    {
        var usage = BuildInstrumentUsage();
        var result = SongOptimizer.Analyze(_session.Song, usage);

        if (!result.FoundAnything)
        {
            MessageBox.Show(this,
                "Nothing to change - the pattern data is already as small as loops can make it." +
                Environment.NewLine +
                "Every instrument's programs are fully used." + Environment.NewLine + Environment.NewLine +
                $"Song length ${result.Before.EndAddress - 0x1000:X3}, patterns {result.Before.PatternBytes} bytes.",
                "Optimize", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var lines = result.Findings
            .Take(20)
            .Select(f => $"  PAT{f.PatternNumber:D2}  {f.Description}" +
                         (f.BytesSaved > 0 ? $"  (-{f.BytesSaved} bytes)" : ""))
            .ToList();
        if (result.Findings.Count > lines.Count)
            lines.Add($"  ...and {result.Findings.Count - lines.Count} more");

        string summary = result.BytesSaved > 0
            ? $"Saves {result.BytesSaved} bytes: " +
              $"${result.Before.EndAddress - 0x1000:X3} to ${result.After.EndAddress - 0x1000:X3}."
            : "Saves no bytes - a dead LS marker emits nothing - but the loops read as what they are.";

        const string caveat =
            "Program trims come from playing the whole song through silently, so they cover every " +
            "row of every pattern - no need to have listened to it first.";

        string blank = Environment.NewLine + Environment.NewLine;
        var answer = MessageBox.Show(this,
            summary + blank + string.Join(Environment.NewLine, lines) + blank + caveat + blank +
            "Apply? Ctrl+Z undoes it.",
            "Optimize", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        StopPlayback();
        StopPatternPreview();
        SongOptimizer.Apply(_session.Song, usage);
        _session.NotifyStructureChanged();
    }

    private void RefreshStatusBar()
    {
        // Wired to DirtyChanged, which MarkDirty raises on every value edit - a pattern row, an
        // instrument field, the speed spinner - so this is where the size tracks ordinary
        // editing. Structural changes come through RefreshAll instead.
        RefreshSongLength();

        _pathLabel.Text = _session.FilePath ?? "(unsaved)";
        _dirtyLabel.Text = _session.IsDirty ? "Modified" : "Saved";
        Text = $"MagSID Editor - {Path.GetFileName(_session.FilePath) ?? "New Song"}{(_session.IsDirty ? " *" : "")}";
    }

    private void DoNew()
    {
        if (!ConfirmDiscardIfDirty())
            return;

        StopSongForSwap();
        _session.New();
    }

    /// <summary>Playback has to stop before the song underneath it is replaced. The engine holds
    /// its own reference to the old song and keeps queueing row events against it, and those
    /// carry the old song's pattern numbers and row indices - applied to a freshly loaded song
    /// whose pattern 01 is shorter, they index past the end of it. That is what crashed loading
    /// Breakdance while BellaCiao was playing.</summary>
    private void StopSongForSwap()
    {
        StopPlayback();
        StopPatternPreview();
        _jamEngine?.SilenceAll();
        _dawView.ClearSoundingNotes();
    }

    private void DoOpen()
    {
        if (!ConfirmDiscardIfDirty())
            return;

        using var dialog = new OpenFileDialog { Filter = "Patterns.s files (*.s)|*.s|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            StopSongForSwap();
            _session.Load(dialog.FileName);
        }
        catch (PatternsFormatException ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoSave()
    {
        if (_session.FilePath is null)
        {
            DoSaveAs();
            return;
        }

        SaveTo(_session.FilePath);
    }

    private void DoSaveAs()
    {
        using var dialog = new SaveFileDialog { Filter = "Patterns.s files (*.s)|*.s|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        SaveTo(dialog.FileName);
    }

    /// <summary>Sounds a note as it's typed. Created on first use, then kept for the session so
    /// each keystroke doesn't pay for opening an output device. Failures are swallowed: losing
    /// the audition is a nuisance, but a dialog on every keypress because some other app grabbed
    /// the sound card would make the editor unusable.</summary>
    /// <summary>Release nibble a typed audition is capped at in Edit mode. Jam mode is a
    /// deliberate performance and always gets the instrument's real release; Edit mode retriggers
    /// rapidly as OS key-repeat writes row after row, and an instrument whose own release is long
    /// (up to $F, tens of seconds) would still be ringing out one keystroke's tail when the next
    /// one starts, piling releases on top of each other. $A (~1s on a real 6581) is long enough
    /// to hear the release's own shape, short enough that it doesn't.</summary>
    private const int MaxEditPreviewRelease = 0xA;

    /// <summary>Release nibble a note placed in the DAW piano roll is auditioned with - always,
    /// not only as a cap. A click is a tap, and $9 (~750ms on a 6581) gives every instrument the
    /// same short, audible tail instead of whatever its own release happens to be.</summary>
    private const int RollPreviewRelease = 0x9;

    private void PreviewNote(NoteName note, int instrumentNumber) => PreviewNote(note, instrumentNumber, null);

    /// <param name="forcedRelease">A release nibble to audition with in place of the
    /// instrument's own, or null for the usual behaviour (capped in Edit mode only).</param>
    private void PreviewNote(NoteName note, int instrumentNumber, int? forcedRelease)
    {
        var instrument = _session.Song.Instruments.FirstOrDefault(i => i.Number == instrumentNumber);
        if (instrument is null)
        {
            return;
        }

        StopPatternPreview(); // F3's own device, not guarded by _isPlaybackActive - hand it back

        if (forcedRelease is { } release)
        {
            instrument = WithRelease(instrument, release);
        }
        else if (_trackerGrid.EditMode)
        {
            instrument = CapReleaseForPreview(instrument);
        }

        try
        {
            _jamEngine ??= new JamEngine(_chipModel);
            _jamEngine.Start();
            // Both edit-mode note entry and live jamming go through this same NoteOn - the
            // instrument's own WAVE program always plays through at least once, however briefly
            // the key was actually held, before an early key-up's release actually lands. See
            // JamEngine.NoteOn's own doc comment.
            _jamEngine.NoteOn(note, instrument);

            // Only once it actually sounds: a key lit for a note nobody can hear would be worse
            // than no light at all.
            _dawView.SetNoteSounding(note, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException)
        {
            _jamEngine?.Dispose();
            _jamEngine = null;
        }
    }

    /// <summary>A shallow copy with just the release nibble capped, when it needs one at all -
    /// never mutates the song's own instrument, which every other voice, the real song, and .sid
    /// export all still read from. The Wavetable/Arpeggio/Filter programs are copied rather than
    /// shared: VoiceEngine only ever reads them, so sharing the same lists would have been safe
    /// too, but a copy costs nothing here and can't be made to alias the real instrument by a
    /// later change to either method.</summary>
    private static Instrument CapReleaseForPreview(Instrument instrument) =>
        (instrument.SustainRelease & 0x0F) <= MaxEditPreviewRelease
            ? instrument
            : WithRelease(instrument, MaxEditPreviewRelease);

    /// <summary>A copy with the release nibble replaced - the song's own instrument is never
    /// touched, for the reasons <see cref="CapReleaseForPreview"/> gives.</summary>
    private static Instrument WithRelease(Instrument instrument, int release)
    {
        if ((instrument.SustainRelease & 0x0F) == release)
        {
            return instrument;
        }

        var capped = new Instrument
        {
            Number = instrument.Number,
            Name = instrument.Name,
            Waveform = instrument.Waveform,
            AttackDecay = instrument.AttackDecay,
            SustainRelease = (byte)((instrument.SustainRelease & 0xF0) | (release & 0x0F)),
            ArpeggioEnding = instrument.ArpeggioEnding,
            ArpeggioLoopPoint = instrument.ArpeggioLoopPoint,
            PulseWidth = instrument.PulseWidth,
            PulseSweep = instrument.PulseSweep,
            PulseContinuous = instrument.PulseContinuous,
            NoHardRestart = instrument.NoHardRestart,
            VibratoDelaySpeed = instrument.VibratoDelaySpeed,
            VibratoDepth = instrument.VibratoDepth,
                SlideRate = instrument.SlideRate,
        };
        capped.Wavetable.AddRange(instrument.Wavetable);
        capped.Arpeggio.AddRange(instrument.Arpeggio);
        capped.Filter.AddRange(instrument.Filter);
        return capped;
    }

    /// <summary>The other half of <see cref="PreviewNote"/>: releases a jammed note's voice on
    /// key-up. No instrument to resolve here - JamEngine tracks which of its three voices this
    /// note occupies internally.</summary>
    private void ReleaseJamNote(NoteName note)
    {
        _jamEngine?.NoteOff(note);
        _dawView.SetNoteSounding(note, false);
    }

    /// <summary>+/- from the pattern view: moves the Instruments panel's own selection, which
    /// is what <see cref="SelectedInstrumentNumber"/> actually reads - so this is the same
    /// action as clicking a different row there, just reachable without leaving the pattern.
    /// Wraps rather than clamping at the ends, so repeatedly pressing the same direction cycles
    /// the whole list instead of getting stuck at instrument 1 or the last one.</summary>
    private void StepSelectedInstrument(int delta)
    {
        var instruments = _session.Song.Instruments.OrderBy(i => i.Number).ToList();
        if (instruments.Count == 0)
        {
            return;
        }

        int current = _instrumentList.SelectedInstrument?.Number ?? instruments[0].Number;
        int index = instruments.FindIndex(i => i.Number == current);
        if (index < 0)
        {
            index = 0;
        }

        int next = (index + delta + instruments.Count) % instruments.Count;
        _instrumentList.SelectInstrumentNumber(instruments[next].Number);
    }

    private void DoAbout()
    {
        using var about = new AboutForm();
        about.ShowDialog(this);
    }

    private void DoExportSid()
    {
        string defaultName = _session.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : "Untitled";

        // The dialog needs the finished size to tell a usable load address from one that would run
        // the tune into I/O, and it must be the size of what actually gets exported - dead
        // patterns are pruned on the way out, exactly as SidExporter does.
        int songByteLength = SongSizeCalculator.Measure(_session.Song.WithUnusedPatternsRemoved()).TotalBytes;

        using var metadataDialog = new SidExportDialog(
            defaultName, EditorSettings.SidAuthor, DateTime.Now.Year.ToString(),
            EditorSettings.SidLoadAddress, songByteLength);
        if (metadataDialog.ShowDialog(this) != DialogResult.OK)
            return;

        // Remembered as soon as it is confirmed, not after a successful write: an export that
        // fails on the assembler still had the name typed into it, and retyping it to try again
        // is the annoyance this removes.
        EditorSettings.SidAuthor = metadataDialog.Author;
        EditorSettings.SidLoadAddress = metadataDialog.LoadAddress;

        byte[] sidBytes;
        try
        {
            var metadata = new SidExporter.Metadata(metadataDialog.SongName, metadataDialog.Author, metadataDialog.Released);
            sidBytes = SidExporter.Export(_session.Song, metadata, _chipModel, metadataDialog.LoadAddress);
        }
        catch (SidExportException ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot export .sid", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var saveDialog = new SaveFileDialog
        {
            Filter = "SID files (*.sid)|*.sid|All files (*.*)|*.*",
            FileName = defaultName + ".sid",
        };
        if (saveDialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.WriteAllBytes(saveDialog.FileName, sidBytes);
            _lastExportedSidPath = saveDialog.FileName;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot export .sid", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Opens a .sid file in the player window, which runs the tune's own 6502 code.
    ///
    /// Any .sid, not only this session's export - but the export is what it is for: the tracker plays
    /// a C# port of the player, while this executes the bytes that were written to disk, so hearing
    /// them agree is the last check on an export. It defaults to the file just written for that
    /// reason.
    /// </summary>
    private void DoLoadAndPlaySid()
    {
        using var open = new OpenFileDialog
        {
            Filter = "SID tunes (*.sid)|*.sid|All files (*.*)|*.*",
            Title = "Load and play a .sid",
        };

        // Wherever a .sid was last opened from, falling back to where one was last exported to -
        // which is usually the same folder the first time anyone does this.
        if (EditorSettings.SidOpenDirectory is { Length: > 0 } remembered)
        {
            open.InitialDirectory = remembered;
        }
        else if (_lastExportedSidPath is not null && File.Exists(_lastExportedSidPath))
        {
            open.InitialDirectory = Path.GetDirectoryName(_lastExportedSidPath);
            open.FileName = Path.GetFileName(_lastExportedSidPath);
        }

        if (open.ShowDialog(this) != DialogResult.OK)
            return;

        if (Path.GetDirectoryName(open.FileName) is { Length: > 0 } opened)
            EditorSettings.SidOpenDirectory = opened;

        // The editor's own playback owns a SidChip too, and two of them mixing into one device is
        // not something anyone wants to hear.
        StopPlayback();

        try
        {
            // Owned by the form, and shown non-modally so the tracker stays usable behind it.
            var player = new SidPlayerForm(open.FileName);
            player.FormClosed += (_, _) => player.Dispose();
            player.Show(this);
        }
        catch (SidPlaybackException ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot play this .sid", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveTo(string path)
    {
        try
        {
            _session.Save(path);
            RefreshStatusBar();
        }
        catch (PatternsFormatException ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ConfirmDiscardIfDirty()
    {
        if (!_session.IsDirty)
            return true;

        var result = MessageBox.Show(
            this, "Discard unsaved changes?", "Unsaved changes",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        return result == DialogResult.Yes;
    }
}
