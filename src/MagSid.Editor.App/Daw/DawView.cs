using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The DAW view: an instrument list down the side and one of the DAW's own editors filling the rest.
///
/// It edits the same <see cref="Song"/> the tracker does - no conversion, no second format, nothing
/// that can fall out of step. That is the same rule that keeps the C# player and the assembly player
/// honest, and it is the reason switching views can never lose or change anything.
///
/// Only the instrument editor is built so far. The arrangement and the piano roll get their own tabs
/// beside it; until then the tabs say so rather than showing an empty panel.
/// </summary>
internal sealed class DawView : Panel
{
    private readonly ArrangeView _arrange = new();
    private readonly PianoRollView _pianoRoll = new();
    private readonly DawInstrumentView _instrument = new();
    private readonly Panel _placeholder = new();
    private readonly FlowLayoutPanel _tabs = new();
    private readonly ListBox _instrumentList = new();

    private readonly List<TabButton> _tabButtons = [];

    private Song? _song;
    private DawTab _current = DawTab.Instrument;

    public DawView()
    {
        BackColor = DawPalette.Background;
        BuildLayout();
    }

    private enum DawTab
    {
        Arrange,
        Instrument,
        PianoRoll,
    }

    /// <summary>Raised when an edit here changes the song, so the host can mark it dirty and
    /// refresh whatever else is showing the same data.</summary>
    public event Action<Instrument>? InstrumentEdited;

    /// <summary>The song order changed here. The tracker is showing the same list in its own
    /// column, so it has to be told.</summary>
    public event Action? OrderEdited;

    /// <summary>A note changed in the piano roll.</summary>
    public event Action? PatternEdited;

    /// <summary>Sound a note placed in the roll.</summary>
    public event Action<MagSid.Core.Model.NoteName, int>? NotePreviewRequested;

    /// <summary>Add, copy or delete an instrument. Handled by the host, which owns the session and
    /// its undo history - the DAW never touches the instrument collection itself, so both views
    /// always add instruments the same way.</summary>
    public event Action? InstrumentAddRequested;

    public event Action<Instrument>? InstrumentCopyRequested;

    public event Action<Instrument>? InstrumentDeleteRequested;

    public void LoadSong(Song song)
    {
        _song = song;
        _arrange.LoadSong(song);
        _pianoRoll.LoadSong(song);
        RefreshInstrumentList();
    }

    /// <summary>Which order entry each channel is playing. Three numbers, because the channels
    /// genuinely are in three different places - see ArrangeView.</summary>
    public void SetPlayheads(int channel1, int channel2, int channel3) =>
        _arrange.SetPlayheads(channel1, channel2, channel3);

    /// <summary>Where the roll's own pattern is being played. Only one channel can be playing the
    /// pattern on screen, so the caller passes which pattern each channel is in and the roll takes
    /// the row from whichever matches - anything else would draw a playhead from a channel that is
    /// somewhere else entirely.</summary>
    public void SetPlayRow(int patternChannel1, int row1, int patternChannel2, int row2, int patternChannel3, int row3)
    {
        int showing = _pianoRoll.Pattern?.Number ?? -1;
        int row =
            patternChannel1 == showing ? row1 :
            patternChannel2 == showing ? row2 :
            patternChannel3 == showing ? row3 : -1;

        _pianoRoll.SetPlayRow(row);
    }

    /// <summary>Which step of the selected instrument's programs is sounding. Same idea as the
    /// retro view's tint: a step nothing ever reaches is a step worth deleting.</summary>
    public void SetProgramPlayheads(int wavetableStep, int arpeggioStep, int filterStep) =>
        _instrument.SetPlayheads(wavetableStep, arpeggioStep, filterStep);

    public void ClearPlayback()
    {
        _arrange.SetPlayheads(-1, -1, -1);
        _pianoRoll.SetPlayRow(-1);
        _instrument.SetPlayheads(-1, -1, -1);
    }

    /// <summary>Follows the retro view's own selection, so switching views lands on the instrument
    /// that was already being worked on rather than resetting to the first.</summary>
    public void SelectInstrument(int number)
    {
        if (_song is null)
            return;

        int index = _song.Instruments.FindIndex(i => i.Number == number);
        if (index >= 0 && index != _instrumentList.SelectedIndex)
            _instrumentList.SelectedIndex = index;
    }

    private Instrument? SelectedInstrument =>
        _song is { } song && _instrumentList.SelectedIndex >= 0
        && _instrumentList.SelectedIndex < song.Instruments.Count
            ? song.Instruments[_instrumentList.SelectedIndex]
            : null;

    public int? SelectedInstrumentNumber =>
        _instrumentList.SelectedIndex >= 0 && _song is { } song
        && _instrumentList.SelectedIndex < song.Instruments.Count
            ? song.Instruments[_instrumentList.SelectedIndex].Number
            : null;

    private void RefreshInstrumentList()
    {
        if (_song is null)
            return;

        int previous = _instrumentList.SelectedIndex;

        _instrumentList.BeginUpdate();
        _instrumentList.Items.Clear();
        foreach (var instrument in _song.Instruments)
        {
            _instrumentList.Items.Add(string.IsNullOrWhiteSpace(instrument.Name)
                ? $"{instrument.Number:00}"
                : $"{instrument.Number:00}   {instrument.Name}");
        }

        _instrumentList.EndUpdate();

        if (_instrumentList.Items.Count > 0)
            _instrumentList.SelectedIndex = Math.Clamp(previous, 0, _instrumentList.Items.Count - 1);
    }

    private void BuildLayout()
    {
        _tabs.Dock = DockStyle.Top;
        _tabs.Height = 36;
        _tabs.BackColor = DawPalette.PanelHeader;
        _tabs.Padding = new Padding(10, 5, 0, 0);
        _tabs.WrapContents = false;

        foreach (var (tab, label, ready) in new[]
                 {
                     (DawTab.Arrange, "ARRANGE", true),
                     (DawTab.Instrument, "INSTRUMENT", true),
                     (DawTab.PianoRoll, "PIANO ROLL", true),
                 })
        {
            var button = new TabButton(label) { Tag = tab, Enabled = ready };
            button.Click += (_, _) => Show(tab);
            _tabButtons.Add(button);
            _tabs.Controls.Add(button);
        }

        _instrumentList.Dock = DockStyle.Left;
        _instrumentList.Width = 186;
        _instrumentList.BackColor = Color.FromArgb(0x12, 0x17, 0x22);
        _instrumentList.ForeColor = DawPalette.Text;
        _instrumentList.BorderStyle = BorderStyle.None;
        _instrumentList.Font = new Font("Consolas", 9f);
        _instrumentList.IntegralHeight = false;
        _instrumentList.ItemHeight = 22;
        _instrumentList.SelectedIndexChanged += (_, _) => ShowSelectedInstrument();

        var menu = new ContextMenuStrip();
        var copyItem = new ToolStripMenuItem("&Copy", null, (_, _) =>
        {
            if (SelectedInstrument is { } instrument)
                InstrumentCopyRequested?.Invoke(instrument);
        });
        var deleteItem = new ToolStripMenuItem("&Delete", null, (_, _) =>
        {
            if (SelectedInstrument is { } instrument)
                InstrumentDeleteRequested?.Invoke(instrument);
        });
        menu.Items.Add(new ToolStripMenuItem("&Add", null, (_, _) => InstrumentAddRequested?.Invoke()));
        menu.Items.Add(copyItem);
        menu.Items.Add(deleteItem);
        menu.Opening += (_, _) => copyItem.Enabled = deleteItem.Enabled = SelectedInstrument is not null;
        DarkTheme.Apply(menu);
        _instrumentList.ContextMenuStrip = menu;

        _arrange.Dock = DockStyle.Fill;
        _arrange.OrderEdited += () => OrderEdited?.Invoke();
        // Double-clicking a clip opens that pattern in the roll and switches to it, which is the
        // one gesture that ties the two halves of the DAW together.
        _arrange.PatternActivated += (_, patternNumber) =>
        {
            _pianoRoll.ShowPattern(patternNumber);
            Show(DawTab.PianoRoll);
        };

        _pianoRoll.Dock = DockStyle.Fill;
        _pianoRoll.Edited += () => PatternEdited?.Invoke();
        _pianoRoll.NotePreviewRequested += (note, instrument) => NotePreviewRequested?.Invoke(note, instrument);

        _instrument.Dock = DockStyle.Fill;
        _instrument.Edited += instrument =>
        {
            RefreshInstrumentList();
            InstrumentEdited?.Invoke(instrument);
        };

        _placeholder.Dock = DockStyle.Fill;
        _placeholder.BackColor = DawPalette.Background;
        _placeholder.Visible = false;
        _placeholder.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = DawPalette.TextFaint,
            Font = new Font("Segoe UI", 10f),
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Not built yet — use RETRO for this.",
        });

        Controls.Add(_pianoRoll);
        Controls.Add(_arrange);
        Controls.Add(_instrument);
        Controls.Add(_placeholder);
        Controls.Add(_instrumentList);
        Controls.Add(_tabs);

        Show(DawTab.Instrument);
    }

    private void ShowSelectedInstrument()
    {
        if (_song is null || _instrumentList.SelectedIndex < 0
            || _instrumentList.SelectedIndex >= _song.Instruments.Count)
        {
            _instrument.LoadInstrument(null);
            return;
        }

        var selected = _song.Instruments[_instrumentList.SelectedIndex];
        _instrument.LoadInstrument(selected);

        // A note drawn in the roll gets whatever instrument is selected here, the same way the
        // tracker's own entry does.
        _pianoRoll.SetDefaultInstrument(selected.Number);
    }

    private void Show(DawTab tab)
    {
        _current = tab;
        _arrange.Visible = tab == DawTab.Arrange;
        _instrument.Visible = tab == DawTab.Instrument;
        _pianoRoll.Visible = tab == DawTab.PianoRoll;
        _placeholder.Visible = false;

        // The instrument list belongs to the instrument editor; the arrangement has its own
        // channel headers and would only be narrowed by it.
        _instrumentList.Visible = tab == DawTab.Instrument;

        foreach (var button in _tabButtons)
            button.Active = (DawTab)button.Tag! == tab;
    }

    /// <summary>One of the view tabs across the top of the DAW.</summary>
    private sealed class TabButton : Control
    {
        private bool _active;
        private bool _hovered;

        public TabButton(string caption)
        {
            Text = caption;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = DawPalette.PanelHeader;
            Size = new Size(108, 26);
            Cursor = Cursors.Hand;
        }

        public bool Active
        {
            get => _active;
            set
            {
                _active = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            var box = new Rectangle(0, 0, Width - 2, Height - 1);
            if (_active || (_hovered && Enabled))
            {
                using var fill = new SolidBrush(_active
                    ? Color.FromArgb(0x1D, 0x2A, 0x3E)
                    : Color.FromArgb(0x18, 0x20, 0x30));
                g.FillRectangle(fill, box);
            }

            using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var ink = new SolidBrush(!Enabled
                ? DawPalette.TextFaint
                : _active ? DawPalette.Oscillator : DawPalette.TextDim);
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Text, font, ink, box, centred);
        }
    }
}
