using System.Globalization;
using MagSid.Core.Model;
using MagSid.Playback;

namespace MagSid.Editor.App.Controls;

/// <summary>
/// Inline instrument editor, embedded directly under the Instruments list instead of behind an
/// Edit... dialog, so picking a row is enough to start editing it.
///
/// Compressed from the original InstrumentEditorForm dialog's layout (2026-08-30, per Magnar's
/// own mockup; that dialog was removed entirely on 2026-08-31 once this panel had proven itself -
/// see MainForm's own history for the "Edit..." menu item and double-click wiring that used to
/// open it):
/// no GroupBox borders/captions/description lines anywhere - Voice and Pulse are one dense
/// label+field row instead of two bordered groups. There is no Name field, and no "Instrument
/// NN" heading either - the Instruments list to the left already shows the number and name of
/// whichever instrument is selected, so repeating it here would be redundant. The Wavetable and
/// Filter grids are one shared grid instead of two,
/// aligned by row/frame number. That merge is more than a visual
/// trick: both programs already advance exactly one step per frame starting the moment a note
/// triggers (VoiceEngine.Automate and FilterEngine.StepFrame both run from SongSequencer.Frame),
/// so "row N" already meant the same frame for both - the grids just hadn't been drawn that way.
/// A row's Filter column can be blank (no filter action that frame); the underlying
/// <see cref="Instrument.Filter"/> list is built from only the rows where it isn't, in order -
/// see <see cref="TryReadFilter"/>. One consequence: Up/Down/Remove now move a whole row, wave
/// and filter data together - there is no way to reorder just one program's steps independently
/// of the other any more, unlike the two-grid original.
///
/// No OK/Cancel/Save at all (2026-08-30): every field applies to the instrument the moment it
/// commits - a text field on Leave, a checkbox/spinner/combo on its own changed event, a grid
/// cell on CellEndEdit - by calling the same TryCommit() a Save button used to. This also means
/// switching to a different instrument mid-edit no longer silently discards anything: the
/// control losing focus fires its own commit before the new instrument's CurrentInstrumentChanged
/// gets to LoadInstrument. See <see cref="_suppressAutoCommit"/> for the one hazard this
/// introduced - several of those same events fire on LoadFrom's own programmatic field
/// assignment, not just a user edit.
///
/// The three blocks (voice/pulse, the merged grid, "after the last note") are plain Docked
/// children of this UserControl, not rows of a TableLayoutPanel and not wrapped in an AutoScroll
/// host (2026-08-31, fixing a real bug Magnar found by screenshot: at a reduced window height,
/// both an earlier fixed-size-plus-AutoScroll version and an intermediate TableLayoutPanel-with-
/// Fill version ended up hiding the row/frame toolbar buttons that lived here at the time -
/// AutoScroll scrolled the whole content away as one block, and a TableLayoutPanel's "Absolute"
/// row size turned out to be only a target that shrinks under space pressure, Absolute rows
/// included, so everything got proportionally squeezed into an overlapping mess instead. Dock
/// never shrinks a control below its own Height, so voice/ending now always render at full size
/// and only the merged grid (Dock=Fill) gives up space - it has its own internal row scrollbar
/// for when it does. Those toolbar buttons are gone now (see BuildProgramGroup - row/frame
/// editing is a right-click context menu on the grid instead), but the layout fix stands on its
/// own regardless of what used to occupy that space.
///
/// No standalone Waveform field either (2026-08-30) - row 0 of the merged grid is the only place
/// it's typed now. Verified directly against Startup\Tables.s, not assumed: an instrument with no
/// WAVE section compiles to the pooled program { waveform | GATE, $00 }, byte-identical to (and
/// deduplicated with) an explicit one-step WAVE section holding the same value - so requiring row
/// 0 to always carry it changes nothing about the assembled output, only where it's typed. See
/// <see cref="TryReadWavetable"/>'s own comment for the read side of this.
/// </summary>
public sealed class InstrumentEditorPanel : UserControl
{
    private Instrument? _instrument;

    private readonly TextBox _attackDecay = HexField();
    private readonly TextBox _sustainRelease = HexField();

    private readonly CheckBox _noHardRestart = new()
    {
        Text = "No HR",
        Appearance = Appearance.Button,
        AutoSize = true,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(8, 1, 0, 0),
    };

    // When this instrument's note is replaced, the hard restart keeps its waveform and clears
    // only the gate, instead of cutting it with $08 - see Instrument.SoftHardRestart.
    private readonly CheckBox _softHardRestart = new()
    {
        Text = "Soft HR",
        Appearance = Appearance.Button,
        AutoSize = true,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(4, 1, 0, 0),
    };

    private readonly NumericUpDown _pulseWidth = Spinner(0, 15, narrow: true);
    private readonly TextBox _pulseSweep = HexField();

    // Vibrator is the instrument's on/off switch for vibrato: off means no VIB section is written
    // (VibratoDepth 0), and a song where no instrument has one is assembled with none of the
    // player's vibrato code or tables. The two fields are the two table bytes: delay/speed
    // nibbles, then depth.
    private readonly CheckBox _vibrator = new()
    {
        Text = "Vibrator",
        Appearance = Appearance.Button,
        AutoSize = true,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(8, 1, 0, 0),
    };
    private readonly TextBox _vibDelaySpeed = HexField();
    private readonly TextBox _vibDepth = HexField();

    /// <summary>What a freshly enabled Vibrator starts from: delay 2 (8 frames), speed 4, depth
    /// $20 - slow and wide enough to hear on a lead without needing to be tuned first.</summary>
    private const byte DefaultVibratoDelaySpeed = 0x24;
    private const byte DefaultVibratoDepth = 0x20;

    private readonly CheckBox _pulseContinuous = new()
    {
        Text = "Cont.",
        Appearance = Appearance.Button,
        AutoSize = true,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
        Margin = new Padding(8, 1, 0, 0),
    };
    /// <summary>Wavetable/Arpeggio and Filter, merged - see this type's own doc comment for why
    /// that is sound rather than just cosmetic. One row per frame: Waveform/Note/Hex are that
    /// frame's wavetable step, Type/Mode/Res/Cutoff/Sweep are that frame's filter step, and
    /// either half can be blank independently of the other - a blank Waveform ends the wavetable
    /// program right there the same as before, a blank Type simply means no filter action this
    /// frame (see TryReadFilter).</summary>
    private readonly DataGridView _grid = ProgramGrid();

    private bool _suppressNoteHexSync;
    private bool _suppressModeMask;

    /// <summary>Guards every auto-commit trigger while <see cref="LoadFrom"/> is populating the
    /// fields for a newly-selected instrument. Several of the controls involved
    /// (CheckedChanged/ValueChanged/SelectedIndexChanged) fire on a programmatic assignment just
    /// as readily as on a user edit - without this, loading an instrument would immediately
    /// TryCommit() using whatever scalar fields LoadFrom had set *so far*, against a grid that is
    /// often still empty (it is repopulated last), overwriting the very instrument being
    /// loaded.</summary>
    private bool _suppressAutoCommit;

    private readonly NumericUpDown _arpLoopFrom = Spinner(0, 63);
    private readonly Label _arpLoopLabel = FieldLabel("  from step ");

    private readonly ComboBox _arpEnding = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 210,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
    };

    private static readonly string[] NoteChoices =
        [RootLabel, .. NoteName.AllNotes.Select(n => n.ToString())];

    private const string RootLabel = "ROOT";

    private static string HexText(NoteName note) => note.TableIndex.ToString("x2");

    /// <summary>DataGridView paints cell by cell straight to the screen, so anything that
    /// invalidates it while a song plays tears visibly. DoubleBuffered is protected, hence the
    /// two-line subclass rather than reflection.</summary>
    private sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid() => DoubleBuffered = true;
    }

    private static DataGridView ProgramGrid()
    {
        var grid = new BufferedGrid
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            // AllCells, not Fill (2026-08-31, per Magnar's own ask): each column sizes to fit
            // its own header/content instead of stretching proportionally to fill the grid's
            // full width - "Res+Voice" gets the room its long header needs and "Hex" doesn't get
            // stretched wide to match it. Leaves empty space to the right of the last column at
            // this panel's usual width, which is expected, not a bug.
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            BackgroundColor = EditorPalette.FieldBackground,
            GridColor = EditorPalette.Border,
            BorderStyle = BorderStyle.FixedSingle,
            EnableHeadersVisualStyles = false,
            Font = new Font("Consolas", 9.5f),
        };

        grid.DefaultCellStyle.BackColor = EditorPalette.FieldBackground;
        grid.DefaultCellStyle.ForeColor = EditorPalette.Mono;
        grid.DefaultCellStyle.SelectionBackColor = EditorPalette.Selection;
        grid.DefaultCellStyle.SelectionForeColor = EditorPalette.Title;
        grid.ColumnHeadersDefaultCellStyle.BackColor = EditorPalette.ButtonBackground;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = EditorPalette.Heading;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = EditorPalette.ButtonBackground;

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        return grid;
    }

    public InstrumentEditorPanel()
    {
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        BuildLayout();
    }

    /// <summary>Loads a different instrument into the fields. Any unsaved edit to whatever was
    /// showing before is discarded - see this type's own doc comment on why that is a known
    /// rough edge rather than a final design decision.</summary>
    public void LoadInstrument(Instrument instrument)
    {
        // Reentrancy guard, not just an optimization (found 2026-08-31 from a real crash report):
        // TryCommit() -> Saved -> MainForm rebuilds the Instruments list (SetInstruments) -> that
        // rebuild clears/reselects the list's grid -> CurrentInstrumentChanged fires again for
        // the very instrument that was just committed (same reference; committing mutates it in
        // place, and the list restores selection by Number) -> back into LoadInstrument. Without
        // this guard that reentrant call runs _grid.Rows.Clear() on this panel's own grid while
        // its CellMouseDown/EndEdit from the edit that started this whole cascade is still
        // unwinding on the stack, which WinForms rejects with "reentrant call to
        // SetCurrentCellAddressCore". Reloading the same instrument into itself was always
        // redundant anyway - every field already shows what was just committed.
        if (ReferenceEquals(_instrument, instrument))
        {
            return;
        }

        _suppressAutoCommit = true;
        _instrument = instrument;

        _grid.Rows.Clear();
        LoadFrom(instrument);
        _suppressAutoCommit = false;
    }

    /// <summary>Called by MainForm when the Menu's Show Hex-Values item is toggled - the
    /// checkbox used to live in this panel and could invalidate its own grid directly, but now
    /// that it is a menu item MainForm owns, this is how it reaches back in.</summary>
    public void RefreshHighHexDisplay() => _grid.Invalidate();

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        Padding = new Padding(12);

        var infoBlock = BuildInfoBlock();
        infoBlock.Dock = DockStyle.Top;

        // As tall as the groups need at this width: one line when they fit side by side, two or
        // more when the panel is narrow - rather than a fixed height that pushes the last ones
        // out of sight.
        void FitInfoBlock()
        {
            int height = infoBlock.GetPreferredSize(new Size(Math.Max(1, ClientSize.Width), 0)).Height;
            if (height > 0 && infoBlock.Height != height)
                infoBlock.Height = height;
        }

        Resize += (_, _) => FitInfoBlock();
        infoBlock.ControlAdded += (_, _) => FitInfoBlock();
        FitInfoBlock();

        var (grid, endingRow) = BuildProgramGroup();
        endingRow.Dock = DockStyle.Bottom;
        endingRow.Height = 34;
        // grid is already Dock=Fill, set in BuildProgramGroup.

        // Plain Dock, not a TableLayoutPanel (2026-08-31, fixing the bug described in this
        // type's own doc comment): Dock never shrinks a control below its own Height, whereas a
        // TableLayoutPanel's "Absolute" row size is only a target - once the container is
        // shorter than the rows' combined height it shrinks every row proportionally, Absolute
        // ones included, which used to hide the row/frame toolbar buttons that lived here behind
        // an overlapping mess at a reduced window height (those buttons are gone now - see
        // BuildProgramGroup - but the same fragility would have applied to anything else in
        // their place). Here only the grid (Dock=Fill) gives up space; the voice row and ending
        // row always render at full height.
        //
        // Docked children lay out in the reverse of Controls.Add order (see MainForm's own use
        // of the same rule for its left-panel controls) - added in this order so the final
        // on-screen stack, top to bottom, is: voice/pulse, the merged grid, then "after the last
        // note" pinned to the very bottom edge.
        Controls.Add(grid);
        Controls.Add(endingRow);
        Controls.Add(infoBlock);
    }

    /// <summary>
    /// The instrument's own settings as four small captioned groups - envelope, pulse, hard
    /// restart, vibrato - that sit side by side and wrap onto another line, a whole group at a
    /// time, when the panel is too narrow for all four. One long row used to run its last fields
    /// off the edge once SOFT HR joined it. No Name field - it is edited in the Instruments list.
    /// </summary>
    private Control BuildInfoBlock()
    {
        var groups = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(4, 4, 4, 2),
        };

        groups.Controls.Add(Group("ENVELOPE",
            FieldLabel("A/D"), _attackDecay, FieldLabel("S/R"), _sustainRelease));
        groups.Controls.Add(Group("PULSE",
            FieldLabel("Width"), _pulseWidth, FieldLabel("Sweep"), _pulseSweep, _pulseContinuous));
        groups.Controls.Add(Group("HARD RESTART", _noHardRestart, _softHardRestart));
        groups.Controls.Add(Group("VIBRATO",
            _vibrator, FieldLabel("D/S"), _vibDelaySpeed, FieldLabel("Depth"), _vibDepth));

        // Fields apply live rather than waiting for an explicit Save (removed - see this type's
        // own doc comment). Text fields commit on Leave (focus actually leaving, which a
        // programmatic LoadFrom assignment never triggers, so no _suppressAutoCommit guard is
        // needed for these two); the value-changed family of events fire on a programmatic
        // assignment too, so those are guarded.
        _attackDecay.Leave += (_, _) => TryCommit();
        _sustainRelease.Leave += (_, _) => TryCommit();
        _pulseSweep.Leave += (_, _) => TryCommit();
        _vibDelaySpeed.Leave += (_, _) => TryCommit();
        _vibDepth.Leave += (_, _) => TryCommit();
        _vibrator.CheckedChanged += (_, _) =>
        {
            if (_suppressAutoCommit)
                return;

            // Switching it on over a blank/zero depth would commit "no vibrato" straight back
            // off, so seed the fields first - the button would otherwise appear to do nothing.
            if (_vibrator.Checked && (!byte.TryParse(_vibDepth.Text, NumberStyles.HexNumber, null, out byte depth) || depth == 0))
            {
                _vibDelaySpeed.Text = DefaultVibratoDelaySpeed.ToString("x2");
                _vibDepth.Text = DefaultVibratoDepth.ToString("x2");
            }

            UpdateVibratoEnabled();
            TryCommit();
        };
        _noHardRestart.CheckedChanged += (_, _) => { if (!_suppressAutoCommit) TryCommit(); };
        _softHardRestart.CheckedChanged += (_, _) => { if (!_suppressAutoCommit) TryCommit(); };
        _pulseWidth.ValueChanged += (_, _) => { if (!_suppressAutoCommit) TryCommit(); };
        _pulseContinuous.CheckedChanged += (_, _) => { if (!_suppressAutoCommit) TryCommit(); };

        return groups;
    }

    /// <summary>A caption over a row of fields, in a thin frame - the grouping a GroupBox would
    /// give, without its heavy border and wasted header space.</summary>
    private static Control Group(string caption, params Control[] fields)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
        };
        row.Controls.AddRange(fields);

        var title = new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = EditorPalette.Subtitle,
            Font = new Font("Segoe UI", 6.75f, FontStyle.Bold),
            Margin = new Padding(3, 0, 0, 1),
        };

        var group = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 3, 6, 4),
            Margin = new Padding(0, 0, 8, 4),
        };
        group.Controls.Add(title);
        group.Controls.Add(row);
        group.Paint += (_, e) =>
        {
            using var pen = new Pen(EditorPalette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, group.Width - 1, group.Height - 1);
        };

        return group;
    }

    /// <summary>Returns the grid and the "after the last note" row separately - two different
    /// places in the layout now (see BuildLayout), where before the grid and its own side-column
    /// buttons shared one GroupBox body. Row/frame editing itself lives on the grid's own
    /// right-click context menu, wired up inline below.</summary>
    private (Control Grid, Control Ending) BuildProgramGroup()
    {
        var step = new DataGridViewTextBoxColumn
        {
            Name = "Step",
            HeaderText = "#",
            ReadOnly = true,
            // AllCells sizes this to whatever's actually on screen, which is one digit for the
            // first ten rows - MinimumWidth guarantees room for two so the column doesn't visibly
            // widen the moment a program reaches its 10th step.
            MinimumWidth = 32,
        };

        var waveform = new DataGridViewTextBoxColumn { Name = "Waveform", HeaderText = "Wave" };

        var note = new DataGridViewComboBoxColumn
        {
            Name = "Note",
            HeaderText = "Note",
            FlatStyle = FlatStyle.Flat,
        };
        note.Items.AddRange(NoteChoices);
        note.Items.Add(string.Empty);

        var hex = new DataGridViewTextBoxColumn { Name = "Hex", HeaderText = "Hex" };

        // Blank is a real, first-class choice here - see this type's own doc comment - so it is
        // listed first rather than tacked on at the end the way the Note column's blank is.
        var filterType = new DataGridViewComboBoxColumn
        {
            Name = "Type",
            HeaderText = "Filter",
            FlatStyle = FlatStyle.Flat,
        };
        filterType.Items.AddRange([string.Empty, "Sweep", "Setup"]);

        var mode = new DataGridViewTextBoxColumn { Name = "Mode", HeaderText = "Mode/Vol" };
        var res = new DataGridViewTextBoxColumn { Name = "Res", HeaderText = "Res+Voice" };
        var cutoff = new DataGridViewTextBoxColumn { Name = "Cutoff", HeaderText = "Cutoff" };
        var sweepRate = new DataGridViewTextBoxColumn { Name = "Sweep", HeaderText = "Sweep" };

        _grid.Columns.AddRange(step, waveform, note, hex, filterType, mode, res, cutoff, sweepRate);

        _grid.CellFormatting += (_, e) =>
        {
            string columnName = _grid.Columns[e.ColumnIndex].Name;
            if (columnName == "Step")
            {
                e.Value = e.RowIndex.ToString();
                e.FormattingApplied = true;
            }
            else if (columnName is "Waveform" or "Note" or "Type")
            {
                // Each program is tinted in its own column rather than all three sharing the "#"
                // one: the wavetable, note program and filter run at their own pace and stop at
                // their own last step, so one merged marker could only ever show one of them.
                ApplyPlaybackTint(e, columnName);
            }
            else if (columnName is "Hex" or "Mode")
            {
                ApplyHighHexFormatting(e);
            }
        };

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0)
                return;
            var row = _grid.Rows[e.RowIndex];
            switch (_grid.Columns[e.ColumnIndex].Name)
            {
                case "Note" when !_suppressNoteHexSync:
                    SyncHexFromNote(row);
                    break;
                case "Hex" when !_suppressNoteHexSync:
                    SyncNoteFromHex(row);
                    break;
                case "Type":
                    ApplyFilterTypeToRow(row);
                    break;
                case "Mode" when !_suppressModeMask:
                    NormalizeModeCell(row);
                    break;
            }
        };

        _arpEnding.Items.AddRange([
            "Loop - cycle the notes for as long as the note is held",
            "Hold - stay on the last note",
            "Root - settle on the row's own note",
        ]);
        // Root, not Loop's own index 0 - this only actually shows before anything has been
        // loaded (LoadFrom below overwrites it with the loaded instrument's own ArpeggioEnding
        // the moment any instrument, including a brand new one, is selected). The real default a
        // new instrument gets lives in Instrument.CreateDefault, deliberately not on the
        // ArpeggioEnding property itself - see that method's own doc comment for why.
        _arpEnding.SelectedIndex = (int)ArpeggioEnding.Root;

        // Row/frame editing moved off a permanent button row onto a right-click context menu on
        // the grid itself (2026-08-31) - the same treatment InstrumentListControl already gave
        // its own Add/Delete/Edit buttons, and for the same reason: a button row took permanent
        // vertical space whether or not there was anything to add/remove/move that moment.
        var addFrameItem = new ToolStripMenuItem("Add frame");
        var removeItem = new ToolStripMenuItem("Remove");
        var upItem = new ToolStripMenuItem("Up");
        var downItem = new ToolStripMenuItem("Down");

        var rowMenu = new ContextMenuStrip();
        DarkTheme.Apply(rowMenu);
        rowMenu.Items.AddRange([addFrameItem, removeItem, upItem, downItem]);
        rowMenu.Opening += (_, _) => removeItem.Enabled = upItem.Enabled = downItem.Enabled = _grid.CurrentRow is not null;
        _grid.ContextMenuStrip = rowMenu;

        // Right-click doesn't move the grid's own selection by itself - without this, Remove/Up/
        // Down would act on whatever row was selected *before* the right-click instead of the one
        // actually under the cursor (same fix InstrumentListControl already needed for its own
        // context menu).
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
        };

        addFrameItem.Click += (_, _) =>
        {
            AddMergedRow("41", string.Empty, string.Empty, null);
            TryCommit();
        };
        removeItem.Click += (_, _) =>
        {
            if (_grid.CurrentRow is { } r)
                _grid.Rows.Remove(r);
            TryCommit();
        };
        upItem.Click += (_, _) => { MoveRow(_grid, -1); TryCommit(); };
        downItem.Click += (_, _) => { MoveRow(_grid, +1); TryCommit(); };

        _grid.Dock = DockStyle.Fill;
        _grid.CellEndEdit += (_, _) =>
        {
            if (!_suppressAutoCommit)
                TryCommit();
        };

        var ending = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0),
        };
        ending.Controls.Add(FieldLabel("After the last note: "));
        ending.Controls.Add(_arpEnding);
        ending.Controls.Add(_arpLoopLabel);
        ending.Controls.Add(_arpLoopFrom);

        _arpEnding.SelectedIndexChanged += (_, _) =>
        {
            UpdateLoopFromVisibility();
            if (!_suppressAutoCommit)
                TryCommit();
        };
        _arpLoopFrom.ValueChanged += (_, _) =>
        {
            if (!_suppressAutoCommit)
                TryCommit();
        };

        return (_grid, ending);
    }

    // ---------------------------------------------------------------- load / commit

    private void LoadFrom(Instrument instrument)
    {
        _attackDecay.Text = instrument.AttackDecay.ToString("x2");
        _sustainRelease.Text = instrument.SustainRelease.ToString("x2");
        _noHardRestart.Checked = instrument.NoHardRestart;
        _softHardRestart.Checked = instrument.SoftHardRestart;

        _pulseWidth.Value = Math.Clamp(instrument.PulseWidth, 0, 15);
        // Shown as the raw byte GoatTracker-style ($00-$ff), same as A/D and S/R, rather than a
        // signed decimal delta - PulseSweep really is just a signed byte in two's complement
        // (see VoiceEngine.StepPulseWidth's own "& 0xFF" / sign-extend treatment of it), the
        // signed decimal in the .s file (`PW n +/-n`) is PatternsFileWriter's own choice of
        // notation for the same byte, not a different underlying value.
        _pulseSweep.Text = unchecked((byte)Math.Clamp(instrument.PulseSweep, -128, 127)).ToString("x2");
        _pulseContinuous.Checked = instrument.PulseContinuous;

        _vibrator.Checked = instrument.HasVibrato;
        _vibDelaySpeed.Text = (instrument.HasVibrato ? instrument.VibratoDelaySpeed : DefaultVibratoDelaySpeed).ToString("x2");
        _vibDepth.Text = (instrument.HasVibrato ? instrument.VibratoDepth : DefaultVibratoDepth).ToString("x2");
        UpdateVibratoEnabled();

        _arpEnding.SelectedIndex = (int)instrument.ArpeggioEnding;
        _arpLoopFrom.Value = Math.Clamp(instrument.ArpeggioLoopPoint, 0, 63);
        UpdateLoopFromVisibility();

        // No Waveform field any more - see this type's own doc comment on why row 0's Wave
        // column is the sole source of it now. Shown raw, not gate-forced: the old field
        // displayed instrument.Waveform exactly as stored, and this replaces it, not the
        // Wavetable group's own implicit-program fallback (VoiceEngine.WavetableOf), which is a
        // playback concern with a different reason to force the gate bit on.
        var waveforms = instrument.Wavetable.Count > 0
            ? instrument.Wavetable.Select(b => b.ToString("x2")).ToList()
            : [instrument.Waveform.ToString("x2")];
        var notes = instrument.Arpeggio.Select(n => n.ToString()).ToList();
        var hexes = instrument.Arpeggio.Select(HexText).ToList();

        // Rows go as far as whichever program is longer - a filter program running past the
        // last wavetable frame (or the other way round) still needs somewhere to show its own
        // remaining steps.
        int frameCount = Math.Max(waveforms.Count, notes.Count);
        int rowCount = Math.Max(frameCount, instrument.Filter.Count);

        for (int i = 0; i < rowCount; i++)
        {
            AddMergedRow(
                i < waveforms.Count ? waveforms[i] : string.Empty,
                i < notes.Count ? notes[i] : string.Empty,
                i < hexes.Count ? hexes[i] : string.Empty,
                i < instrument.Filter.Count ? instrument.Filter[i] : null);
        }
    }

    /// <summary>
    /// Lights the "#" cell of every program step this playback has actually reached. The point is
    /// the steps that stay dark: a wavetable can carry eight steps and the song only ever play the
    /// first three, and those other five are bytes in the exported .sid doing nothing - so this is
    /// how you find a program worth shortening.
    ///
    /// Accumulating, not a running position marker: a single moving highlight would have to be
    /// watched continuously to learn the same thing, and a step reached once in the whole song is
    /// still a step the song needs. Cleared when playback stops.
    /// </summary>
    /// <summary>
    /// The trail of steps already played, plus where the programs are at the instant currently
    /// coming out of the speakers. The playhead is passed in rather than read off the coverage
    /// because it has to be taken against the audio clock - the engine renders as far ahead as
    /// the output buffer is deep, so the sequencer's own position leads the sound.
    /// </summary>
    public void SetPlaybackCoverage(ProgramCoverage? coverage, int waveStep = -1, int arpStep = -1,
        int filterStep = -1)
    {
        // This runs on every poll - around 60 times a second - while the tints change at most
        // once per 20ms frame, and usually far less. Repainting regardless is what made the Wave
        // and Filter values flicker, so nothing is invalidated unless something really moved:
        // the playhead, or the trail (Version only moves when a step is reached for the first
        // time), or the instrument being shown.
        int version = coverage?.Version ?? -1;
        bool unchanged = ReferenceEquals(_coverage, coverage)
                         && waveStep == _playheadWave
                         && arpStep == _playheadArp
                         && filterStep == _playheadFilter
                         && version == _coverageVersion;

        _coverage = coverage;
        _playheadWave = waveStep;
        _playheadArp = arpStep;
        _playheadFilter = filterStep;
        _coverageVersion = version;

        if (!unchanged)
        {
            InvalidateTintedColumns();
        }
    }

    private ProgramCoverage? _coverage;
    private int _playheadWave = -1;
    private int _playheadArp = -1;
    private int _playheadFilter = -1;
    private int _coverageVersion = -1;

    private void InvalidateTintedColumns()
    {
        foreach (string name in new[] { "Waveform", "Note", "Type" })
        {
            var column = _grid.Columns[name];
            if (column is not null)
                _grid.InvalidateColumn(column.Index);
        }
    }

    /// <summary>Two levels in the program's own column: the bright cell is where it is right
    /// now, walking down as it runs; the dimmer ones are steps it has already been through, so
    /// what stays untinted after a pass through the song is what nothing ever played.</summary>
    private void ApplyPlaybackTint(DataGridViewCellFormattingEventArgs e, string columnName)
    {
        if (_coverage is not { } coverage || e.CellStyle is not { } style || e.RowIndex < 0)
        {
            return;
        }

        var (playhead, reached) = columnName switch
        {
            "Waveform" => (_playheadWave, coverage.Wavetable),
            "Note" => (_playheadArp, coverage.Arpeggio),
            _ => (_playheadFilter, coverage.Filter),
        };

        if (e.RowIndex == playhead)
            style.BackColor = PlayheadBackground;
        else if (e.RowIndex < ProgramCoverage.MaxSteps && reached[e.RowIndex])
            style.BackColor = ReachedBackground;
    }

    /// <summary>Two tints over the field background: 10% for a step this playback has been
    /// through, and a brighter one for where the program is right now. The dim trail is what
    /// shows an unused tail once the song has run; the bright cell is what you watch move.</summary>
    private static readonly Color ReachedBackground = Lighten(EditorPalette.FieldBackground, 0.10f);
    private static readonly Color PlayheadBackground = Lighten(EditorPalette.FieldBackground, 0.34f);

    private static Color Lighten(Color color, float amount) => Color.FromArgb(
        color.R + (int)((255 - color.R) * amount),
        color.G + (int)((255 - color.G) * amount),
        color.B + (int)((255 - color.B) * amount));

    private void UpdateVibratoEnabled()
    {
        _vibDelaySpeed.Enabled = _vibrator.Checked;
        _vibDepth.Enabled = _vibrator.Checked;
    }

    /// <summary>Validates everything first and only then writes to the instrument, so a
    /// rejected Save never leaves it half-updated.</summary>
    private bool TryCommit()
    {
        if (_instrument is not { } instrument)
        {
            return false;
        }

        if (!TryReadHex(_attackDecay, "Attack/Decay", out byte attackDecay) ||
            !TryReadHex(_sustainRelease, "Sustain/Release", out byte sustainRelease) ||
            !TryReadHex(_pulseSweep, "Pulse Sweep", out byte pulseSweepByte))
        {
            return false;
        }

        byte vibratoDelaySpeed = 0, vibratoDepth = 0;
        if (_vibrator.Checked)
        {
            if (!TryReadHex(_vibDelaySpeed, "Vibrato delay/speed", out vibratoDelaySpeed) ||
                !TryReadHex(_vibDepth, "Vibrato depth", out vibratoDepth))
            {
                return false;
            }

            if (vibratoDepth == 0)
            {
                Complain("Vibrato depth must be 01-ff - 00 would mean no vibrato; switch Vibrator off instead.");
                _vibDepth.Focus();
                return false;
            }
        }

        if (!TryReadWavetable(instrument, out var waveform, out var waveforms, out var arpeggio, out var ending))
            return false;

        if (!TryReadFilter(out var filter))
            return false;

        instrument.Waveform = waveform;
        instrument.AttackDecay = attackDecay;
        instrument.SustainRelease = sustainRelease;
        instrument.NoHardRestart = _noHardRestart.Checked;
        instrument.SoftHardRestart = _softHardRestart.Checked;
        instrument.PulseWidth = (int)_pulseWidth.Value;
        instrument.PulseSweep = unchecked((sbyte)pulseSweepByte);
        instrument.PulseContinuous = _pulseContinuous.Checked;

        // Switched off keeps the delay/speed the instrument last had rather than zeroing it, so
        // toggling Vibrator back on restores it; only the depth (which is the on/off state in the
        // file and the player) goes to 0.
        if (_vibrator.Checked)
            instrument.VibratoDelaySpeed = vibratoDelaySpeed;
        instrument.VibratoDepth = vibratoDepth;

        instrument.Wavetable.Clear();
        instrument.Wavetable.AddRange(waveforms);
        instrument.Arpeggio.Clear();
        instrument.Arpeggio.AddRange(arpeggio);
        instrument.ArpeggioEnding = ending;
        instrument.ArpeggioLoopPoint =
            ending == ArpeggioEnding.Loop ? (int)_arpLoopFrom.Value : 0;
        instrument.Filter.Clear();
        instrument.Filter.AddRange(filter);

        Saved?.Invoke(instrument);
        return true;
    }

    /// <summary>Fired after a successful Save, with the instrument just written - MainForm
    /// answers by refreshing the Instruments list (the name/program summary columns) and
    /// marking the song dirty.</summary>
    public event Action<Instrument>? Saved;

    private bool TryReadWavetable(
        Instrument instrument, out byte waveform, out List<byte> waveforms, out List<NoteName> notes,
        out ArpeggioEnding ending)
    {
        // Unchanged unless row 0 provides a new one below - the case an entirely blank grid
        // leaves it with nothing to derive from, which should mean "leave it as it was", not
        // silently reset to some other value.
        waveform = instrument.Waveform;
        waveforms = [];
        notes = [];
        ending = (ArpeggioEnding)Math.Max(0, _arpEnding.SelectedIndex);

        bool waveformsEnded = false, notesEnded = false;

        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            var row = _grid.Rows[i];
            int frame = i + 1;

            string waveformText = (row.Cells["Waveform"].Value as string ?? "").Trim();
            if (waveformText.Length == 0)
            {
                waveformsEnded = true;
            }
            else if (waveformsEnded)
            {
                Complain($"Frame {frame}: the waveform column already ended on an earlier frame, " +
                         "so this value would never be reached.");
                return false;
            }
            else if (!TryReadHexText(waveformText, $"Frame {frame} waveform", out byte stepValue))
            {
                return false;
            }
            else if (stepValue == 0)
            {
                Complain($"Frame {frame}: $00 is what ends a wavetable, so it cannot also be a step. " +
                         "Leave the cell blank instead.");
                return false;
            }
            else
            {
                waveforms.Add(stepValue);
            }

            string noteText = (row.Cells["Note"].Value as string ?? "").Trim();

            if (noteText.Length == 0 || noteText == RootLabel)
            {
                if (noteText == RootLabel)
                    ending = ArpeggioEnding.Root;
                notesEnded = true;
            }
            else if (notesEnded)
            {
                Complain($"Frame {frame}: the note column already ended on an earlier frame, " +
                         "so this note would never be reached.");
                return false;
            }
            else if (!NoteName.TryParse(noteText, out var note))
            {
                Complain($"Frame {frame}: '{noteText}' is not a note.");
                return false;
            }
            else
            {
                notes.Add(note);
            }
        }

        if (ending == ArpeggioEnding.Hold && notes.Count == 0)
        {
            Complain("\"Hold\" stays on the last note, so the note column needs at least one.");
            return false;
        }

        if (ending == ArpeggioEnding.Loop && _arpLoopFrom.Value >= Math.Max(1, notes.Count))
        {
            Complain($"The loop goes back to step {(int)_arpLoopFrom.Value}, so there has to be " +
                     "a note there. Step numbering starts at 0.");
            return false;
        }

        // A single wavetable step compiles to exactly the same pooled program as no WAVE
        // section at all (Startup\Tables.s: both become { step, $00 }, deduplicated by
        // content), so a one-row program always collapses into the base Waveform field rather
        // than ever being stored as a redundant one-entry WAVE section - verified directly
        // against Tables.s, not assumed from this port alone.
        if (waveforms.Count == 1)
        {
            waveform = waveforms[0];
            waveforms.Clear();
        }

        return true;
    }

    /// <summary>Builds the Filter program from only the rows whose Filter column isn't blank, in
    /// row order - see this type's own doc comment on why a blank row is a real "no filter
    /// action this frame" rather than a gap that needs representing.</summary>
    private bool TryReadFilter(out List<FilterStep> filter)
    {
        filter = [];

        foreach (DataGridViewRow row in _grid.Rows)
        {
            string type = (row.Cells["Type"].Value as string ?? "").Trim();
            if (type.Length == 0)
                continue;

            if (!TryReadFilterRow(row, out var step))
                return false;

            filter.Add(step);
        }

        return true;
    }

    private bool TryReadFilterRow(DataGridViewRow row, out FilterStep step)
    {
        step = default;

        bool isSetup = (row.Cells["Type"].Value as string) == "Setup";
        int frame = row.Index + 1;

        if (!TryReadHexCell(row, "Cutoff", $"Frame {frame} filter cutoff", out byte cutoff))
            return false;

        if (cutoff is < FilterStep.MinCutoff or > FilterStep.MaxCutoff)
        {
            Complain(
                $"Frame {frame} filter: cutoff must be ${FilterStep.MinCutoff:x2}-${FilterStep.MaxCutoff:x2}.\n\n" +
                "$00 ends a program and $ff marks a setup step, so neither can also mean a cutoff.");
            return false;
        }

        if (!isSetup)
        {
            step = FilterStep.Sweep(cutoff);
            return true;
        }

        if (!TryReadHexCell(row, "Mode", $"Frame {frame} filter mode+volume", out byte mode) ||
            !TryReadHexCell(row, "Res", $"Frame {frame} filter resonance+routing", out byte resonance))
        {
            return false;
        }

        string rateText = (row.Cells["Sweep"].Value as string ?? "").Trim();
        if (rateText.Length == 0)
            rateText = "0";

        if (!int.TryParse(rateText, out int rate) || rate is < -128 or > 127)
        {
            Complain($"Frame {frame} filter: the sweep rate is a signed number, -128 to 127. " +
                     "0 stops a sweep that is already running.");
            return false;
        }

        step = FilterStep.Setup(mode, resonance, cutoff, rate);
        return true;
    }

    // ---------------------------------------------------------------- small helpers

    private void ApplyHighHexFormatting(DataGridViewCellFormattingEventArgs e)
    {
        if (!EditorSettings.ShowHighHex)
            return;

        string text = (e.Value as string ?? "").Trim();
        if (text.Length == 0 || !byte.TryParse(text, NumberStyles.HexNumber, null, out byte raw))
            return;

        e.Value = ((byte)(raw | 0x80)).ToString("x2");
        e.FormattingApplied = true;
    }

    private void AddMergedRow(string waveform, string note, string hex, FilterStep? filterStep)
    {
        string type = filterStep is { IsSetup: true } ? "Setup" : filterStep is not null ? "Sweep" : "";
        string mode = filterStep?.ModeVolume?.ToString("x2") ?? "";
        string res = filterStep?.ResonanceRouting?.ToString("x2") ?? "";
        string cutoff = filterStep is { } fs ? fs.Cutoff.ToString("x2") : "";
        string rate = filterStep is { IsSetup: true } setupStep ? setupStep.SweepRate.ToString() : "";

        int index = _grid.Rows.Add(string.Empty, waveform, note, hex, type, mode, res, cutoff, rate);
        ApplyFilterTypeToRow(_grid.Rows[index]);
    }

    private void SyncHexFromNote(DataGridViewRow row)
    {
        string noteText = (row.Cells["Note"].Value as string ?? "").Trim();
        string hex = NoteName.TryParse(noteText, out var note) ? HexText(note) : "";

        _suppressNoteHexSync = true;
        row.Cells["Hex"].Value = hex;
        _suppressNoteHexSync = false;
    }

    private void SyncNoteFromHex(DataGridViewRow row)
    {
        string hexText = (row.Cells["Hex"].Value as string ?? "").Trim().TrimStart('$');
        if (hexText.Length == 0)
        {
            return;
        }

        if (!byte.TryParse(hexText, NumberStyles.HexNumber, null, out byte raw))
        {
            Complain($"'{hexText}' is not a hex value.");
            return;
        }

        int index = raw & 0x7F;
        if (index > 0x5F)
        {
            Complain($"${raw:x2} does not correspond to a note (valid: $00..$5f or $80..$df).");
            return;
        }

        var note = NoteName.AllNotes[index];
        _suppressNoteHexSync = true;
        row.Cells["Note"].Value = note.ToString();
        row.Cells["Hex"].Value = HexText(note);
        _suppressNoteHexSync = false;
    }

    private void NormalizeModeCell(DataGridViewRow row)
    {
        string text = (row.Cells["Mode"].Value as string ?? "").Trim();
        if (text.Length == 0 || !byte.TryParse(text, NumberStyles.HexNumber, null, out byte raw))
            return;

        byte masked = (byte)(raw & 0x7f);
        if (masked == raw)
            return;

        _suppressModeMask = true;
        row.Cells["Mode"].Value = masked.ToString("x2");
        _suppressModeMask = false;
    }

    /// <summary>Cutoff needs a filter step of either kind; Mode/Res/Sweep-rate need Setup
    /// specifically. A blank Type (no filter action this frame) blanks and locks all four - see
    /// this type's own doc comment on why that is a real, valid state now rather than every row
    /// needing some filter data.</summary>
    private static void ApplyFilterTypeToRow(DataGridViewRow row)
    {
        string type = row.Cells["Type"].Value as string ?? "";
        bool isSetup = type == "Setup";
        bool hasStep = type.Length > 0;

        var cutoffCell = row.Cells["Cutoff"];
        cutoffCell.ReadOnly = !hasStep;
        cutoffCell.Style.ForeColor = hasStep ? EditorPalette.Mono : EditorPalette.Subtitle;
        if (!hasStep)
            cutoffCell.Value = "";
        else if (string.IsNullOrWhiteSpace(cutoffCell.Value as string))
            cutoffCell.Value = "08";

        foreach (string column in new[] { "Mode", "Res", "Sweep" })
        {
            var cell = row.Cells[column];
            cell.ReadOnly = !isSetup;
            cell.Style.ForeColor = isSetup ? EditorPalette.Mono : EditorPalette.Subtitle;

            if (!isSetup)
                cell.Value = "";
            else if (string.IsNullOrWhiteSpace(cell.Value as string))
                cell.Value = column == "Mode" ? "1f" : column == "Res" ? "f1" : "0";
        }
    }

    private static void MoveRow(DataGridView grid, int delta)
    {
        if (grid.CurrentRow is not { } row)
            return;

        int target = row.Index + delta;
        if (target < 0 || target >= grid.Rows.Count)
            return;

        grid.Rows.Remove(row);
        grid.Rows.Insert(target, row);

        var firstEditable = row.Cells.Cast<DataGridViewCell>().First(c => !c.ReadOnly);
        grid.CurrentCell = firstEditable;
    }

    private void UpdateLoopFromVisibility()
    {
        bool loops = _arpEnding.SelectedIndex == (int)ArpeggioEnding.Loop;
        _arpLoopLabel.Visible = _arpLoopFrom.Visible = loops;
    }

    private bool TryReadHex(TextBox box, string what, out byte value)
    {
        if (TryReadHexText(box.Text, what, out value))
            return true;

        box.Focus();
        return false;
    }

    private bool TryReadHexCell(DataGridViewRow row, string column, string what, out byte value) =>
        TryReadHexText(row.Cells[column].Value as string ?? "", what, out value);

    private bool TryReadHexText(string text, string what, out byte value)
    {
        if (byte.TryParse(text.Trim(), NumberStyles.HexNumber, null, out value))
            return true;

        Complain($"{what} must be a hex byte, 00-ff.");
        return false;
    }

    private void Complain(string message) =>
        MessageBox.Show(this, message, "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = EditorPalette.Body,
        Margin = new Padding(3, 0, 0, 0),
        Padding = new Padding(0, 5, 0, 0),
    };

    private static TextBox HexField() => new()
    {
        Width = 26,
        MaxLength = 2,
        TextAlign = HorizontalAlignment.Center,
        Margin = new Padding(2, 3, 2, 0),
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Mono,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9.5f),
    };

    private static NumericUpDown Spinner(int min, int max, bool narrow = false) => new()
    {
        Minimum = min,
        Maximum = max,
        // Pulse width only ever needs one digit's worth of room (0-15, i.e. a single hex
        // nibble) - narrow trims the box down from the usual two-decimal-digit width.
        TextAlign = HorizontalAlignment.Center,
        Width = narrow ? 36 : 60,
        Margin = new Padding(2, 3, 2, 0),
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Mono,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9.5f),
    };

}
