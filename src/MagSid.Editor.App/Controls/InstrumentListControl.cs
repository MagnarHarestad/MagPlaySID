using MagSid.Core.Midi;
using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Editor.App.Controls;

/// <summary>Grid of INSTRUMENTS: rows (number, name, and a summary of which programs it carries)
/// with Add/Delete. Numbers are assigned by EditorSession, never typed; waveform/AD/SR and the
/// WAVE/ARP/FLT/PW programs themselves are edited in the InstrumentEditorPanel embedded below
/// this list in MainForm, not here.</summary>
public sealed class InstrumentListControl : UserControl
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        // Default (RowHeaderSelect) only highlights the single cell clicked, since there are no
        // row headers here to click instead - FullRowSelect is what actually makes clicking #,
        // Name or Prog. highlight the whole instrument row, per Magnar's own ask.
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };

    private readonly ToolStripMenuItem _addItem = new("Add");
    private readonly ToolStripMenuItem _copyItem = new("Copy");
    private readonly ToolStripMenuItem _deleteItem = new("Delete");

    /// <summary>Shown in place of an actually-empty Name, the way every tracker since the
    /// format's C64 ancestors greets an unnamed instrument with something rather than a blank
    /// that reads as "not loaded yet". Purely a display default - an instrument's stored Name
    /// stays empty until something is actually typed over it.</summary>
    private const string UnnamedPlaceholder = InstrumentNames.Unnamed;

    /// <summary>Written in the first empty slot: clicking it adds an instrument there, where the
    /// eye goes looking for the next one.</summary>
    private const string NewInstrumentText = "+ New instrument";

    private IReadOnlyList<Instrument> _instruments = [];

    private readonly InstrumentListHeader _header = new("Instruments", EditorPalette.Background,
        EditorPalette.Heading, EditorPalette.Subtitle, EditorPalette.ButtonHover);
    private bool _suppressEvents;

    /// <summary>The row showing <see cref="NewInstrumentText"/>, or -1 when all 16 are used.</summary>
    private int _newInstrumentRow = -1;

    // Drag-to-reorder state: the row index the current left-button drag started from (or most
    // recently moved to - see the CellMouseMove handler), -1 when no drag is in progress. Row
    // index, not instrument number, since it tracks a *position* the drag is currently at, which
    // stays meaningful across the SetInstruments rebuild MoveRequested's own handler triggers
    // mid-drag (see MoveRequested's own doc comment).
    private int _dragRowIndex = -1;

    public event Action? AddRequested;
    public event Action<GmBank.Sound>? AddFromBankRequested;

    /// <summary>A sound of the bank, onto the selected instrument in place of its own.</summary>
    public event Action<Instrument, GmBank.Sound>? ChangeSoundRequested;
    public event Action? LoadRequested;
    /// <summary>The selected instrument should be cloned into a new instrument number - see
    /// EditorSession.CopyInstrument for what "cloned" actually copies.</summary>
    public event Action<Instrument>? CopyRequested;
    public event Action<Instrument>? DeleteRequested;
    public event Action<Instrument>? InstrumentEdited;

    /// <summary>Hold the left mouse button on an occupied row and drag up/down to reorder it -
    /// fires once per row boundary crossed (not once per pixel), each time naming the dragged
    /// instrument and the 1-based position it should now occupy. The handler is expected to call
    /// EditorSession.MoveInstrument and then re-select the instrument by its (now changed)
    /// Number - see MainForm's wiring - since the generic "restore the previously selected
    /// number" logic SetInstruments does on every rebuild would otherwise follow the OLD row
    /// position instead of the instrument that actually moved.</summary>
    public event Action<Instrument, int>? MoveRequested;

    /// <summary>The user picked a different instrument by interacting with this list directly -
    /// clicking a row or arrowing to one while it has focus. Not raised when
    /// <see cref="SetInstruments"/> or <see cref="SelectInstrumentNumber"/> change the selection
    /// programmatically (an instrument edit being saved, or the tracker grid's own +/- shortcut),
    /// since in both of those cases this list is not what the user is about to keep typing
    /// into. MainForm answers by moving focus to the tracker grid, so picking an instrument here
    /// and then typing a note works without an extra click back into the pattern.</summary>
    public event Action? InstrumentSelected;

    /// <summary>Fires on every selection change, focus or not - unlike <see cref="InstrumentSelected"/>,
    /// which is specifically about whether to steal focus. Wired by MainForm to the embedded
    /// InstrumentEditorPanel, which needs to follow the current selection regardless of how it
    /// changed (click, +/-, or SetInstruments' own restore-after-edit). Null when nothing is
    /// selected (an empty list).</summary>
    public event Action<Instrument?>? CurrentInstrumentChanged;

    public InstrumentListControl()
    {
        // Short header on purpose: a narrow docked panel, and waveform/AD/SR - the reason this
        // used to carry three more hex columns - are edited in the embedded InstrumentEditorPanel
        // now, so a single wider Name column replaces all three.
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Number", HeaderText = "#", ReadOnly = true, FillWeight = 50,
            // NotSortable on every column below too - found 2026-08-31 from a real accidental
            // click during verification: WinForms sorts an unbound DataGridView by header click
            // unless told not to, and this list's whole design depends on row index always
            // equalling slot number minus one (see InstrumentAtRow/SetInstruments' own doc
            // comment) - a header-click sort silently breaks that, so clicking a row afterward
            // can load a completely different instrument than the one actually displayed there,
            // with no error or visual sign anything is wrong.
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Name", HeaderText = "Name", FillWeight = 200, SortMode = DataGridViewColumnSortMode.NotSortable });

        // Read-only summary so it's visible at a glance which instruments carry programs -
        // otherwise an arpeggio or filter sweep is invisible until you open the editor.
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Programs",
            HeaderText = "Prog.",
            ReadOnly = true,
            FillWeight = 100,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        // This sits directly beside the tracker grid, which has always been dark - so the list,
        // its header and its buttons follow the same palette rather than leaving a light panel
        // pasted against a dark one. DarkTheme explains why the grid needs more than a BackColor.
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;
        DarkTheme.Apply(_grid);

        // A song can never hold more than SongValidator.MaxInstrumentNumber instruments (a
        // pattern row names one with a single $C0..$CF byte), so a list that can scroll past 16
        // rows is a list sized for a case that can't happen. Docked Top with a fixed height
        // instead of Fill, sized for exactly that many rows.
        _grid.Dock = DockStyle.Top;
        _grid.Height = _grid.ColumnHeadersHeight + _grid.RowTemplate.Height * SongValidator.MaxInstrumentNumber + 2;

        // Every way to add is posted rather than run inside the grid's own event: adding rebuilds
        // every row, which a DataGridView refuses to have done to it from its own mouse handler.
        _header.AddRequested += RequestAdd;
        _header.AddFromBankRequested += sound => BeginInvoke(() => AddFromBankRequested?.Invoke(sound));
        _header.ChangeSoundRequested += sound =>
        {
            if (SelectedInstrument is { } i)
                BeginInvoke(() => ChangeSoundRequested?.Invoke(i, sound));
        };
        _header.LoadRequested += () => LoadRequested?.Invoke();
        _header.DuplicateRequested += () => { if (SelectedInstrument is { } i) CopyRequested?.Invoke(i); };
        _header.DeleteRequested += () => { if (SelectedInstrument is { } i) DeleteRequested?.Invoke(i); };

        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Insert)
            {
                RequestAdd();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && SelectedInstrument is { } doomed)
            {
                DeleteRequested?.Invoke(doomed);
                e.Handled = true;
            }
        };
        _grid.CellMouseEnter += (_, e) => _grid.Cursor = e.RowIndex >= 0 && e.RowIndex == _newInstrumentRow ? Cursors.Hand : Cursors.Default;
        _grid.CellMouseLeave += (_, _) => _grid.Cursor = Cursors.Default;

        // Add/Delete moved off a button row (which sat between the header and the grid, taking
        // up permanent vertical space whether or not anything needed doing) onto a right-click
        // context menu on the grid itself instead.
        var menu = new ContextMenuStrip();
        DarkTheme.Apply(menu);
        menu.Items.AddRange([_addItem, _copyItem, _deleteItem]);
        menu.Opening += (_, _) => _copyItem.Enabled = _deleteItem.Enabled = SelectedInstrument is not null;
        _grid.ContextMenuStrip = menu;

        // Right-click doesn't move the grid's own selection by itself - without this, Delete
        // from the menu would act on whatever row was selected *before* the right-click instead
        // of the one actually under the cursor. Left-click on an occupied row also arms the
        // drag-to-reorder gesture below - an empty slot has nothing to move, so it's left as -1
        // (no drag) rather than letting a drag "start" from nothing.
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];

            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.RowIndex == _newInstrumentRow)
                RequestAdd();

            _dragRowIndex = e.Button == MouseButtons.Left && e.RowIndex >= 0 && InstrumentAtRow(e.RowIndex) is not null
                ? e.RowIndex
                : -1;
        };

        // Fires once per row boundary the drag crosses, not once per pixel of mouse movement -
        // moving within the same row's own bounds repeatedly hits the e.RowIndex == _dragRowIndex
        // guard and does nothing. MouseButtons (the static, current-global-state property, not
        // this event's own e.Button) is checked too as a defensive fallback in case a MouseUp
        // outside the grid was ever missed - without it a drag that somehow lost its mouse-up
        // would keep reordering rows on every future hover indefinitely.
        _grid.CellMouseMove += (_, e) =>
        {
            if (_dragRowIndex < 0 || e.RowIndex < 0 || e.RowIndex == _dragRowIndex)
                return;
            if ((MouseButtons & MouseButtons.Left) == 0)
            {
                _dragRowIndex = -1;
                return;
            }
            if (InstrumentAtRow(_dragRowIndex) is not { } dragged)
            {
                _dragRowIndex = -1;
                return;
            }

            // MoveRequested's own handler (MainForm) calls EditorSession.MoveInstrument, which
            // raises StructureChanged synchronously - by the time this line returns, _grid has
            // already been through a full SetInstruments Rows.Clear()+rebuild. That's fine: the
            // next CellMouseMove hit-tests against whatever rows exist *then*, by screen
            // position, not by holding a reference to a row object that rebuild just replaced -
            // row index i still means "instrument at position i+1" after every rebuild (see
            // SetInstruments' own invariant), so this keeps tracking the same drag correctly.
            MoveRequested?.Invoke(dragged, e.RowIndex + 1);
            _dragRowIndex = e.RowIndex;
        };

        _grid.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _dragRowIndex = -1;
        };

        _grid.SelectionChanged += (_, _) =>
        {
            // _suppressEvents also covers this (found 2026-08-31 from a real crash report): while
            // SetInstruments is rebuilding, Rows.Clear() and every Rows.Add() can themselves move
            // CurrentCell and fire SelectionChanged - not just the final SelectRow that's actually
            // restoring the selection on purpose. Without this check, one of those interim,
            // meaningless selection changes fires CurrentInstrumentChanged straight back out to
            // MainForm, which calls InstrumentEditorPanel.LoadInstrument - reentrant against
            // whatever grid edit's CellEndEdit started this whole SetInstruments call in the first
            // place, which WinForms rejects with "reentrant call to SetCurrentCellAddressCore".
            // SetInstruments fires one settled CurrentInstrumentChanged itself once the rebuild is
            // over (see its own tail), so listeners still hear about the real, final selection -
            // they just don't get spammed with every intermediate one this rebuild passes through
            // on the way there.
            if (_suppressEvents)
            {
                return;
            }

            _header.SetState(_instruments.Count, SelectedInstrument is not null);
            CurrentInstrumentChanged?.Invoke(SelectedInstrument);

            // Focused is the test for "the user just did this by clicking or arrowing in this
            // grid" - SetInstruments' own restore-selection step and SelectInstrumentNumber both
            // run while this grid does not have focus (the tracker grid, or nothing, does), so
            // neither one steals focus away from wherever the user actually is.
            if (_grid.Focused)
            {
                InstrumentSelected?.Invoke();
            }
        };
        _grid.CellEndEdit += Grid_CellEndEdit;

        _addItem.Click += (_, _) => AddRequested?.Invoke();
        _copyItem.Click += (_, _) => { if (SelectedInstrument is { } i) CopyRequested?.Invoke(i); };
        _deleteItem.Click += (_, _) => { if (SelectedInstrument is { } i) DeleteRequested?.Invoke(i); };

        // Docked children lay out in the *reverse* of Controls.Add order (see WinForms' own
        // Control.Controls docs) - header claims the top strip first, then the grid claims the
        // slice below that, so this list has to read bottom-to-top of the on-screen result:
        // grid, header.
        Controls.Add(_grid);
        Controls.Add(_header);
    }

    private void RequestAdd()
    {
        if (_instruments.Count < SongValidator.MaxInstrumentNumber)
            BeginInvoke(() => AddRequested?.Invoke());
    }

    /// <summary>Selects <paramref name="number"/> and opens its name for typing - what follows
    /// adding one, so it can be named straight away.</summary>
    public void BeginRename(int number)
    {
        if (number is < 1 or > SongValidator.MaxInstrumentNumber || InstrumentByNumber(number) is null)
            return;

        _grid.CurrentCell = _grid.Rows[number - 1].Cells["Name"];
        _grid.Focus();
        _grid.BeginEdit(selectAll: true);
    }

    public Instrument? SelectedInstrument =>
        _grid.CurrentRow is { } row ? InstrumentAtRow(row.Index) : null;

    /// <summary>This control's own natural height (header + the 16-row grid; Add/Delete/Edit are
    /// a context menu now, not a row of their own - see the constructor) - for a parent that
    /// docks it Top rather than Fill, which needs an explicit Height to dock against since
    /// nothing above computes one for it.</summary>
    public int PreferredHeight => _header.Height + _grid.Height;

    /// <summary>Always exactly SongValidator.MaxInstrumentNumber rows, one per slot - not one
    /// per instrument that happens to exist. A song can never hold more instruments than that
    /// (a pattern row names one with a single $C0..$CF byte), so showing every slot up front,
    /// occupied or not, is what tells you at a glance how much room is actually left rather than
    /// having to subtract the visible count from 16 yourself. Row index and slot number always
    /// agree (row i is slot i+1), which is what lets every other method here work by index
    /// instead of searching.</summary>
    public void SetInstruments(IReadOnlyList<Instrument> instruments)
    {
        // Rebuilding the rows loses CurrentRow - DataGridView has nothing to carry a selection
        // across Rows.Clear() by identity, only by row index, and the index of "the instrument
        // I picked" isn't stable across an edit that can renumber or reorder the list. This ran
        // on every single instrument edit (MainForm's Saved handler calls it after every commit)
        // as well as on every StructureChanged, so it reset which instrument Jam mode played back
        // to the song's first instrument far more often than any click the user made to change
        // it, which read as the selection simply not working.
        int? selectedNumber = SelectedInstrument?.Number;

        _instruments = [.. instruments.OrderBy(i => i.Number)];

        _suppressEvents = true;
        _grid.Rows.Clear();
        _newInstrumentRow = -1;
        for (int number = 1; number <= SongValidator.MaxInstrumentNumber; number++)
        {
            // The "#" cell itself is blank for an empty slot too, not just Name/Prog. - so the
            // numbers actually printed are a direct count of instruments in use at a glance,
            // rather than always running 01..16 regardless of how many are real (per Magnar's
            // own ask). The slot still occupies a row either way; only its own number label
            // is withheld.
            var instrument = InstrumentByNumber(number);
            bool offerNew = instrument is null && _newInstrumentRow < 0;
            int row = instrument is { } inst
                ? _grid.Rows.Add(number.ToString("D2"), inst.Name.Length > 0 ? inst.Name : UnnamedPlaceholder, DescribePrograms(inst))
                : _grid.Rows.Add(string.Empty, offerNew ? NewInstrumentText : string.Empty, string.Empty);

            ApplySlotStyle(_grid.Rows[row], occupied: instrument is not null);
            if (offerNew)
            {
                _newInstrumentRow = row;
                _grid.Rows[row].Cells["Name"].Style.ForeColor = EditorPalette.Title;
            }
        }

        if (selectedNumber is { } number2)
        {
            SelectRow(number2);
        }

        _suppressEvents = false;
        _header.SetState(_instruments.Count, SelectedInstrument is not null);

        // One explicit, settled notification instead of however many interim ones Rows.Clear()/
        // Rows.Add()/SelectRow happened to fire above (all suppressed - see SelectionChanged's
        // own comment on why those are spurious and reentrant-unsafe to act on). This one still
        // needs to fire even when the restored selection is the exact instrument that was
        // already loaded - LoadInstrument's own ReferenceEquals guard turns that into a safe
        // no-op - because it also has to cover the case where the previously-selected instrument
        // no longer exists (deleted): SelectedInstrument comes back null here, and downstream
        // listeners need to hear about that too, not just about an actual instrument-to-
        // instrument change.
        CurrentInstrumentChanged?.Invoke(SelectedInstrument);
    }

    /// <summary>Selects the given instrument without requiring a click - the tracker grid's own
    /// +/- shortcut steps through instruments this way while staying focused on the pattern.
    /// Does nothing if the number is out of range.</summary>
    public void SelectInstrumentNumber(int number) => SelectRow(number);

    private void SelectRow(int number)
    {
        if (number is >= 1 and <= SongValidator.MaxInstrumentNumber)
        {
            _grid.CurrentCell = _grid.Rows[number - 1].Cells[0];

            // The grid raises SelectionChanged while CurrentRow is still the old row, so the
            // editor below was handed the instrument being left and fell one selection behind -
            // an instrument added from the sound bank showed the one picked before it. Told again
            // here, now the row has moved; a repeat of the same instrument is a no-op there.
            if (!_suppressEvents)
            {
                _header.SetState(_instruments.Count, SelectedInstrument is not null);
                CurrentInstrumentChanged?.Invoke(SelectedInstrument);
            }
        }
    }

    private Instrument? InstrumentByNumber(int number) =>
        _instruments.FirstOrDefault(i => i.Number == number);

    /// <summary>Row index and slot number always agree (see <see cref="SetInstruments"/>), so
    /// this is just the one place that arithmetic lives.</summary>
    private Instrument? InstrumentAtRow(int rowIndex) =>
        rowIndex >= 0 && rowIndex < _grid.Rows.Count ? InstrumentByNumber(rowIndex + 1) : null;

    /// <summary>An empty slot's Name cell is read-only (there is no instrument to rename) and
    /// dimmer than an occupied one's, so "not yet created" reads differently at a glance from
    /// UnnamedPlaceholder's "created, but nothing typed over Flute yet".</summary>
    private static void ApplySlotStyle(DataGridViewRow row, bool occupied)
    {
        row.Cells["Name"].ReadOnly = !occupied;
        row.DefaultCellStyle.ForeColor = occupied ? EditorPalette.Body : EditorPalette.Subtitle;
    }

    private static string DescribePrograms(Instrument instrument)
    {
        var parts = new List<string>(3);
        if (instrument.Arpeggio.Count > 0)
            parts.Add($"ARP{instrument.Arpeggio.Count}");
        if (instrument.Filter.Count > 0)
            parts.Add($"FLT{instrument.Filter.Count}");
        if (!instrument.HasDefaultPulse)
            parts.Add("PW");

        return parts.Count > 0 ? string.Join(" ", parts) : "<Edit>";
    }

    private void Grid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_suppressEvents || InstrumentAtRow(e.RowIndex) is not { } instrument)
            return;

        var row = _grid.Rows[e.RowIndex];

        // Typing "Flute" over the placeholder is indistinguishable from actually wanting that
        // name - and a flute is a perfectly good instrument name - so this commits verbatim
        // rather than treating the default as a reserved sentinel for "still unnamed".
        instrument.Name = (row.Cells["Name"].Value?.ToString() ?? "").Trim();

        InstrumentEdited?.Invoke(instrument);
    }
}
