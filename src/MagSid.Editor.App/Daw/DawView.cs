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
    /// <summary>A fixed rule between the lanes and the roll. It was a splitter once, but the lanes
    /// are held to the instrument strip's 16 rows, so dragging it only broke that.</summary>
    private readonly Panel _arrangeSplitter = new();
    private readonly Panel _arrangeTop = new();
    private readonly Label _octaveLabel = new();
    private readonly RollToolbar _tools = new() { Dock = DockStyle.Top };

    /// <summary>The arrange's own tools, in the top row between the tabs and AUTOSCROLL.</summary>
    private readonly RollToolbar _arrangeTools = new(
    [
        (RollTool.Select, "Object Selection", "Click a clip to select it and show its pattern below. Drag it to another place in its lane, or into another lane. Shift+click marks a run of clips for Glue; drag a clip's end to change its pattern's length."),
        (RollTool.Erase, "Erase", "Click a clip to take it out of its lane. The pattern itself stays in the song."),
        (RollTool.Split, "Split", "Click inside a clip to cut it in two there - two patterns, one after the other."),
        (RollTool.Glue, "Glue", "Click a clip to join it to the next one - or click a run marked with Shift+click to join all of it - into one pattern."),
    ], switches: false) { Margin = new Padding(24, 1, 0, 0) };

    /// <summary>Sits over the list while a name is being typed. One box, moved to whichever parent
    /// the list currently has - it changes homes between the two tabs.</summary>
    /// <summary>The instrument the open editor belongs to, caught at the start: the list's
    /// selection can move while it is open, and the typed name belongs to the row it was opened
    /// on.</summary>
    private Instrument? _renaming;

    /// <summary>The row being renamed: lifted a shade off the list, with the instrument's colour
    /// underneath - the name is typed in place, in the list's own font, not in a box over it.</summary>
    private static readonly Color RenameBack = Color.FromArgb(0x1E, 0x27, 0x3A);

    private readonly TextBox _rename = new()
    {
        BorderStyle = BorderStyle.None,
        AutoSize = false,
        BackColor = RenameBack,
        ForeColor = DawPalette.Text,
        Font = new Font("Consolas", 9f),
        Dock = DockStyle.Fill,
    };

    /// <summary>Holds the editor over the name part of the row; the row itself still draws the
    /// number and the colour square, so they stay put while typing.</summary>
    private readonly Panel _renameHost = new() { Visible = false, BackColor = RenameBack };
    private readonly ToggleButton _autoScroll = new("AUTOSCROLL") { Checked = true, Accent = DawPalette.Oscillator };
    private readonly ToggleButton _cycle = new("CYCLE") { Accent = DawPalette.Oscillator };

    /// <summary>Notes are written only while this is open - between the punch-in and the
    /// punch-out. Closing it lets go of a held key there.</summary>
    private bool _recordWindowOpen = true;
    private readonly DawInstrumentView _instrument = new();
    private readonly Panel _placeholder = new();
    private readonly FlowLayoutPanel _tabs = new();
    private readonly ListBox _instrumentList = new();

    /// <summary>On the instrument page: a new instrument from the sound bank, or from a file - the
    /// same choice the Retro list's arrow offers, as a sheet of cards (see SoundBankPicker).</summary>
    private readonly TabButton _soundBank = new("SOUNDBANK");

    /// <summary>The last row of the strip when there is room for another instrument: clicking it
    /// adds one. Never selected - choosing it by the keyboard puts the selection back.</summary>
    private const string NewInstrumentText = "+ New instrument";
    private int _lastInstrumentIndex = -1;

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

    /// <summary>A channel name in the arrange lanes was clicked. The host flips the tracker
    /// grid's own mute, which is where that state lives.</summary>
    public event Action<int>? MuteToggleRequested;

    /// <summary>Wired by the host to the tracker grid, so a mute set in either view shows in
    /// both.</summary>
    public Func<int, bool>? IsChannelMuted
    {
        get => _arrange.IsChannelMuted;
        set => _arrange.IsChannelMuted = value;
    }

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
    public event Action<MagSid.Core.Midi.GmBank.Sound>? InstrumentFromBankRequested;

    /// <summary>The sound bank's swap button: this sound onto that instrument, in place.</summary>
    public event Action<Instrument, MagSid.Core.Midi.GmBank.Sound>? InstrumentSoundChangeRequested;
    public event Action? InstrumentLoadRequested;

    /// <summary>A sound of the bank to be heard before it is taken, on the note given.</summary>
    public event Action<MagSid.Core.Model.Instrument, MagSid.Core.Model.NoteName>? SoundAuditionRequested;

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

    /// <summary>The chip playback runs on, for the waveforms drawn from reSID.</summary>
    public MagSid.Sid.ChipModel ChipModel
    {
        get => _instrument.ChipModel;
        set => _instrument.ChipModel = value;
    }

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

    /// <summary>The roll has the keyboard - it was the last thing clicked, the note grid or the
    /// beat bar. Notes are only written down then: a note played while working in the lanes or on
    /// an instrument is being listened to, not entered.</summary>
    public bool RollHasFocus => _pianoRoll.ContainsFocus;

    /// <summary>The roll has the keyboard and an LE selected: digits are its loop count.</summary>
    public bool RollTakesDigits => _pianoRoll.ContainsFocus && _pianoRoll.LoopEndSelected;

    /// <summary>The arrange lanes have the keyboard, and the digits typed there are pattern numbers
    /// rather than notes.</summary>
    public bool ArrangeHasFocus => _arrange.ContainsFocus;

    public bool StepInput(MagSid.Core.Model.NoteName note) =>
        _pianoRoll.Visible && _pianoRoll.StepInput(note);

    /// <summary>The roll's REC button: true to start a take, false to stop it. The host plays the
    /// song from <see cref="RecordingFrom"/>.</summary>
    public event Action<bool>? RecordToggled;

    public bool IsRecording => _pianoRoll.IsRecording;

    /// <summary>Loops drawn as they play in the piano roll and the arrange clips, later passes
    /// linked to the first - the same switch as Retro's.</summary>
    public bool UnrollLoops
    {
        get => _pianoRoll.UnrollLoops;
        set
        {
            _pianoRoll.UnrollLoops = value;
            _arrange.UnrollLoops = value;
        }
    }

    /// <summary>A short message for the status bar.</summary>
    public event Action<string>? Notice;

    /// <summary>The project cursor, as a clip and the ticks into it - null to play from the top.</summary>
    public (int Channel, int Index, int TicksInto)? PlayFrom => _arrange.PlayFrom;

    public void MarkPlayStart() => _arrange.MarkPlayStart();

    /// <summary>Cycle switched, or the locators moved - playback has to take the new range.</summary>
    public event Action? CycleChanged;

    public bool CycleOn
    {
        get => _cycle.Checked;
        set => _cycle.Checked = value;
    }

    public bool PreRollOn => _tools.PreRollOn;

    public bool ClickOn => _tools.ClickOn;

    public bool PunchOn => _tools.PunchOn;

    /// <summary>Whether the take is inside its punch window right now - the host knows the
    /// frame being heard. Closing it lets go of a held key at the row reached.</summary>
    public void SetRecordWindow(bool open)
    {
        if (_recordWindowOpen && !open)
            _pianoRoll.PunchOut();

        _recordWindowOpen = open;
    }

    /// <summary>The locators as places in the song - each a clip and the ticks into it, null for
    /// the very start - or null when there are none, or the range runs past every lane.</summary>
    public ((int Channel, int Index, int TicksInto)? Left, (int Channel, int Index, int TicksInto) Right)? LocatorRange
    {
        get
        {
            if (_arrange.Locators is not { } range || _arrange.LocationAt(range.Right) is not { } right)
                return null;

            return (_arrange.LocationAt(range.Left), right);
        }
    }

    public void ReturnToPlayStart() => _arrange.ReturnToPlayStart();

    public void ResetProjectCursor() => _arrange.ResetProjectCursor();

    /// <summary>Where each channel is in its order entry while the song plays - the arrange lanes'
    /// playheads, and the project cursor riding along.</summary>
    public void SetSongPositions(IReadOnlyList<(int Entry, double Ticks)?> channels) =>
        _arrange.SetLanePositions(channels, follow: _autoScroll.Checked);

    /// <summary>The instrument page's note-program REC is lit: keys held become its arpeggio.</summary>
    public bool IsRecordingChord => _instrument.IsRecordingChord;

    public void RecordChordNote(MagSid.Core.Model.NoteName note) => _instrument.RecordChordNote(note);

    public void RecordChordRelease(MagSid.Core.Model.NoteName note) => _instrument.RecordChordRelease(note);

    /// <summary>The clip a take records into - the selected one, whose place in the song the
    /// playback starts from so the other channels play what really sounds against it.</summary>
    public (int Channel, int Index)? RecordingFrom { get; private set; }

    public bool BeginRecording()
    {
        if (_arrange.Selection is not { } clip || _pianoRoll.Pattern is null)
            return false;

        RecordingFrom = clip;
        _pianoRoll.BeginRecording();
        _tools.Recording = true;
        return true;
    }

    public void EndRecording()
    {
        RecordingFrom = null;
        _pianoRoll.EndRecording();
        _tools.Recording = false;
    }

    public bool RecordNote(MagSid.Core.Model.NoteName note, int instrumentNumber) =>
        _pianoRoll.RecordNote(note, instrumentNumber);

    public void RecordRelease(MagSid.Core.Model.NoteName note) => _pianoRoll.RecordRelease(note);

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
        // Not during a take, which stays on its own pattern while the song moves on.
        if (_autoScroll.Checked && !IsRecording)
            _arrange.FollowPlayhead();
    }

    /// <summary>Where the roll's own pattern is being played. The caller passes which pattern each
    /// channel is in and the roll takes the row from one that matches - the channel of the selected
    /// clip first, since that is the one the roll was opened from, then any other. Anything else
    /// would draw a playhead from a channel that is somewhere else entirely.</summary>
    /// <summary>Each channel's pattern, the row in it being heard and how far through that row.
    /// The roll follows whichever channel is playing the pattern it shows - the selected clip's
    /// channel first, since that is the one being looked at.</summary>
    public void SetPlayRow(
        (int Pattern, int Row, double Fraction, double Ticks) channel1,
        (int Pattern, int Row, double Fraction, double Ticks) channel2,
        (int Pattern, int Row, double Fraction, double Ticks) channel3)
    {
        int showing = _pianoRoll.Pattern?.Number ?? -1;
        var channels = new[] { channel1, channel2, channel3 };

        // A take follows the clip's own channel only: another channel playing the same pattern
        // number is at a different moment of it.
        if (RecordingFrom is { } take)
        {
            var at = channels[take.Channel];
            if (_recordWindowOpen && at.Pattern == _pianoRoll.RecordPatternNumber)
                _pianoRoll.SetRecordRow(at.Row, at.Fraction, at.Ticks);
            else
                _pianoRoll.SetRecordRow(-1, 0);
        }

        int preferred = _arrange.Selection?.Channel ?? 0;
        foreach (int ch in new[] { preferred, 0, 1, 2 })
        {
            if (channels[ch].Pattern == showing)
            {
                _pianoRoll.SetPlayRow(channels[ch].Row, channels[ch].Fraction, channels[ch].Ticks);
                return;
            }
        }

        _pianoRoll.SetPlayRow(-1, 0);
    }

    /// <summary>Which step of the selected instrument's programs is sounding. Same idea as the
    /// retro view's tint: a step nothing ever reaches is a step worth deleting.</summary>
    public void SetProgramPlayheads(int wavetableStep, int arpeggioStep, int filterStep) =>
        _instrument.SetPlayheads(wavetableStep, arpeggioStep, filterStep);

    /// <summary>Where the selected instrument's envelope is in the song - (stage, level), or null
    /// while it is silent.</summary>
    public void SetEnvelopePlayhead((int Stage, int Level)? at) => _instrument.SetEnvelopePlayhead(at);

    /// <summary>Where the selected instrument's filter program is in the song - (frame, cutoff),
    /// or null while the filter is not its own.</summary>
    public void SetCutoffPlayhead((int Frame, int Cutoff)? at) => _instrument.SetCutoffPlayhead(at);

    public void ClearPlayback()
    {
        _instrument.SetEnvelopePlayhead(null);
        _instrument.SetCutoffPlayhead(null);
        _arrange.SetPlayheads(-1, -1, -1);
        _arrange.ClearLanePlayheads();
        _pianoRoll.SetPlayRow(-1, 0);
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
            _instrumentList.Items.Add($"{instrument.Number:00}   {InstrumentNames.Display(instrument)}");
        }

        if (_song.Instruments.Count < MagSid.Core.Validation.SongValidator.MaxInstrumentNumber)
            _instrumentList.Items.Add(NewInstrumentText);

        _instrumentList.EndUpdate();

        if (_song.Instruments.Count > 0)
            _instrumentList.SelectedIndex = Math.Clamp(previous, 0, _song.Instruments.Count - 1);
    }

    private bool IsNewInstrumentRow(int index) =>
        _song is { } song && index == song.Instruments.Count && index < _instrumentList.Items.Count;

    /// <summary>Drops the sound bank's cards under the SOUNDBANK button. Read each time it opens,
    /// so a file just saved into the bank folder is there.</summary>
    private void ShowSoundBank()
    {
        // The swap button puts a sound on the selected instrument - a new one is a plain Flute.
        var selected = _song?.Instruments.FirstOrDefault(i => i.Number == SelectedInstrumentNumber);
        string? target = selected is null ? null : $"{selected.Number:00} {InstrumentNames.Display(selected)}";
        var picker = new SoundBankPicker(MagSid.Core.Midi.GmBank.Load(EditorSettings.BankDirectory).All.ToList())
        {
            ChangeTarget = target,
            Hint = target is null
                ? "Click to hear  ·  double-click or + to add to the song"
                : $"Click to hear  ·  + adds it to the song  ·  \u21C4 puts it on {target}",
        };
        var host = new ToolStripControlHost(picker) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = picker.Size };
        var drop = new ToolStripDropDown { Padding = Padding.Empty, BackColor = DawPalette.Panel };
        drop.Items.Add(host);
        drop.Closed += (_, _) => BeginInvoke(drop.Dispose);

        picker.Auditioned += sound => SoundAuditionRequested?.Invoke(sound.Instrument,
            MagSid.Core.Model.NoteName.Parse(sound.Key is "Bass" or "SynthBass" or "KickBass" ? "C-2" : "C-4"));
        picker.Picked += sound =>
        {
            drop.Close();
            BeginInvoke(() => InstrumentFromBankRequested?.Invoke(sound));
        };
        picker.Changed += sound =>
        {
            drop.Close();
            if (selected is not null)
                BeginInvoke(() => InstrumentSoundChangeRequested?.Invoke(selected, sound));
        };
        picker.LoadRequested += () =>
        {
            drop.Close();
            BeginInvoke(() => InstrumentLoadRequested?.Invoke());
        };
        picker.FolderRequested += () =>
        {
            drop.Close();
            MagSid.Editor.App.Controls.InstrumentListHeader.OpenBankFolder();
        };

        drop.Show(_soundBank, new Point(0, _soundBank.Height + 2));
    }

    /// <summary>Opens the selected instrument's name for typing - what follows adding one.</summary>
    public void RenameSelectedInstrument() => BeginRename();

    /// <summary>"+ New instrument" and Add on the right-click menu: a new instrument is one to
    /// shape, so it opens on the instrument page - the tab first, since the strip moves with it
    /// and the name is opened for typing there. Posted: adding rebuilds the list, which is not
    /// done from inside its own click.</summary>
    private void AddOnInstrumentPage() => BeginInvoke(() =>
    {
        if (_current != DawTab.Instrument)
            Show(DawTab.Instrument);
        InstrumentAddRequested?.Invoke();
    });

    /// <summary>One row of the instrument list: the text exactly where the plain list put it, and
    /// the instrument's colour in the gap after the number.</summary>
    private void DrawInstrumentItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _instrumentList.Items.Count)
            return;

        bool renaming = _renaming is { } edited && _song is { } owner
            && e.Index < owner.Instruments.Count && owner.Instruments[e.Index] == edited;
        bool selected = (e.State & DrawItemState.Selected) != 0 && !renaming;
        if (renaming)
        {
            using var back = new SolidBrush(RenameBack);
            e.Graphics.FillRectangle(back, e.Bounds);
            using var underline = new SolidBrush(DawPalette.ForInstrument(_renaming!.Number));
            e.Graphics.FillRectangle(underline, e.Bounds.X, e.Bounds.Bottom - 1, e.Bounds.Width, 1);
        }

        var ink = selected ? SystemColors.HighlightText : _instrumentList.ForeColor;
        string text = _instrumentList.Items[e.Index]?.ToString() ?? string.Empty;
        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        if (IsNewInstrumentRow(e.Index))
            ink = DawPalette.Oscillator;
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? _instrumentList.Font, e.Bounds with { X = e.Bounds.X - 1 }, ink, flags);

        if (_song is not { } song || e.Index >= song.Instruments.Count)
            return;

        // Centred in the spaces between "01" and the name, measured in the list's own font.
        var font = e.Font ?? _instrumentList.Font;
        int numberEnd = TextRenderer.MeasureText(e.Graphics, "01", font, Size.Empty, flags).Width;
        int nameStart = TextRenderer.MeasureText(e.Graphics, "01   ", font, Size.Empty, flags).Width;
        int size = Math.Max(6, e.Bounds.Height - 6);
        var swatch = new Rectangle(
            e.Bounds.X + ((numberEnd + nameStart - size) / 2) - 2,
            e.Bounds.Y + ((e.Bounds.Height - size) / 2),
            size,
            size);

        using (var fill = new SolidBrush(DawPalette.ForInstrument(song.Instruments[e.Index].Number)))
            e.Graphics.FillRectangle(fill, swatch);
        using (var edge = new Pen(Color.FromArgb(90, 0, 0, 0)))
            e.Graphics.DrawRectangle(edge, swatch.X, swatch.Y, swatch.Width - 1, swatch.Height - 1);
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

        _soundBank.Margin = new Padding(24, 0, 0, 0);
        _soundBank.Click += (_, _) => ShowSoundBank();
        _tabs.Controls.Add(_soundBank);
        _arrangeTools.ToolChanged += tool => _arrange.Tool = tool;
        _tabs.Controls.Add(_arrangeTools);

        _autoScroll.Size = new Size(96, 26);
        _autoScroll.BackColor = DawPalette.PanelHeader;
        _autoScroll.Margin = new Padding(24, 0, 0, 0);
        _autoScroll.CheckedChanged += on => _pianoRoll.AutoScroll = on;
        _tabs.Controls.Add(_autoScroll);

        // Cycle loops the locators' range - set by dragging in the arrange ruler.
        _cycle.Size = new Size(64, 26);
        _cycle.BackColor = DawPalette.PanelHeader;
        _cycle.Margin = new Padding(6, 0, 0, 0);
        _cycle.CheckedChanged += on =>
        {
            _arrange.CycleOn = on;
            CycleChanged?.Invoke();
        };
        _arrange.LocatorsChanged += () => CycleChanged?.Invoke();
        _tabs.Controls.Add(_cycle);

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

        // Drawn by hand for the colour square between number and name - the colour the roll
        // gives that instrument's notes. Rows stay the GDI text height, what the list had
        // before it was owner-drawn - the arrange strip's height is measured from it.
        _instrumentList.DrawMode = DrawMode.OwnerDrawFixed;
        _instrumentList.ItemHeight = TextRenderer.MeasureText("01", _instrumentList.Font, Size.Empty, TextFormatFlags.NoPadding).Height;
        _instrumentList.DrawItem += DrawInstrumentItem;
        _renameHost.Controls.Add(_rename);
        _instrumentList.SelectedIndexChanged += (_, _) =>
        {
            if (IsNewInstrumentRow(_instrumentList.SelectedIndex))
            {
                _instrumentList.SelectedIndex = _song is { } song && _lastInstrumentIndex >= 0 && _lastInstrumentIndex < song.Instruments.Count
                    ? _lastInstrumentIndex
                    : -1;
                return;
            }

            _lastInstrumentIndex = _instrumentList.SelectedIndex;
            CommitRename(keep: true);
            ShowSelectedInstrument();
        };
        _instrumentList.MouseMove += (_, e) =>
            _instrumentList.Cursor = IsNewInstrumentRow(_instrumentList.IndexFromPoint(e.Location)) ? Cursors.Hand : Cursors.Default;

        // F2 or a double-click, the two gestures Explorer taught everyone. The retro view renames
        // in its own grid cell; this is the same edit, reached the same way a list allows.
        _instrumentList.DoubleClick += (_, _) => BeginRename();
        _instrumentList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F2)
                return;

            BeginRename();
            e.Handled = true;
        };

        _rename.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape))
                return;

            CommitRename(keep: e.KeyCode == Keys.Enter);
            _instrumentList.Focus();
            e.Handled = e.SuppressKeyPress = true;
        };

        // Clicking away keeps what was typed: a name half-entered and then lost to a stray click
        // is worse than one committed early, which is simply renamed again.
        _rename.LostFocus += (_, _) => CommitRename(keep: true);

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
        menu.Items.Add(new ToolStripMenuItem("&Add", null, (_, _) => AddOnInstrumentPage()));
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
            if (IsNewInstrumentRow(_instrumentList.IndexFromPoint(e.Location)))
            {
                AddOnInstrumentPage();
                return;
            }

            if (_arrangePage.Visible && _instrumentList.IndexFromPoint(e.Location) >= 0
                && SelectedInstrument is { } instrument)
                _pianoRoll.SetSelectedNoteInstrument(instrument.Number);
        };

        _arrange.Dock = DockStyle.Fill;
        _arrange.OrderEdited += () => OrderEdited?.Invoke();
        _arrange.Notice += message => Notice?.Invoke(message);
        _arrange.MuteToggled += channel => MuteToggleRequested?.Invoke(channel);

        // The roll may be showing the very pattern that just changed length, so it reloads before
        // the host hears about the edit.
        _arrange.PatternLengthChanged += () =>
        {
            _pianoRoll.ReloadPattern();
            PatternEdited?.Invoke();
        };
        _arrange.PatternLengthChanging += _pianoRoll.ReloadPattern;
        // The roll below always shows the pattern of the selected clip - selecting is the whole
        // gesture, no double-click and no tab switch.
        _arrange.SelectedPatternChanged += (channel, patternNumber) =>
            _pianoRoll.ShowPattern(patternNumber, channel);

        _arrangeSplitter.Dock = DockStyle.Top;
        _arrangeSplitter.Height = 5;
        _arrangeSplitter.BackColor = DawPalette.Border;

        _pianoRoll.Dock = DockStyle.Fill;
        _pianoRoll.Edited += () => PatternEdited?.Invoke();
        _tools.RecordToggled += on => RecordToggled?.Invoke(on);
        _pianoRoll.Notice += message => Notice?.Invoke(message);
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
        // rule under them claim their strips before it takes what is left.
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

        // The roll's tools sit directly above it, under the rule.
        _tools.ToolChanged += tool => _pianoRoll.Tool = tool;
        _pianoRoll.ToolRequested += tool => _tools.Tool = tool;
        _pianoRoll.Tool = _tools.Tool;

        _arrangePage.Controls.Add(_pianoRoll);
        _arrangePage.Controls.Add(_tools);
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

    /// <summary>Where the name starts in a row, as DrawInstrumentItem lays it out: one pixel left,
    /// the renderer's own padding, then the number and three spaces of the monospaced font.</summary>
    private int NameLeft()
    {
        var font = _instrumentList.Font;
        const TextFormatFlags tight = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int padding = (TextRenderer.MeasureText("01", font, Size.Empty, TextFormatFlags.NoPrefix).Width
                     - TextRenderer.MeasureText("01", font, Size.Empty, tight).Width) / 2;
        int column = TextRenderer.MeasureText("0000000000", font, Size.Empty, tight).Width / 10;
        return -1 + padding + 5 * column;
    }

    /// <summary>Opens the editor over the name in the selected row. The number is not part of
    /// the name and stays drawn by the list.</summary>
    private void BeginRename()
    {
        if (SelectedInstrument is not { } instrument
            || _instrumentList.SelectedIndex < 0
            || _instrumentList.Parent is not { } parent)
        {
            return;
        }

        var item = _instrumentList.GetItemRectangle(_instrumentList.SelectedIndex);
        if (_renameHost.Parent != parent)
        {
            _renameHost.Parent?.Controls.Remove(_renameHost);
            parent.Controls.Add(_renameHost);
        }

        // The edit control keeps a small margin of its own before the first character; the host
        // starts that much earlier, so the typed name sits exactly where the drawn one did.
        _rename.Text = instrument.Name;
        int margin = _rename.IsHandleCreated ? Math.Max(0, _rename.GetPositionFromCharIndex(0).X) : 1;
        int left = NameLeft() - margin;
        _renameHost.Bounds = new Rectangle(
            _instrumentList.Left + left,
            _instrumentList.Top + item.Top,
            Math.Max(40, _instrumentList.ClientSize.Width - left - 4),
            item.Height - 1);

        _renaming = instrument;
        _instrumentList.Invalidate(item);
        _renameHost.Visible = true;
        _renameHost.BringToFront();
        _rename.Focus();
        _rename.SelectAll();
    }

    /// <summary>Takes what was typed, or throws it away. Commits verbatim, the same as the retro
    /// grid does: a name that happens to match the placeholder is still a name someone chose.
    /// </summary>
    private void CommitRename(bool keep)
    {
        if (!_renameHost.Visible)
        {
            return;
        }

        var instrument = _renaming;
        _renaming = null;
        _renameHost.Visible = false;
        _instrumentList.Invalidate();

        if (!keep || instrument is null)
        {
            return;
        }

        string name = _rename.Text.Trim();
        if (name == instrument.Name)
        {
            return;
        }

        instrument.Name = name;
        RefreshInstrumentList();
        InstrumentEdited?.Invoke(instrument);
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
        _arrangePage.Visible = tab == DawTab.Arrange;
        _instrument.Visible = tab == DawTab.Instrument;
        _placeholder.Visible = false;

        // One list, two homes: beside the oscillator and vibrato on the instrument page, and beside
        // the channel lanes on ARRANGE - where picking one sets the instrument the roll draws
        // with, and re-voices the selected note. Either way what is below it - the program lanes,
        // the piano roll - runs the full width under it. Moved rather than duplicated so the
        // selection is one thing.
        Control home = tab == DawTab.Arrange ? _arrangeTop : _instrument.StripHost;

        // Beside the lanes it runs their height, which ApplyArrangeHeight already holds to 16
        // rows; on the instrument page it is sized to its 16 rows itself, so its background does
        // not run on past the last one.
        _instrumentList.Dock = tab == DawTab.Arrange ? DockStyle.Left : DockStyle.Top;
        if (tab != DawTab.Arrange)
            _instrumentList.Height = 16 * _instrumentList.ItemHeight;

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
        _arrangeTools.Visible = tab == DawTab.Arrange;
        _soundBank.Visible = tab == DawTab.Instrument;
        _cycle.Visible = tab == DawTab.Arrange;

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
