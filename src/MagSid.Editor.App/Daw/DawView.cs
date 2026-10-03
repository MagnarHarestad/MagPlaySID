using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The DAW view: an instrument list down the side and one of the DAW's own editors filling the rest.
///
/// It edits the same <see cref="Song"/> the tracker does - no conversion, no second format, nothing
/// that can fall out of step. That is the same rule that keeps the C# player and the assembly player
/// honest, and it is the reason switching views can never lose or change anything.
///
/// Two tabs. ARRANGE is the three channel lanes with the piano roll beneath them, showing whichever
/// pattern the selected clip plays - so picking a clip and editing its notes is one view, not two.
/// INSTRUMENT is the instrument editor with the instrument list beside it.
/// </summary>
internal sealed class DawView : Panel
{
    private readonly ArrangeView _arrange = new();
    private readonly PianoRollView _pianoRoll = new();
    private readonly Panel _arrangePage = new();
    private readonly Splitter _arrangeSplitter = new();
    private readonly Panel _arrangeTop = new();
    private readonly Label _octaveLabel = new();
    private readonly ToggleButton _autoScroll = new("AUTOSCROLL") { Checked = true, Accent = DawPalette.Oscillator };
    private readonly DawInstrumentView _instrument = new();
    private readonly Panel _placeholder = new();
    private readonly FlowLayoutPanel _tabs = new();
    private readonly ListBox _instrumentList = new();

    private readonly List<TabButton> _tabButtons = [];

    private Song? _song;
    private DawTab _current = DawTab.Arrange;

    public DawView()
    {
        BackColor = DawPalette.Background;
        BuildLayout();
    }

    private enum DawTab
    {
        Arrange,
        Instrument,
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

    /// <summary>Gate off for a note the roll auditioned - the mouse came up.</summary>
    public event Action<MagSid.Core.Model.NoteName>? NotePreviewReleased;

    /// <summary>Add, copy or delete an instrument. Handled by the host, which owns the session and
    /// its undo history - the DAW never touches the instrument collection itself, so both views
    /// always add instruments the same way.</summary>
    public event Action? InstrumentAddRequested;

    public event Action<Instrument>? InstrumentCopyRequested;

    public event Action<Instrument>? InstrumentDeleteRequested;

    public void LoadSong(Song song)
    {
        _song = song;

        // The roll first: loading the arrangement can announce its selection, and the roll has to
        // be holding the new Song by then or it would look the pattern up in the old one.
        _pianoRoll.LoadSong(song);
        _arrange.LoadSong(song);
        RefreshInstrumentList();
    }

    /// <summary>Every knob the DAW shows, for MIDI learn.</summary>
    public IEnumerable<KnobControl> Knobs() => _instrument.Knobs();

    /// <summary>A key was pressed on a MIDI keyboard. Writes into the piano roll when that is the
    /// view on screen, and reports whether it did - the caller sounds the note either way, because
    /// hearing what you played is not conditional on it being recorded.</summary>
    /// <summary>The octave the computer keyboard's lower key row plays in; the upper row is one
    /// above it. Its own, not the tracker's: the two views are looked at one at a time, and a
    /// shared octave would move under you while you were not watching.</summary>
    public int Octave { get; private set; } = 4;

    public void StepOctave(bool up)
    {
        int next = PianoKeyMap.StepOctave(Octave, up);
        if (next == Octave)
        {
            return;
        }

        Octave = next;
        _octaveLabel.Text = $"OCT {Octave}";
    }

    public bool StepInput(MagSid.Core.Model.NoteName note) =>
        _pianoRoll.Visible && _pianoRoll.StepInput(note);

    /// <summary>Lights the roll's own key for a note being held, wherever that note came from.
    /// Harmless while the DAW is hidden - the roll simply repaints into nothing.</summary>
    public void SetNoteSounding(MagSid.Core.Model.NoteName note, bool sounding) =>
        _pianoRoll.SetNoteSounding(note, sounding);

    public void ClearSoundingNotes() => _pianoRoll.ClearSoundingNotes();

    /// <summary>Which order entry each channel is playing. Three numbers, because the channels
    /// genuinely are in three different places - see ArrangeView.</summary>
    public void SetPlayheads(int channel1, int channel2, int channel3)
    {
        _arrange.SetPlayheads(channel1, channel2, channel3);

        // Autoscroll: the selection follows the selected channel's playhead, so the roll below
        // turns to each pattern as it starts playing.
        if (_autoScroll.Checked)
            _arrange.FollowPlayhead();
    }

    /// <summary>Where the roll's own pattern is being played. The caller passes which pattern each
    /// channel is in and the roll takes the row from one that matches - the channel of the selected
    /// clip first, since that is the one the roll was opened from, then any other. Anything else
    /// would draw a playhead from a channel that is somewhere else entirely.</summary>
    public void SetPlayRow(int patternChannel1, int row1, int patternChannel2, int row2, int patternChannel3, int row3)
    {
        int showing = _pianoRoll.Pattern?.Number ?? -1;
        int[] patterns = [patternChannel1, patternChannel2, patternChannel3];
        int[] rows = [row1, row2, row3];

        int preferred = _arrange.Selection?.Channel ?? 0;
        int row = patterns[preferred] == showing ? rows[preferred]
            : patterns[0] == showing ? rows[0]
            : patterns[1] == showing ? rows[1]
            : patterns[2] == showing ? rows[2] : -1;

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
                 })
        {
            var button = new TabButton(label) { Tag = tab, Enabled = ready };
            button.Click += (_, _) => Show(tab);
            _tabButtons.Add(button);
            _tabs.Controls.Add(button);
        }

        _autoScroll.Size = new Size(96, 26);
        _autoScroll.BackColor = DawPalette.PanelHeader;
        _autoScroll.Margin = new Padding(24, 0, 0, 0);
        _autoScroll.CheckedChanged += on => _pianoRoll.AutoScroll = on;
        _tabs.Controls.Add(_autoScroll);

        // The octave the typing keys play in, where it can be seen. A keyboard that plays two
        // octaves off and says nothing about it reads as a tuning fault.
        _octaveLabel.Text = $"OCT {Octave}";
        _octaveLabel.Font = new Font("Consolas", 8.5f, FontStyle.Bold);
        _octaveLabel.ForeColor = DawPalette.TextDim;
        _octaveLabel.AutoSize = false;
        _octaveLabel.Size = new Size(70, 26);
        _octaveLabel.TextAlign = ContentAlignment.MiddleCenter;
        _octaveLabel.Margin = new Padding(20, 3, 0, 0);
        _tabs.Controls.Add(_octaveLabel);

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

        // Clicking an instrument while a note is selected in the roll re-voices that note. A mouse
        // click only - SelectedIndexChanged also fires when the retro view syncs its selection or
        // the list is refreshed, and neither should touch the pattern.
        _instrumentList.MouseClick += (_, e) =>
        {
            if (_arrangePage.Visible && _instrumentList.IndexFromPoint(e.Location) >= 0
                && SelectedInstrument is { } instrument)
                _pianoRoll.SetSelectedNoteInstrument(instrument.Number);
        };

        _arrange.Dock = DockStyle.Fill;
        _arrange.OrderEdited += () => OrderEdited?.Invoke();
        // The roll below always shows the pattern of the selected clip - selecting is the whole
        // gesture, no double-click and no tab switch.
        _arrange.SelectedPatternChanged += (channel, patternNumber) =>
            _pianoRoll.ShowPattern(patternNumber, channel);

        _arrangeSplitter.Dock = DockStyle.Top;
        _arrangeSplitter.Height = 5;
        _arrangeSplitter.BackColor = DawPalette.Border;
        _arrangeSplitter.MinSize = 120;
        _arrangeSplitter.MinExtra = 160;

        _pianoRoll.Dock = DockStyle.Fill;
        _pianoRoll.Edited += () => PatternEdited?.Invoke();
        _pianoRoll.NotePreviewRequested += (note, instrument) => NotePreviewRequested?.Invoke(note, instrument);
        _pianoRoll.NotePreviewReleased += note => NotePreviewReleased?.Invoke(note);

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

        // Docking runs in reverse z-order: the fill goes in first so the top-docked lanes and the
        // splitter under them claim their strips before it takes what is left.
        _arrangePage.Dock = DockStyle.Fill;
        _arrangePage.BackColor = DawPalette.Background;
        _arrangeTop.Dock = DockStyle.Top;

        // Never past the bottom of a full instrument list. The strip beside it holds 16 and no
        // more, and arrange lanes hanging below the last instrument are height the piano roll
        // could have had. ArrangeView lays its lanes out to whatever height it ends up with.
        ApplyArrangeHeight();
        _instrumentList.HandleCreated += (_, _) => ApplyArrangeHeight();
        _arrangeTop.BackColor = DawPalette.Background;
        _arrangeTop.Controls.Add(_arrange);

        _arrangePage.Controls.Add(_pianoRoll);
        _arrangePage.Controls.Add(_arrangeSplitter);
        _arrangePage.Controls.Add(_arrangeTop);

        Controls.Add(_arrangePage);
        Controls.Add(_instrument);
        Controls.Add(_placeholder);
        Controls.Add(_instrumentList);
        Controls.Add(_tabs);

        Show(DawTab.Arrange);
    }

    /// <summary>Sixteen rows of the instrument list - the song format's own limit, so the list can
    /// never grow past it. Read off the list itself rather than assumed: ItemHeight follows the
    /// font, and a ListBox quietly overrides what it is set to.</summary>
    private int InstrumentStripHeight => 16 * _instrumentList.ItemHeight;

    /// <summary>Applied twice: once while building, and again when the list gets its handle.
    /// Until then ItemHeight still reports whatever it was assigned rather than what the font
    /// actually gives, and the lanes would keep a height measured against the wrong row.</summary>
    private void ApplyArrangeHeight() =>
        _arrangeTop.Height = Math.Min(ArrangeView.PreferredHeight, InstrumentStripHeight);

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
        _arrangePage.Visible = tab == DawTab.Arrange;
        _instrument.Visible = tab == DawTab.Instrument;
        _placeholder.Visible = false;

        // One list, two homes: full height beside the instrument editor, and beside the channel
        // lanes on ARRANGE - where picking one sets the instrument the roll draws with, and
        // re-voices the selected note. Moved rather than duplicated so the selection is one thing.
        Control home = tab == DawTab.Arrange ? _arrangeTop : this;
        if (_instrumentList.Parent != home)
        {
            _instrumentList.Parent?.Controls.Remove(_instrumentList);
            home.Controls.Add(_instrumentList);

            // Docking runs from the highest index down: the list must dock before the fill beside
            // it, and on the root after the tab strip, or it would take the tabs' full height.
            home.Controls.SetChildIndex(_instrumentList, home.Controls.Count - 1);
            if (home == this)
                Controls.SetChildIndex(_tabs, Controls.Count - 1);
        }

        // Autoscroll steers the lanes and the roll, which only the arrangement shows.
        _autoScroll.Visible = tab == DawTab.Arrange;

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
