using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>
/// The song order as three lanes of clips, one lane per SID channel.
///
/// A clip's width is its pattern's length in rows, and each lane starts at the left, so the lanes
/// drift apart exactly as the song does. That is the whole reason this view earns its place: the
/// three channels have <em>independent</em> order lengths, and entry 4 on CH1 is not the same moment
/// as entry 4 on CH3. The tracker's order column puts them in a row and hides it; here it is the
/// first thing you see.
///
/// Editing goes through ChannelOrder's own methods, which keep the restart position aimed at the
/// entry it was aimed at before - the bookkeeping that makes inserting in the middle of a list safe.
/// </summary>
internal sealed class ArrangeView : Control
{
    private const int GutterWidth = 104;
    private const int RulerHeight = 24;
    private const int DefaultLaneHeight = 78;
    private const int MinLaneHeight = 40;
    private const int LaneGap = 4;
    private const int PixelsPerRow = 5;
    // The line of shortcut text that used to sit under the lanes is gone - the piano roll's tool
    // row took its place, and the keys it listed are in About. Kept as a named zero rather than
    // deleted so the height sums below still say what they are made of.
    private const int HintHeight = 0;
    private const int ScrollHeight = 12;

    // The RST block after a lane's last clip: where the channel's order runs out, and which entry
    // it goes back to.
    private const int RestartGap = 6;
    private const int RestartWidth = 46;

    /// <summary>Tall enough for the ruler, the three lanes, the hint and the scroll bar - and no
    /// taller, because the piano roll below wants every pixel it can get.</summary>
    public const int PreferredHeight =
        RulerHeight + (3 * (DefaultLaneHeight + LaneGap)) + HintHeight + ScrollHeight;

    /// <summary>The three lanes share whatever is left after the fixed furniture, so the view fits
    /// the height it is given instead of only one height. Below <see cref="MinLaneHeight"/> a lane
    /// has no room for its own name and counts, so it stops shrinking and clips instead.</summary>
    private int LaneHeight => Math.Max(
        MinLaneHeight,
        (Height - RulerHeight - HintHeight - ScrollHeight - (3 * LaneGap)) / 3);

    private readonly SlimScrollBar _scroll = new(Orientation.Horizontal)
    {
        Dock = DockStyle.Bottom,
        Height = ScrollHeight,
    };

    private Song? _song;
    private int _selectedChannel;
    private int _selectedIndex = -1;
    private (int Channel, int Pattern) _announced = (-1, -1);
    private (int Channel, int Index) _hovered = (-1, -1);
    private readonly int[] _playheads = [-1, -1, -1];

    /// <summary>
    /// The project cursor, in ticks along the lanes: where F2 plays from. It follows the song while
    /// it plays and stays where it stopped, so F2 carries on from there; a click in the ruler puts
    /// it anywhere, a right-click back at the start.
    /// </summary>
    private double _cursor;

    /// <summary>Where the cursor was when playback last started - a second stop goes back there.</summary>
    private double _playStart;

    /// <summary>The left and right locators, in ticks along the lanes - the range Cycle loops and
    /// a recording punches into. Set by dragging in the ruler; null until then.</summary>
    private (double Left, double Right)? _locators;

    /// <summary>A press in the ruler, until it is known whether it is a click or a drag.</summary>
    private int _rulerPressX = -1;
    private bool _rulerDragging;

    /// <summary>Cycle is on: the locators' range is shaded down the lanes too.</summary>
    public bool CycleOn
    {
        get => _cycleOn;
        set
        {
            _cycleOn = value;
            Invalidate();
        }
    }

    private bool _cycleOn;

    public (double Left, double Right)? Locators => _locators;

    /// <summary>The locators moved.</summary>
    public event Action? LocatorsChanged;

    /// <summary>Each lane's playhead, in ticks along it - NaN while nothing plays. Per lane, since
    /// a channel with a shorter order has wrapped round its RST while the others play on.</summary>
    private readonly double[] _lanePlayhead = [double.NaN, double.NaN, double.NaN];

    /// <summary>The selected channel's RST block is selected rather than a clip. _selectedIndex
    /// stays on the last clip meanwhile, so everything that reads it still finds a clip.</summary>
    private bool _onRestart;
    private int _hoveredRestart = -1;

    /// <summary>Opened over an RST block on a double-click, for typing the restart entry.</summary>
    private readonly TextBox _restartEntry = new()
    {
        Visible = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = DawPalette.Inset,
        ForeColor = DawPalette.Text,
        Font = new Font("Consolas", 10f, FontStyle.Bold),
        TextAlign = HorizontalAlignment.Center,
        MaxLength = 3,
    };

    private int _restartEntryChannel = -1;

    /// <summary>What a click on a clip does: select and drag it, erase it, split it in two, or
    /// glue it to what follows - the piano roll's icons, for clips.</summary>
    public RollTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            _dragFrom = null;
            _dropAt = null;
            _splitAt = null;
            SetGluePreview(null);
            Invalidate();
        }
    }

    private RollTool _tool = RollTool.Select;

    /// <summary>The other end of a run of clips marked with Shift+click in the selected lane - what
    /// Glue joins - or -1 when only the selected clip is marked.</summary>
    private int _markEnd = -1;

    /// <summary>A clip pressed with the selection tool, until it is known whether it is a click or
    /// a drag; then where the drag would put it - a lane and the place in its order.</summary>
    private (int Channel, int Index)? _dragFrom;
    private Point _dragPress;
    private bool _dragging;
    private (int Channel, int Index)? _dropAt;

    /// <summary>Where the split tool would cut, under the mouse: the x of the row edge.</summary>
    private (int Channel, int X)? _splitAt;

    /// <summary>The clips Glue would join if the mouse were clicked here - shown pulsing slowly,
    /// so the result is seen before it is made. Fits says whether they make one pattern.</summary>
    private (int Channel, int From, int To, bool Fits)? _gluePreview;

    /// <summary>Drives the preview's pulse; runs only while there is one.</summary>
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 40 };

    /// <summary>A short message for the status bar - mostly why an edit was not made.</summary>
    public event Action<string>? Notice;

    private bool IsMarked(int channel, int index) =>
        channel == _selectedChannel && !_onRestart && _selectedIndex >= 0
        && (index == _selectedIndex
            || (_markEnd >= 0 && index >= Math.Min(_selectedIndex, _markEnd) && index <= Math.Max(_selectedIndex, _markEnd)));

    public ArrangeView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        BackColor = DawPalette.Background;
        TabStop = true;

        _scroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_scroll);
        _pulse.Tick += (_, _) =>
        {
            if (_gluePreview is { } glue)
                Invalidate(new Rectangle(GutterWidth, LaneTop(glue.Channel), Width - GutterWidth, LaneHeight));
        };

        _restartEntry.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.Escape))
                return;

            EndRestartEntry(keep: e.KeyCode == Keys.Enter);
            e.Handled = e.SuppressKeyPress = true;
        };
        _restartEntry.LostFocus += (_, _) => EndRestartEntry(keep: true);
        Controls.Add(_restartEntry);
    }

    /// <summary>The selected clip now points at a different pattern (channel, pattern number) - the
    /// piano roll below the lanes follows it. Raised only on a real change, so moving between two
    /// clips of the same pattern on the same channel leaves the roll's scroll and selection alone.</summary>
    public event Action<int, int>? SelectedPatternChanged;

    /// <summary>The order changed. The host marks the song dirty and refreshes the tracker, which is
    /// showing the same list in its own column.</summary>
    public event Action? OrderEdited;

    /// <summary>
    /// Points the view at a song. Called again whenever the session replaces the Song object -
    /// undo restores a whole-song snapshot rather than reversing an action, so a view still holding
    /// the old one would be editing a song the editor has already thrown away.
    ///
    /// The selection is kept where it still exists, because a structural change elsewhere should not
    /// move what is selected here.
    /// </summary>
    public void LoadSong(Song song)
    {
        int channel = _selectedChannel;
        int index = _selectedIndex;

        _song = song;
        _markEnd = -1;
        _selectedChannel = Math.Clamp(channel, 0, Song.ChannelCount - 1);

        int count = song.ChannelOrders[_selectedChannel].Tokens.Count;
        _selectedIndex = count == 0 ? -1 : Math.Clamp(index < 0 ? 0 : index, 0, count - 1);

        UpdateScrollRange();
        Invalidate();
        AnnounceSelection();
    }

    /// <summary>The pattern under the selection, or -1.</summary>
    public int SelectedPatternNumber =>
        _song is { } song && _selectedIndex >= 0
        && _selectedIndex < song.ChannelOrders[_selectedChannel].Tokens.Count
        && song.ChannelOrders[_selectedChannel].Tokens[_selectedIndex] is PatternRefToken token
            ? token.PatternNumber
            : -1;

    private void AnnounceSelection()
    {
        var now = (_selectedChannel, SelectedPatternNumber);
        if (now == _announced || now.Item2 < 0)
            return;

        _announced = now;
        SelectedPatternChanged?.Invoke(now.Item1, now.Item2);
    }

    /// <summary>Which order entry each channel is playing, or -1. Three separate numbers, because
    /// the channels genuinely are in three different places.</summary>
    public void SetPlayheads(int channel1, int channel2, int channel3)
    {
        if (_playheads[0] == channel1 && _playheads[1] == channel2 && _playheads[2] == channel3)
            return;

        _playheads[0] = channel1;
        _playheads[1] = channel2;
        _playheads[2] = channel3;

        // Stopped: the next start is a clip to follow, even if it is the one followed last.
        for (int ch = 0; ch < Song.ChannelCount; ch++)
        {
            if (_playheads[ch] < 0)
                _followed[ch] = -1;
        }

        Invalidate();
    }

    /// <summary>Each channel's playhead as autoscroll last saw it, or -1.</summary>
    private readonly int[] _followed = [-1, -1, -1];

    /// <summary>Autoscroll: moves the selection to whatever the selected channel is playing, which
    /// takes the roll below along with it, and scrolls the lanes so that clip is in view.
    ///
    /// Only when the channel reaches a clip it has not been followed to yet - not on every tick -
    /// so a clip clicked during playback stays selected until the channel moves on, instead of
    /// being snatched back to the one playing before the click has shown anything.</summary>
    public void FollowPlayhead()
    {
        if (_song is not { } song)
            return;

        // Every channel's playhead is noted, not only the selected one's: a click on another lane
        // must not count as that lane having just moved.
        bool moved = _playheads[_selectedChannel] != _followed[_selectedChannel];
        for (int ch = 0; ch < Song.ChannelCount; ch++)
            _followed[ch] = _playheads[ch];

        int playing = _playheads[_selectedChannel];
        if (!moved || playing < 0 || playing >= song.ChannelOrders[_selectedChannel].Tokens.Count
            || (playing == _selectedIndex && !_onRestart))
            return;

        _selectedIndex = playing;
        _onRestart = false;
        ScrollIntoView(song.ChannelOrders[_selectedChannel], playing);
        Invalidate();
        AnnounceSelection();
    }

    private void ScrollIntoView(ChannelOrder order, int index)
    {
        if (!_scroll.Enabled)
            return;

        int start = OffsetOf(order, index);
        int end = OffsetOf(order, index + 1);
        int visible = Math.Max(1, Width - GutterWidth);
        int max = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);

        if (start < _scroll.Value || end > _scroll.Value + visible)
            _scroll.Value = Math.Clamp(start - 20, 0, max);
    }

    public (int Channel, int Index)? Selection =>
        _selectedIndex >= 0 ? (_selectedChannel, _selectedIndex) : null;

    /// <summary>
    /// A clip's width in ticks - the rows it plays, an LS/LE loop's section once per pass - so a
    /// lane's length is time and the three lanes line up the way the song plays. A loop with no
    /// count is played until the longest lane reaches its RST.
    /// </summary>
    private int RowsOf(int patternNumber)
    {
        if (_song?.Patterns.FirstOrDefault(p => p.Number == patternNumber) is not { } pattern)
            return 0;

        return Played(pattern).Count;
    }

    /// <summary>A pattern as it plays - the clip's width and, inside it, where a loop's later
    /// passes start.</summary>
    private IReadOnlyList<PatternLoops.UnrolledRow> Played(Pattern pattern)
    {
        bool endless = pattern.Rows.Any(r => r is LoopEndRow { RepeatCount: null });
        return PatternLoops.Unroll(pattern, endless ? PatternLoops.EndlessCap(_song!) : 0);
    }

    /// <summary>Draw a loop's passes inside its clip - on by default, the same switch as Retro's.
    /// The clip is as wide as it plays either way.</summary>
    public bool UnrollLoops
    {
        get => _unrollLoops;
        set
        {
            _unrollLoops = value;
            Invalidate();
        }
    }

    private bool _unrollLoops = true;

    /// <summary>A loop's later passes inside a clip: a violet wash from where each starts, and
    /// "↻2/4" at its top - the same rows again, linked to the first pass.</summary>
    private void DrawClipPasses(Graphics g, int patternNumber, Rectangle box)
    {
        if (!_unrollLoops || _song?.Patterns.FirstOrDefault(p => p.Number == patternNumber) is not { } pattern)
            return;

        var played = Played(pattern);
        var linked = Color.FromArgb(0x7F, 0x77, 0xDD);
        using var wash = new SolidBrush(Color.FromArgb(30, linked));
        using var edge = new Pen(Color.FromArgb(150, linked));
        using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
        using var ink = new SolidBrush(Color.FromArgb(220, 0xC8, 0xC2, 0xF4));

        for (int n = 0; n < played.Count; n++)
        {
            if (!played[n].Linked)
                continue;

            int x = box.X + (n * PixelsPerRow);
            g.FillRectangle(wash, x, box.Y + 1, PixelsPerRow, box.Height - 1);
            if (played[n].PassStart)
            {
                g.DrawLine(edge, x, box.Y + 1, x, box.Bottom - 1);
                string tag = played[n].Passes is > 0 and < 10 ? $"\u21BB{played[n].Pass}/{played[n].Passes}" : $"\u21BB{played[n].Pass}";
                g.DrawString(tag, font, ink, x + 2, box.Bottom - 13);
            }
        }
    }

    /// <summary>Where a lane's entry starts, in pixels from the lane's own beginning. Cumulative
    /// rows, so a channel of short patterns falls behind one of long patterns - which is what
    /// actually happens when the song plays.</summary>
    private int OffsetOf(ChannelOrder order, int index)
    {
        int rows = 0;
        for (int i = 0; i < index && i < order.Tokens.Count; i++)
        {
            if (order.Tokens[i] is PatternRefToken token)
                rows += RowsOf(token.PatternNumber);
        }

        return rows * PixelsPerRow;
    }

    private int LaneLength(ChannelOrder order) => OffsetOf(order, order.Tokens.Count);

    /// <summary>
    /// The project cursor as a place in the song: a clip and how many ticks into it, which the
    /// sequencer can find in the music. Read in the selected channel's lane when it has a clip
    /// there, otherwise the first that does. Null at the very start, or past every lane's end -
    /// either way, play from the top.
    /// </summary>
    public (int Channel, int Index, int TicksInto)? PlayFrom => LocationAt(_cursor);

    /// <summary>A place on the lanes, in ticks, as a clip and the ticks into it - see
    /// <see cref="PlayFrom"/>. Null at the very start, or past every lane's end.</summary>
    public (int Channel, int Index, int TicksInto)? LocationAt(double place)
    {
        if (_song is not { } song)
            return null;

        int ticks = (int)Math.Floor(place);
        foreach (int channel in new[] { _selectedChannel, 0, 1, 2 })
        {
            var order = song.ChannelOrders[channel];
            int start = 0;
            for (int i = 0; i < order.Tokens.Count; i++)
            {
                if (order.Tokens[i] is not PatternRefToken token)
                    continue;

                int length = RowsOf(token.PatternNumber);
                if (ticks >= start && ticks < start + length)
                    return i == 0 && ticks == 0 ? null : (channel, i, ticks - start);
                start += length;
            }
        }

        return null;
    }

    /// <summary>The project cursor, in ticks along the lanes.</summary>
    public double ProjectCursor => _cursor;

    /// <summary>Playback is starting from the cursor: a second stop comes back here.</summary>
    public void MarkPlayStart() => _playStart = _cursor;

    /// <summary>The second stop: the cursor goes back to where playback last started.</summary>
    public void ReturnToPlayStart()
    {
        _cursor = _playStart;
        ScrollToTicks(_cursor);
        Invalidate();
    }

    /// <summary>Playing from the top: the cursor starts there too.</summary>
    public void ResetProjectCursor()
    {
        _cursor = 0;
        _playStart = 0;
        Invalidate();
    }

    /// <summary>
    /// Where each channel is: its order entry and how many ticks into it, or null while it is not
    /// playing. Each lane draws its own playhead, and the project cursor rides the longest lane's -
    /// the song's own timeline, which the shorter lanes loop round inside. With
    /// <paramref name="follow"/>, the lanes scroll to keep the cursor in view.
    /// </summary>
    public void SetLanePositions(IReadOnlyList<(int Entry, double Ticks)?> channels, bool follow)
    {
        if (_song is not { } song)
            return;

        int reference = Enumerable.Range(0, Song.ChannelCount).OrderByDescending(ch => LaneLength(song.ChannelOrders[ch])).First();
        for (int ch = 0; ch < Song.ChannelCount; ch++)
        {
            _lanePlayhead[ch] = ch < channels.Count && channels[ch] is { } at && at.Entry >= 0
                ? (OffsetOf(song.ChannelOrders[ch], at.Entry) / (double)PixelsPerRow) + at.Ticks
                : double.NaN;
        }

        if (!double.IsNaN(_lanePlayhead[reference]))
            _cursor = _lanePlayhead[reference];

        if (follow)
            ScrollToTicks(_cursor);

        Invalidate();
    }

    public void ClearLanePlayheads()
    {
        Array.Fill(_lanePlayhead, double.NaN);
        Invalidate();
    }

    /// <summary>Scrolls just enough to bring a place on the lanes into view - a page at a time, so
    /// the lanes stand still while the cursor crosses them.</summary>
    private void ScrollToTicks(double ticks)
    {
        if (!_scroll.Enabled)
            return;

        int x = (int)(ticks * PixelsPerRow);
        int visible = Math.Max(1, Width - GutterWidth);
        int max = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
        if (x < _scroll.Value || x > _scroll.Value + visible - 20)
            _scroll.Value = Math.Clamp(x - 40, 0, max);
    }

    private void UpdateScrollRange()
    {
        if (_song is null)
            return;

        int longest = _song.ChannelOrders.Max(LaneLength);
        int visible = Math.Max(1, Width - GutterWidth);

        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, longest + 120);
        _scroll.LargeChange = visible;
        _scroll.SmallChange = PixelsPerRow * 8;
        _scroll.Enabled = longest > visible;
        if (!_scroll.Enabled)
            _scroll.Value = 0;
    }

    protected override void OnResize(EventArgs e)
    {
        UpdateScrollRange();
        base.OnResize(e);
    }

    /// <summary>Whether a channel is silenced, for drawing. Read from the tracker grid's own
    /// state rather than kept here, so the two views cannot disagree about which voice is off.
    /// </summary>
    public Func<int, bool>? IsChannelMuted { get; set; }

    /// <summary>A click on a lane's header asked for that channel to be muted or unmuted.</summary>
    public event Action<int>? MuteToggled;

    /// <summary>A clip's end was dragged, which is the pattern's own length - so this is a change
    /// to the pattern, not to the order, and the roll showing it has to be told.</summary>
    public event Action? PatternLengthChanged;

    /// <summary>A clip's end moved during a drag - the pattern's rows already changed, so the roll
    /// showing it has to re-read them now, not at the end of the drag: a repaint in between read
    /// rows that were no longer there.</summary>
    public event Action? PatternLengthChanging;

    /// <summary>The clip being resized, as (channel, index), or null.</summary>
    private (int Channel, int Index)? _resizing;

    private bool _resizeChanged;

    private int LaneTop(int channel) => RulerHeight + (channel * (LaneHeight + LaneGap));

    /// <summary>The lane header under a point - the gutter the channel's name sits in.</summary>
    private int? HeaderAt(Point point)
    {
        if (point.X >= GutterWidth)
        {
            return null;
        }

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            int top = LaneTop(channel);
            if (point.Y >= top && point.Y < top + LaneHeight)
            {
                return channel;
            }
        }

        return null;
    }

    /// <summary>The clip under a point, if any.</summary>
    private (int Channel, int Index)? ClipAt(Point point)
    {
        if (_song is null || point.X < GutterWidth)
            return null;

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            int top = LaneTop(channel);
            if (point.Y < top || point.Y >= top + LaneHeight)
                continue;

            var order = _song.ChannelOrders[channel];
            int x = point.X - GutterWidth + _scroll.Value;

            for (int i = 0; i < order.Tokens.Count; i++)
            {
                if (order.Tokens[i] is not PatternRefToken token)
                    continue;

                int start = OffsetOf(order, i);
                int width = Math.Max(PixelsPerRow * 2, RowsOf(token.PatternNumber) * PixelsPerRow);
                if (x >= start && x < start + width)
                    return (channel, i);
            }

            return null;
        }

        return null;
    }

    /// <summary>A lane's RST block, in view coordinates.</summary>
    private Rectangle RestartBox(ChannelOrder order, int channel) => new(
        GutterWidth + LaneLength(order) - _scroll.Value + RestartGap,
        LaneTop(channel) + 6,
        RestartWidth,
        LaneHeight - 14);

    /// <summary>The channel whose RST block is under a point, or -1.</summary>
    private int RestartAt(Point point)
    {
        if (_song is null || point.X < GutterWidth)
            return -1;

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            if (RestartBox(_song.ChannelOrders[channel], channel).Contains(point))
                return channel;
        }

        return -1;
    }

    private void SelectRestart(int channel)
    {
        if (_song is null)
            return;

        _selectedChannel = channel;
        _selectedIndex = Math.Max(0, _song.ChannelOrders[channel].Tokens.Count - 1);
        _onRestart = true;
        Invalidate();
        AnnounceSelection();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && RestartAt(e.Location) is var channel and >= 0)
        {
            SelectRestart(channel);
            BeginRestartEntry(channel);
        }

        base.OnMouseDoubleClick(e);
    }

    private void BeginRestartEntry(int channel)
    {
        if (_song is not { } song)
            return;

        var box = RestartBox(song.ChannelOrders[channel], channel);
        _restartEntryChannel = channel;
        _restartEntry.Text = song.ChannelOrders[channel].RestartPosition.ToString("00");
        _restartEntry.ForeColor = DawPalette.Text;
        _restartEntry.Bounds = new Rectangle(box.X + 2, box.Y + (box.Height / 2) - 12, box.Width - 4, 24);
        _restartEntry.Visible = true;
        _restartEntry.BringToFront();
        _restartEntry.Focus();
        _restartEntry.SelectAll();
    }

    private void EndRestartEntry(bool keep)
    {
        // Hiding a focused box moves the focus, and LostFocus comes back in here before the box
        // counts as hidden - the channel is let go of first, so that second call finds nothing.
        if (!_restartEntry.Visible || _restartEntryChannel < 0 || _song is not { } song)
            return;

        var order = song.ChannelOrders[_restartEntryChannel];
        bool valid = int.TryParse(_restartEntry.Text.Trim(), out int typed)
                     && typed >= 1 && typed <= order.Tokens.Count;

        if (keep && !valid && _restartEntry.Focused)
        {
            // Not an entry of this channel's order: say so and stay open. Escape is the way out.
            System.Media.SystemSounds.Beep.Play();
            _restartEntry.ForeColor = Color.FromArgb(0xFF, 0x66, 0x66);
            _restartEntry.SelectAll();
            return;
        }

        _restartEntryChannel = -1;
        _restartEntry.Visible = false;
        Focus();

        if (keep && valid && typed != order.RestartPosition)
        {
            order.RestartPosition = typed;
            Edited();
        }
    }

    /// <summary>The last few pixels of a clip, where a drag changes the pattern's length. Every
    /// clip of that pattern changes with it, in this lane and the others - a pattern has one
    /// length, and the lanes are showing the same thing twice.</summary>
    private (int Channel, int Index)? EndHandleAt(Point point)
    {
        if (ClipAt(point) is not { } at || _song is null)
        {
            return null;
        }

        var order = _song.ChannelOrders[at.Channel];
        if (order.Tokens[at.Index] is not PatternRefToken token)
        {
            return null;
        }

        int end = GutterWidth + OffsetOf(order, at.Index)
                  + Math.Max(PixelsPerRow * 2, RowsOf(token.PatternNumber) * PixelsPerRow)
                  - _scroll.Value;

        return point.X >= end - 5 && point.X <= end + 1 ? at : null;
    }

    /// <summary>Drags a clip's end to wherever the pointer is, in whole rows. One row is the floor:
    /// a pattern of nothing has no row for anything to be written on.</summary>
    private void DragClipEnd(int x)
    {
        if (_resizing is not { } at || _song is null)
        {
            return;
        }

        var order = _song.ChannelOrders[at.Channel];
        if (order.Tokens[at.Index] is not PatternRefToken token
            || _song.Patterns.FirstOrDefault(p => p.Number == token.PatternNumber) is not { } pattern)
        {
            return;
        }

        // The clip is as wide as the ticks it plays, so the drag asks for ticks: rows are added
        // or taken at the pattern's end - one tick each there, after any loop - and taking stops
        // at a loop marker rather than cut a loop in half.
        int start = GutterWidth + OffsetOf(order, at.Index) - _scroll.Value;
        int wanted = Math.Max(1, (int)Math.Round((x - start) / (double)PixelsPerRow));
        int delta = wanted - RowsOf(token.PatternNumber);
        if (delta == 0)
        {
            return;
        }

        for (; delta < 0 && pattern.Rows.Count > 1 && pattern.Rows[^1] is not (LoopStartRow or LoopEndRow); delta++)
        {
            pattern.Rows.RemoveAt(pattern.Rows.Count - 1);
        }

        for (; delta > 0; delta--)
        {
            pattern.Rows.Add(new HoldRow());
        }

        _resizeChanged = true;
        UpdateScrollRange();
        Invalidate();
        PatternLengthChanging?.Invoke();
    }

    private double TicksAtX(int x) => Math.Max(0, (x - GutterWidth + _scroll.Value) / PixelsPerRow);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_rulerPressX >= 0)
        {
            if (_rulerDragging || Math.Abs(e.X - _rulerPressX) > 3)
            {
                _rulerDragging = true;
                double a = TicksAtX(_rulerPressX), b = TicksAtX(e.X);
                _locators = (Math.Min(a, b), Math.Max(a, b) + 1);
                Invalidate();
            }

            base.OnMouseMove(e);
            return;
        }

        if (_resizing is not null)
        {
            DragClipEnd(e.X);
            base.OnMouseMove(e);
            return;
        }

        if (_dragFrom is not null)
        {
            if (!_dragging && (Math.Abs(e.X - _dragPress.X) > 4 || Math.Abs(e.Y - _dragPress.Y) > 4))
                _dragging = true;

            if (_dragging)
            {
                _dropAt = DropAt(e.Location);
                Cursor = _dropAt is null ? Cursors.No : Cursors.SizeAll;
                Invalidate();
            }

            base.OnMouseMove(e);
            return;
        }

        // The size is only worked out again when the mouse moves onto another run of clips.
        if (_tool == RollTool.Glue)
        {
            var range = ClipAt(e.Location) is { } over ? GlueRange(over) : null;
            if (range is null)
                SetGluePreview(null);
            else if (_gluePreview is not { } shown || (shown.Channel, shown.From, shown.To) != range.Value)
                SetGluePreview((range.Value.Channel, range.Value.From, range.Value.To, GlueBytes(range.Value) <= 255));
        }

        if (_tool == RollTool.Split)
        {
            (int Channel, int X)? cut = ClipAt(e.Location) is { } clip && SplitPoint(clip, e.X) is { } row
                ? (clip.Channel, ClipLeft(clip) + (row * PixelsPerRow))
                : null;
            if (cut != _splitAt)
            {
                _splitAt = cut;
                Invalidate();
            }
        }

        Cursor = _tool == RollTool.Select && EndHandleAt(e.Location) is not null ? Cursors.SizeWE : Cursors.Default;

        var at = ClipAt(e.Location) ?? (-1, -1);
        int restart = RestartAt(e.Location);
        if (at != _hovered || restart != _hoveredRestart)
        {
            _hovered = at;
            _hoveredRestart = restart;
            Cursor = at.Item2 >= 0 && _tool == RollTool.Split ? Cursors.VSplit
                : at.Item2 >= 0 || restart >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = (-1, -1);
        _hoveredRestart = -1;
        _splitAt = null;
        SetGluePreview(null);
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        if (HeaderAt(e.Location) is { } header)
        {
            MuteToggled?.Invoke(header);
            Invalidate();
            base.OnMouseDown(e);
            return;
        }

        // The ruler: a click puts the project cursor there - and makes it where a second stop
        // returns to - a drag sets the locators, and a right-click sends the cursor to the start.
        if (e.Y < RulerHeight && e.X >= GutterWidth)
        {
            if (e.Button == MouseButtons.Right)
            {
                _cursor = 0;
                _playStart = 0;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Left)
            {
                _rulerPressX = e.X;
                _rulerDragging = false;
                Capture = true;
            }

            base.OnMouseDown(e);
            return;
        }

        if (e.Button == MouseButtons.Left && _tool == RollTool.Select && EndHandleAt(e.Location) is { } handle)
        {
            _resizing = handle;
            _resizeChanged = false;
            _selectedChannel = handle.Channel;
            _selectedIndex = handle.Index;
            _onRestart = false;
            Capture = true;
            Invalidate();
            AnnounceSelection();
            base.OnMouseDown(e);
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            ShowClipMenu(e.Location);
            base.OnMouseDown(e);
            return;
        }

        if (ClipAt(e.Location) is { } at)
        {
            // Shift+click marks a run with Glue in hand too, so it is marked and joined without
            // going back to the selection tool.
            bool marking = ModifierKeys.HasFlag(Keys.Shift) && _tool is RollTool.Select or RollTool.Glue;
            if (e.Button == MouseButtons.Left && !marking && UseTool(at, e.X))
            {
                base.OnMouseDown(e);
                return;
            }

            // Shift+click marks the run from the selected clip to this one, in its lane - what
            // Glue joins.
            if (e.Button == MouseButtons.Left && ModifierKeys.HasFlag(Keys.Shift)
                && at.Channel == _selectedChannel && !_onRestart && _selectedIndex >= 0)
            {
                _markEnd = at.Index == _selectedIndex ? -1 : at.Index;
                SetGluePreview(null);
                Invalidate();
                base.OnMouseDown(e);
                return;
            }

            _selectedChannel = at.Channel;
            _selectedIndex = at.Index;
            _onRestart = false;
            _markEnd = -1;
            if (e.Button == MouseButtons.Left && _tool == RollTool.Select)
            {
                _dragFrom = at;
                _dragPress = e.Location;
                _dragging = false;
                Capture = true;
            }

            Invalidate();
            AnnounceSelection();
        }
        else if (RestartAt(e.Location) is var restart and >= 0)
        {
            SelectRestart(restart);
        }

        base.OnMouseDown(e);
    }

    /// <summary>A left click on a clip with a tool other than the selection: true when the tool
    /// took it.</summary>
    private bool UseTool((int Channel, int Index) at, int x)
    {
        switch (_tool)
        {
            case RollTool.Erase:
                if (_song!.ChannelOrders[at.Channel].Tokens.Count <= 1)
                    Notice?.Invoke("A lane keeps at least one clip - the channel has to play something.");
                else
                    DeleteClip(at.Channel, at.Index);
                _markEnd = -1;
                return true;

            case RollTool.Split:
                if (SplitPoint(at, x) is { } row)
                    SplitClip(at, row);
                else if (PatternAt(at) is { } pattern && pattern.Rows.Any(r => r is LoopStartRow or LoopEndRow))
                    Notice?.Invoke($"PAT{pattern.Number:00} has a loop - its passes have no one row to cut at. Split it in the tracker.");
                return true;

            case RollTool.Glue:
                GlueAt(at);
                return true;

            default:
                return false;
        }
    }

    private Pattern? PatternAt((int Channel, int Index) at) =>
        _song is { } song && at.Index >= 0 && at.Index < song.ChannelOrders[at.Channel].Tokens.Count
        && song.ChannelOrders[at.Channel].Tokens[at.Index] is PatternRefToken token
            ? song.Patterns.FirstOrDefault(p => p.Number == token.PatternNumber)
            : null;

    private int ClipLeft((int Channel, int Index) at) =>
        GutterWidth + OffsetOf(_song!.ChannelOrders[at.Channel], at.Index) - _scroll.Value;

    /// <summary>How many clips play a pattern, in all three lanes.</summary>
    private static int Uses(Song song, int patternNumber) =>
        song.ChannelOrders.Sum(o => o.Tokens.Count(t => t is PatternRefToken p && p.PatternNumber == patternNumber));

    /// <summary>A new pattern at the end of the list, the next number - the only one that keeps the
    /// numbers 1..N the assembler wants.</summary>
    private static Pattern AddPattern(Song song, IEnumerable<PatternRow> rows)
    {
        var pattern = new Pattern { Number = song.Patterns.Count + 1 };
        pattern.Rows.AddRange(rows);
        song.Patterns.Add(pattern);
        return pattern;
    }

    /// <summary>The row a split at <paramref name="x"/> cuts before - the nearest row edge - or
    /// null where the clip cannot be cut: at its very ends, or anywhere in a pattern with a loop,
    /// whose passes have no single row to cut at.</summary>
    private int? SplitPoint((int Channel, int Index) at, int x)
    {
        if (PatternAt(at) is not { } pattern || pattern.Rows.Any(r => r is LoopStartRow or LoopEndRow))
            return null;

        int row = (int)Math.Round((x - ClipLeft(at)) / (double)PixelsPerRow);
        return row > 0 && row < pattern.Rows.Count ? row : null;
    }

    /// <summary>
    /// Cuts a clip in two at <paramref name="row"/>: the rows before it and the rows from it on,
    /// each its own pattern, one clip after the other. A pattern played only here is cut in place
    /// and the second half becomes the next pattern; one other clips also play is left as it is,
    /// for them, and both halves are new.
    /// </summary>
    private void SplitClip((int Channel, int Index) at, int row)
    {
        var song = _song!;
        var source = PatternAt(at)!;
        bool own = Uses(song, source.Number) == 1;
        if (song.Patterns.Count + (own ? 1 : 2) > MaxPatterns)
        {
            Notice?.Invoke($"A song holds at most {MaxPatterns} patterns.");
            return;
        }

        var order = song.ChannelOrders[at.Channel];
        var rest = source.Rows.Skip(row).ToList();
        Pattern first;
        if (own)
        {
            source.Rows.RemoveRange(row, source.Rows.Count - row);
            first = source;
        }
        else
        {
            first = AddPattern(song, source.Rows.Take(row));
        }

        var second = AddPattern(song, rest);
        order.Tokens[at.Index] = new PatternRefToken(first.Number);
        order.InsertEntry(at.Index + 1, second.Number);

        _selectedChannel = at.Channel;
        _selectedIndex = at.Index;
        _onRestart = false;
        _markEnd = -1;
        _splitAt = null;
        Edited();
        if (own)
            PatternLengthChanged?.Invoke();
        AnnounceSelection();
        Notice?.Invoke($"PAT{source.Number:00} split at row {row}: PAT{first.Number:00} and PAT{second.Number:00}.");
    }

    /// <summary>
    /// Joins clips into one: the run marked with Shift+click when the click is on it, otherwise
    /// the clip clicked and the one after it. Their rows follow one another in a single pattern -
    /// the first clip's own, when nothing else plays it, otherwise a new one. A pattern holds 255
    /// bytes of note data, so a join that would not fit is refused.
    /// </summary>
    private void GlueAt((int Channel, int Index) at)
    {
        var song = _song!;
        var order = song.ChannelOrders[at.Channel];
        if (GlueRange(at) is not { } range)
        {
            Notice?.Invoke("Glue joins a clip to the one after it - this is the lane's last.");
            return;
        }

        int from = range.From, to = range.To;
        var patterns = Enumerable.Range(from, to - from + 1).Select(i => PatternAt((at.Channel, i))).ToList();
        if (patterns.Any(p => p is null))
            return;

        var rows = patterns.SelectMany(p => p!.Rows).ToList();
        int bytes = GlueBytes(range);
        if (bytes > 255)
        {
            Notice?.Invoke(bytes == int.MaxValue
                ? "Those clips cannot be one pattern."
                : $"Glued, the pattern would be {bytes} bytes - a pattern holds 255.");
            return;
        }

        var first = patterns[0]!;
        bool own = Uses(song, first.Number) == 1;
        if (!own && song.Patterns.Count >= MaxPatterns)
        {
            Notice?.Invoke($"A song holds at most {MaxPatterns} patterns.");
            return;
        }

        Pattern glued;
        if (own)
        {
            first.Rows.Clear();
            first.Rows.AddRange(rows);
            glued = first;
        }
        else
        {
            glued = AddPattern(song, rows);
        }

        for (int i = to; i > from; i--)
            order.RemoveEntryAt(i);
        order.Tokens[from] = new PatternRefToken(glued.Number);

        _selectedChannel = at.Channel;
        _selectedIndex = from;
        _onRestart = false;
        _markEnd = -1;
        SetGluePreview(null);
        Edited();
        if (own)
            PatternLengthChanged?.Invoke();
        AnnounceSelection();
        Notice?.Invoke($"{to - from + 1} clips glued into PAT{glued.Number:00}.");
    }

    /// <summary>The clips a Glue click on <paramref name="at"/> joins: the run marked with
    /// Shift+click when the click is on it, otherwise the clip and the one after it - or null for
    /// a lane's last clip, which has nothing to join.</summary>
    private (int Channel, int From, int To)? GlueRange((int Channel, int Index) at)
    {
        int from = at.Index, to = at.Index + 1;
        if (_markEnd >= 0 && IsMarked(at.Channel, at.Index))
            (from, to) = (Math.Min(_selectedIndex, _markEnd), Math.Max(_selectedIndex, _markEnd));

        return to < _song!.ChannelOrders[at.Channel].Tokens.Count ? (at.Channel, from, to) : null;
    }

    /// <summary>The note data the joined pattern would take, or int.MaxValue when the rows cannot
    /// be one pattern at all.</summary>
    private int GlueBytes((int Channel, int From, int To) range)
    {
        var patterns = Enumerable.Range(range.From, range.To - range.From + 1)
            .Select(i => PatternAt((range.Channel, i))).ToList();
        if (patterns.Any(p => p is null))
            return int.MaxValue;

        try
        {
            return SongSizeCalculator.EncodedPatternLength(patterns.SelectMany(p => p!.Rows).ToList());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return int.MaxValue;
        }
    }

    private void SetGluePreview((int Channel, int From, int To, bool Fits)? preview)
    {
        if (preview == _gluePreview)
            return;

        _gluePreview = preview;
        _pulse.Enabled = preview is not null;
        Invalidate();
    }

    /// <summary>Where a dragged clip would land: the lane under the mouse and the place in its
    /// order, before the first clip whose middle is right of the mouse. Null off the lanes, or out
    /// of a lane that would be left empty.</summary>
    private (int Channel, int Index)? DropAt(Point point)
    {
        int lane = LaneAt(point);
        if (_song is not { } song || _dragFrom is not { } from || lane < 0)
            return null;

        if (lane != from.Channel && song.ChannelOrders[from.Channel].Tokens.Count <= 1)
            return null;

        var order = song.ChannelOrders[lane];
        int x = point.X - GutterWidth + _scroll.Value;
        for (int i = 0; i < order.Tokens.Count; i++)
        {
            if (order.Tokens[i] is not PatternRefToken token)
                continue;

            int width = Math.Max(PixelsPerRow * 2, RowsOf(token.PatternNumber) * PixelsPerRow);
            if (x < OffsetOf(order, i) + (width / 2))
                return (lane, i);
        }

        return (lane, order.Tokens.Count);
    }

    /// <summary>Moves a clip to another place in its lane, or into another lane.</summary>
    private void MoveClip((int Channel, int Index) from, (int Channel, int Index) drop)
    {
        var song = _song!;
        var source = song.ChannelOrders[from.Channel];
        var target = song.ChannelOrders[drop.Channel];
        int index = drop.Index;
        if (from.Channel == drop.Channel)
        {
            if (index == from.Index || index == from.Index + 1)
                return;
            if (index > from.Index)
                index--;
        }

        if (source.Tokens[from.Index] is not PatternRefToken token)
            return;

        source.RemoveEntryAt(from.Index);
        target.InsertEntry(index, token.PatternNumber);

        _selectedChannel = drop.Channel;
        _selectedIndex = index;
        _onRestart = false;
        _markEnd = -1;
        Edited();
        AnnounceSelection();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _pulse.Dispose();

        base.Dispose(disposing);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!_scroll.Enabled)
            return;

        int step = _scroll.SmallChange * 3 * Math.Sign(-e.Delta);
        _scroll.Value = Math.Clamp(_scroll.Value + step, _scroll.Minimum, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Delete or Keys.Insert or Keys.Enter
        || (keyData & Keys.KeyCode) is Keys.Left or Keys.Right;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_song is not { } song || _selectedIndex < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var order = song.ChannelOrders[_selectedChannel];
        if (e.KeyCode is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu))
            _markEnd = -1;

        if (_onRestart)
        {
            RestartKey(order, e);
            base.OnKeyDown(e);
            return;
        }

        if (!e.Control && !e.Alt && DigitOf(e.KeyCode) is { } digit)
        {
            EnterPatternDigit(song, order, digit);
            e.Handled = e.SuppressKeyPress = true;
            base.OnKeyDown(e);
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Left when e.Control:
            case Keys.Right when e.Control:
            {
                int moved = order.MoveEntry(_selectedIndex, e.KeyCode == Keys.Left ? -1 : 1);
                if (moved >= 0)
                {
                    _selectedIndex = moved;
                    Edited();
                }

                e.Handled = true;
                break;
            }

            case Keys.Left:
                _selectedIndex = Math.Max(0, _selectedIndex - 1);
                Invalidate();
                e.Handled = true;
                break;

            // Past the last clip is the lane's RST block.
            case Keys.Right:
                if (_selectedIndex >= order.Tokens.Count - 1)
                    _onRestart = true;
                _selectedIndex = Math.Min(order.Tokens.Count - 1, _selectedIndex + 1);
                Invalidate();
                e.Handled = true;
                break;

            case Keys.Up:
                _selectedChannel = Math.Max(0, _selectedChannel - 1);
                ClampSelection();
                e.Handled = true;
                break;

            case Keys.Down:
                _selectedChannel = Math.Min(Song.ChannelCount - 1, _selectedChannel + 1);
                ClampSelection();
                e.Handled = true;
                break;

            case Keys.Delete:
                DeleteClip(_selectedChannel, _selectedIndex);
                e.Handled = true;
                break;

            case Keys.Insert:
                InsertClip(_selectedChannel, _selectedIndex + 1, newPattern: e.Shift);
                e.Handled = true;
                break;

            // Where the channel goes back to at the end of its own order. Stored one-based, as
            // the file format writes it; the lane's RST block shows it.
            case Keys.R when e.Control:
                order.RestartPosition = _selectedIndex + 1;
                Edited();
                e.Handled = true;
                break;
        }

        AnnounceSelection();
        base.OnKeyDown(e);
    }

    /// <summary>Keys on a selected RST block: digits retype the restart entry the way they retype
    /// a clip's pattern, Enter opens the box, Left goes back to the last clip, Up and Down move to
    /// the next lane's RST block.</summary>
    private void RestartKey(ChannelOrder order, KeyEventArgs e)
    {
        if (!e.Control && !e.Alt && DigitOf(e.KeyCode) is { } digit)
        {
            bool combine = _pendingDigit >= 0 && (DateTime.UtcNow - _lastDigitTime) < TimeSpan.FromMilliseconds(800);
            int typed = combine ? (_pendingDigit * 10) + digit : digit;
            _pendingDigit = combine ? -1 : digit;
            _lastDigitTime = DateTime.UtcNow;

            if (typed >= 1)
            {
                order.RestartPosition = Math.Min(typed, Math.Max(1, order.Tokens.Count));
                Edited();
            }

            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Left:
                _onRestart = false;
                _selectedIndex = Math.Max(0, order.Tokens.Count - 1);
                Invalidate();
                AnnounceSelection();
                e.Handled = true;
                break;

            case Keys.Up:
            case Keys.Down:
                SelectRestart(Math.Clamp(_selectedChannel + (e.KeyCode == Keys.Up ? -1 : 1), 0, Song.ChannelCount - 1));
                e.Handled = true;
                break;

            case Keys.Enter:
                BeginRestartEntry(_selectedChannel);
                e.Handled = e.SuppressKeyPress = true;
                break;

            // The lane's end: Insert adds a clip there, before RST, and Delete takes the last one.
            case Keys.Insert:
                InsertClip(_selectedChannel, order.Tokens.Count, newPattern: e.Shift);
                e.Handled = true;
                break;

            case Keys.Delete:
                DeleteClip(_selectedChannel, order.Tokens.Count - 1);
                SelectRestart(_selectedChannel);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The most patterns a song can be given here: a clip's pattern is typed as two
    /// digits, so a pattern 100 could be made but never typed back.</summary>
    private const int MaxPatterns = 99;

    private bool CanAddPattern => _song is { } song && song.Patterns.Count < MaxPatterns;

    /// <summary>
    /// A new clip at <paramref name="index"/> in a lane - a copy of the clip before it (the first
    /// clip's pattern at the start of a lane), or a new, empty pattern of its own: the next number,
    /// the same one typing it would create. The new clip is selected, so two digits retype it.
    /// </summary>
    private void InsertClip(int channel, int index, bool newPattern)
    {
        if (_song is not { } song)
            return;

        var order = song.ChannelOrders[channel];
        index = Math.Clamp(index, 0, order.Tokens.Count);

        int pattern;
        if (newPattern)
        {
            if (!CanAddPattern)
                return;
            pattern = song.ResolveTypedPatternNumber(song.Patterns.Count + 1);
        }
        else
        {
            int from = Math.Clamp(index - 1, 0, order.Tokens.Count - 1);
            if (order.Tokens.Count == 0 || order.Tokens[from] is not PatternRefToken copy)
                return;
            pattern = copy.PatternNumber;
        }

        order.InsertEntry(index, pattern);
        _selectedChannel = channel;
        _selectedIndex = index;
        _onRestart = false;
        Edited();
        AnnounceSelection();
    }

    /// <summary>Takes a clip out of its lane. A channel with no entries at all cannot be assembled
    /// - Tables.s asserts it - so the last clip in a lane refuses to go.</summary>
    private void DeleteClip(int channel, int index)
    {
        if (_song is not { } song)
            return;

        var order = song.ChannelOrders[channel];
        if (order.Tokens.Count <= 1 || index < 0 || index >= order.Tokens.Count)
            return;

        order.RemoveEntryAt(index);
        _selectedChannel = channel;
        _selectedIndex = Math.Min(index, order.Tokens.Count - 1);
        Edited();
        AnnounceSelection();
    }

    /// <summary>
    /// Right-click: picks the clip or RST block under the mouse, then offers what the keys do
    /// there. A click on a lane's empty end counts as its RST block.
    /// </summary>
    private void ShowClipMenu(Point point)
    {
        if (BuildClipMenu(point) is not { } menu)
            return;

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, point);
    }

    private ContextMenuStrip? BuildClipMenu(Point point)
    {
        if (_song is not { } song)
            return null;

        int channel;
        bool onClip;
        if (ClipAt(point) is { } at)
        {
            _selectedChannel = at.Channel;
            _selectedIndex = at.Index;
            _onRestart = false;
            channel = at.Channel;
            onClip = true;
            Invalidate();
            AnnounceSelection();
        }
        else if ((RestartAt(point) is var restart and >= 0 ? restart : LaneAt(point)) is var lane and >= 0)
        {
            SelectRestart(lane);
            channel = lane;
            onClip = false;
        }
        else
        {
            return null;
        }

        var order = song.ChannelOrders[channel];
        int index = _selectedIndex;
        int insertAt = onClip ? index + 1 : order.Tokens.Count;
        int deleteAt = onClip ? index : order.Tokens.Count - 1;
        bool canDelete = order.Tokens.Count > 1;

        var menu = new ContextMenuStrip();
        if (onClip)
        {
            menu.Items.Add(new ToolStripMenuItem("Insert copy after", null, (_, _) => InsertClip(channel, insertAt, newPattern: false)) { ShortcutKeyDisplayString = "Ins" });
            menu.Items.Add(new ToolStripMenuItem($"Insert new pattern {song.Patterns.Count + 1:00} after", null, (_, _) => InsertClip(channel, insertAt, newPattern: true)) { ShortcutKeyDisplayString = "Shift+Ins", Enabled = CanAddPattern });
            menu.Items.Add(new ToolStripMenuItem("Delete clip", null, (_, _) => DeleteClip(channel, deleteAt)) { ShortcutKeyDisplayString = "Del", Enabled = canDelete });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Restart here", null, (_, _) =>
            {
                order.RestartPosition = index + 1;
                Edited();
            }) { ShortcutKeyDisplayString = "Ctrl+R" });
        }
        else
        {
            menu.Items.Add(new ToolStripMenuItem("Add clip before RST (copy of the last)", null, (_, _) => InsertClip(channel, insertAt, newPattern: false)) { ShortcutKeyDisplayString = "Ins" });
            menu.Items.Add(new ToolStripMenuItem($"Add new pattern {song.Patterns.Count + 1:00} before RST", null, (_, _) => InsertClip(channel, insertAt, newPattern: true)) { ShortcutKeyDisplayString = "Shift+Ins", Enabled = CanAddPattern });
            menu.Items.Add(new ToolStripMenuItem("Delete last clip", null, (_, _) =>
            {
                DeleteClip(channel, deleteAt);
                SelectRestart(channel);
            }) { ShortcutKeyDisplayString = "Del", Enabled = canDelete });
        }

        DarkTheme.Apply(menu);
        return menu;
    }

    /// <summary>The lane under a point, by height alone - or -1.</summary>
    private int LaneAt(Point point)
    {
        if (_song is null || point.X < GutterWidth)
            return -1;

        for (int channel = 0; channel < Song.ChannelCount; channel++)
        {
            int top = LaneTop(channel);
            if (point.Y >= top && point.Y < top + LaneHeight)
                return channel;
        }

        return -1;
    }

    private void ClampSelection()
    {
        if (_song is not { } song)
            return;

        int count = song.ChannelOrders[_selectedChannel].Tokens.Count;
        _selectedIndex = count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, count - 1);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_rulerPressX >= 0)
        {
            if (_rulerDragging)
            {
                LocatorsChanged?.Invoke();
            }
            else
            {
                _cursor = TicksAtX(_rulerPressX);
                _playStart = _cursor;
            }

            _rulerPressX = -1;
            _rulerDragging = false;
            Capture = false;
            Invalidate();
            base.OnMouseUp(e);
            return;
        }

        if (_dragFrom is { } dragged)
        {
            _dragFrom = null;
            Capture = false;
            if (_dragging && _dropAt is { } drop)
                MoveClip(dragged, drop);

            _dragging = false;
            _dropAt = null;
            Invalidate();
            base.OnMouseUp(e);
            return;
        }

        // One edit for the drag rather than one per pixel - the song is marked dirty and every
        // view rebuilt on each of them.
        if (_resizing is not null)
        {
            _resizing = null;
            Capture = false;

            if (_resizeChanged)
            {
                _resizeChanged = false;
                UpdateScrollRange();
                Invalidate();
                PatternLengthChanged?.Invoke();
            }
        }

        base.OnMouseUp(e);
    }

    private int _pendingDigit = -1;
    private DateTime _lastDigitTime;

    private static int? DigitOf(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => key - Keys.D0,
        >= Keys.NumPad0 and <= Keys.NumPad9 => key - Keys.NumPad0,
        _ => null,
    };

    /// <summary>
    /// Retypes which pattern the selected clip plays: two digits within 800ms make one number, the
    /// way the tracker's own order field and instrument entry work. The clip changes on the first
    /// digit and again on the second, so "02" shows 0 clamped to pattern 1 for a moment and then
    /// lands on 2 - the same as the tracker, and the reason a typed number may only ever create the
    /// one pattern that comes next (see Song.ResolveTypedPatternNumber).
    /// </summary>
    private void EnterPatternDigit(Song song, ChannelOrder order, int digit)
    {
        if (_selectedIndex >= order.Tokens.Count || order.Tokens[_selectedIndex] is not PatternRefToken)
        {
            return;
        }

        bool combine = _pendingDigit >= 0 && (DateTime.UtcNow - _lastDigitTime) < TimeSpan.FromMilliseconds(800);
        int typed = combine ? (_pendingDigit * 10) + digit : digit;
        _pendingDigit = combine ? -1 : digit;
        _lastDigitTime = DateTime.UtcNow;

        order.Tokens[_selectedIndex] = new PatternRefToken(song.ResolveTypedPatternNumber(typed));
        Edited();
        AnnounceSelection();
    }

    private void Edited()
    {
        UpdateScrollRange();
        Invalidate();
        OrderEdited?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(DawPalette.Background);

        if (_song is not { } song)
            return;

        DrawRuler(g, song);

        for (int channel = 0; channel < Song.ChannelCount; channel++)
            DrawLane(g, song, channel);

        DrawLocators(g);
        DrawPlayFrom(g);
        DrawLanePlayheads(g);
        DrawToolMarks(g, song);

    }

    /// <summary>The locators: a band along the bottom of the ruler with L and R at its ends, and,
    /// with Cycle on, the range shaded faintly down the lanes.</summary>
    private void DrawLocators(Graphics g)
    {
        if (_locators is not { } range)
            return;

        int x0 = GutterWidth + (int)(range.Left * PixelsPerRow) - _scroll.Value;
        int x1 = GutterWidth + (int)(range.Right * PixelsPerRow) - _scroll.Value;
        int left = Math.Max(GutterWidth, x0), right = Math.Min(Width, x1);
        if (right <= left)
            return;

        var accent = DawPalette.Oscillator;
        if (_cycleOn)
        {
            using var shade = new SolidBrush(DawPalette.WithAlpha(accent, 16));
            g.FillRectangle(shade, left, RulerHeight, right - left, Height - ScrollHeight - RulerHeight);
        }

        using (var band = new SolidBrush(DawPalette.WithAlpha(accent, _cycleOn ? 110 : 55)))
            g.FillRectangle(band, left, RulerHeight - 6, right - left, 5);

        using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
        using var ink = new SolidBrush(DawPalette.WithAlpha(accent, 220));
        if (x0 >= GutterWidth)
            g.DrawString("L", font, ink, x0 - 1, RulerHeight - 17);
        if (x1 <= Width)
            g.DrawString("R", font, ink, x1 - 8, RulerHeight - 17);
    }

    /// <summary>Where a dragged clip would land - a bar between clips - and where the split tool
    /// would cut.</summary>
    private void DrawToolMarks(Graphics g, Song song)
    {
        // Clips scrolled half out of view still start left of the lanes; nothing of the marks
        // goes over the channel names.
        var state = g.Save();
        g.SetClip(new Rectangle(GutterWidth, 0, Math.Max(0, Width - GutterWidth), Height));
        DrawToolMarksInLanes(g, song);
        g.Restore(state);
    }

    private void DrawToolMarksInLanes(Graphics g, Song song)
    {
        if (_dragging && _dropAt is { } drop)
        {
            var order = song.ChannelOrders[drop.Channel];
            int x = GutterWidth - _scroll.Value
                    + (drop.Index < order.Tokens.Count ? OffsetOf(order, drop.Index) : LaneLength(order)) - 1;
            int top = LaneTop(drop.Channel);
            using var bar = new SolidBrush(DawPalette.Oscillator);
            g.FillRectangle(bar, x - 1, top + 3, 3, LaneHeight - 8);
            g.FillPolygon(bar, new Point[] { new(x - 5, top + 2), new(x + 6, top + 2), new(x, top + 8) });
        }

        if (_tool == RollTool.Glue && _gluePreview is { } glue)
            DrawGluePreview(g, song, glue);

        if (_tool == RollTool.Split && _splitAt is { } cut)
        {
            int top = LaneTop(cut.Channel);
            using var line = new Pen(Color.White, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawLine(line, cut.X, top + 4, cut.X, top + LaneHeight - 6);
        }
    }

    /// <summary>
    /// The clips Glue would join, breathing in and out about once a second and a half: each lit
    /// softly, and one outline round all of them - the single clip they would become. In red when
    /// the join would not fit in one pattern, so a click that would be refused looks it.
    /// </summary>
    private void DrawGluePreview(Graphics g, Song song, (int Channel, int From, int To, bool Fits) glue)
    {
        var order = song.ChannelOrders[glue.Channel];
        if (glue.To >= order.Tokens.Count)
            return;

        double phase = (Environment.TickCount64 % 1500) / 1500.0;
        double breath = 0.5 - (0.5 * Math.Cos(phase * 2 * Math.PI));
        var accent = glue.Fits ? DawPalette.Oscillator : Color.FromArgb(0xF0, 0x4A, 0x6E);
        int top = LaneTop(glue.Channel) + 6, height = LaneHeight - 14;

        int left = GutterWidth + OffsetOf(order, glue.From) - _scroll.Value;
        int right = left;
        using (var wash = new SolidBrush(DawPalette.WithAlpha(accent, 22 + (int)(58 * breath))))
        {
            for (int i = glue.From; i <= glue.To; i++)
            {
                if (order.Tokens[i] is not PatternRefToken token)
                    continue;

                int start = GutterWidth + OffsetOf(order, i) - _scroll.Value;
                int width = Math.Max(PixelsPerRow * 2, RowsOf(token.PatternNumber) * PixelsPerRow);
                g.FillRectangle(wash, start, top, width - 2, height);
                right = start + width - 2;
            }
        }

        using var outline = new Pen(DawPalette.WithAlpha(accent, 120 + (int)(120 * breath)), 2f);
        g.DrawRectangle(outline, left - 1, top - 1, right - left + 2, height + 2);
    }

    /// <summary>Each lane's playhead: the red of the piano roll's, across that lane only.</summary>
    private void DrawLanePlayheads(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(0xF0, 0x4A, 0x6E), 2f);
        for (int ch = 0; ch < Song.ChannelCount; ch++)
        {
            if (double.IsNaN(_lanePlayhead[ch]))
                continue;

            int x = GutterWidth + (int)(_lanePlayhead[ch] * PixelsPerRow) - _scroll.Value;
            if (x < GutterWidth || x > Width)
                continue;

            int top = LaneTop(ch);
            g.DrawLine(pen, x, top + 2, x, top + LaneHeight - 2);
        }
    }

    /// <summary>The project cursor: a small flag in the ruler and a faint line down the lanes -
    /// there to be found, not to compete with the clips.</summary>
    private void DrawPlayFrom(Graphics g)
    {
        int x = GutterWidth + (int)(_cursor * PixelsPerRow) - _scroll.Value;
        if (x < GutterWidth || x > Width)
            return;

        var colour = DawPalette.Oscillator;
        using (var line = new Pen(DawPalette.WithAlpha(colour, 70)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
            g.DrawLine(line, x, RulerHeight, x, Height - ScrollHeight);

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var flag = new SolidBrush(DawPalette.WithAlpha(colour, 200)))
            g.FillPolygon(flag, new PointF[] { new(x - 5, RulerHeight - 9), new(x + 5, RulerHeight - 9), new(x, RulerHeight - 1) });
        g.SmoothingMode = smooth;

        using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
        using var ink = new SolidBrush(DawPalette.WithAlpha(colour, 200));
        g.DrawString("F2", font, ink, x + 5, RulerHeight - 13);
    }

    private void DrawRuler(Graphics g, Song song)
    {
        using (var fill = new SolidBrush(DawPalette.PanelHeader))
            g.FillRectangle(fill, 0, 0, Width, RulerHeight);
        using (var rule = new Pen(DawPalette.Border))
            g.DrawLine(rule, 0, RulerHeight, Width, RulerHeight);

        using var font = new Font("Consolas", 7f);
        using var ink = new SolidBrush(DawPalette.TextFaint);
        using var strong = new SolidBrush(DawPalette.TextDim);
        using var tick = new Pen(DawPalette.BorderSoft);

        // Ticks every 16 rows, which is the tracker's own step and the number anyone writing this
        // music already counts in.
        const int rowsPerTick = 16;
        int longest = song.ChannelOrders.Max(LaneLength);

        for (int row = 0; row * PixelsPerRow <= longest + 400; row += rowsPerTick)
        {
            int x = GutterWidth + (row * PixelsPerRow) - _scroll.Value;
            if (x < GutterWidth || x > Width)
                continue;

            bool major = row % (rowsPerTick * 4) == 0;
            g.DrawLine(tick, x, major ? 4 : 12, x, RulerHeight);
            if (major)
                g.DrawString(row.ToString(), font, strong, x + 3, 5);
        }

        using var labelFont = new Font("Segoe UI", 7f, FontStyle.Bold);
        g.DrawString("ROW", labelFont, ink, 10, 6);
    }

    private void DrawLane(Graphics g, Song song, int channel)
    {
        int top = LaneTop(channel);
        var order = song.ChannelOrders[channel];

        using (var laneFill = new SolidBrush(Color.FromArgb(0x0C, 0x0F, 0x17)))
            g.FillRectangle(laneFill, GutterWidth, top, Width - GutterWidth, LaneHeight);

        // header
        using (var headerFill = new SolidBrush(DawPalette.Panel))
            g.FillRectangle(headerFill, 0, top, GutterWidth, LaneHeight);
        using (var border = new Pen(DawPalette.Border))
        {
            g.DrawLine(border, GutterWidth, top, GutterWidth, top + LaneHeight);
            g.DrawLine(border, 0, top + LaneHeight, Width, top + LaneHeight);
        }

        // Name over two lines of counts, centred in the lane rather than pinned to its top: the
        // lane's height follows the view's, and a block measured from the top falls out of a short
        // one.
        const int NameToMeta = 20;
        const int MetaToMeta = 14;
        int textTop = top + Math.Max(4, (LaneHeight - (NameToMeta + MetaToMeta + 12)) / 2);

        // A muted channel says so where its name is, in the same red the tracker's own header
        // uses - the lane keeps its clips, so without this there is nothing to see.
        bool muted = IsChannelMuted?.Invoke(channel) ?? false;

        using (var nameFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
        using (var nameInk = new SolidBrush(muted ? Color.FromArgb(0xFF, 0x66, 0x66) : DawPalette.Text))
            g.DrawString(muted ? $"CH {channel + 1} MUTE" : $"CH {channel + 1}", nameFont, nameInk, 12, textTop);

        using (var metaFont = new Font("Consolas", 7f))
        using (var metaInk = new SolidBrush(DawPalette.TextFaint))
        {
            int rows = LaneLength(order) / PixelsPerRow;
            g.DrawString($"{order.Tokens.Count} clips", metaFont, metaInk, 12, textTop + NameToMeta);
            g.DrawString($"{rows} rows", metaFont, metaInk, 12, textTop + NameToMeta + MetaToMeta);
        }

        // clips
        for (int i = 0; i < order.Tokens.Count; i++)
        {
            if (order.Tokens[i] is not PatternRefToken token)
                continue;

            int start = GutterWidth + OffsetOf(order, i) - _scroll.Value;
            int width = Math.Max(PixelsPerRow * 2, RowsOf(token.PatternNumber) * PixelsPerRow);
            if (start + width < GutterWidth || start > Width)
                continue;

            DrawClip(g, token.PatternNumber, new Rectangle(start, top + 6, width - 2, LaneHeight - 14),
                selected: IsMarked(channel, i),
                hovered: _hovered == (channel, i),
                playing: _playheads[channel] == i,
                // Where the RST block points, picked out while it is pointed at - hovered or
                // selected - rather than always: a mark on the first clip read as happening there.
                restart: order.RestartPosition - 1 == i
                         && (_hoveredRestart == channel || (_onRestart && _selectedChannel == channel)));
            DrawClipPasses(g, token.PatternNumber, new Rectangle(start, top + 6, width - 2, LaneHeight - 14));
        }

        DrawRestart(g, song, order, channel);

        // The end of the list, so a lane that stops early is obvious rather than just empty.
        int endX = GutterWidth + LaneLength(order) - _scroll.Value;
        if (endX >= GutterWidth && endX <= Width)
        {
            using var endPen = new Pen(DawPalette.WithAlpha(DawPalette.TextFaint, 120)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawLine(endPen, endX, top + 4, endX, top + LaneHeight - 4);
        }
    }

    /// <summary>The RST block: the restart entry, one-based as the file and the Retro view write
    /// it, and the pattern that entry plays.</summary>
    private void DrawRestart(Graphics g, Song song, ChannelOrder order, int channel)
    {
        var box = RestartBox(order, channel);
        var clipped = Rectangle.Intersect(box, new Rectangle(GutterWidth, 0, Width - GutterWidth, Height));
        if (clipped.Width <= 0 || order.Tokens.Count == 0)
            return;

        bool selected = _onRestart && _selectedChannel == channel;
        bool hovered = _hoveredRestart == channel;
        var accent = DawPalette.Effect;

        using (var fill = new SolidBrush(hovered ? Color.FromArgb(0x1B, 0x23, 0x33) : Color.FromArgb(0x14, 0x1B, 0x29)))
            g.FillRectangle(fill, clipped);
        if (box.X >= GutterWidth)
        {
            using var edge = new SolidBrush(accent);
            g.FillRectangle(edge, box.X, box.Y, 3, box.Height);
        }
        using (var border = new Pen(selected ? accent : DawPalette.WithAlpha(accent, 90), selected ? 1.6f : 1f))
            g.DrawRectangle(border, clipped.X, clipped.Y, clipped.Width - 1, clipped.Height - 1);

        var previous = g.Clip;
        g.SetClip(clipped);

        using (var label = new Font("Segoe UI", 6.5f, FontStyle.Bold))
        using (var labelInk = new SolidBrush(DawPalette.WithAlpha(accent, 170)))
            g.DrawString("RST", label, labelInk, box.X + 7, box.Y + 3);

        if (box.Height >= 30)
        {
            using var valueFont = new Font("Consolas", 9.5f, FontStyle.Bold);
            using var valueInk = new SolidBrush(accent);
            g.DrawString($"{order.RestartPosition:00}", valueFont, valueInk, box.X + 6, box.Y + 15);
        }

        int target = order.RestartPosition - 1;
        if (box.Height >= 48 && target >= 0 && target < order.Tokens.Count
            && order.Tokens[target] is PatternRefToken token)
        {
            using var targetFont = new Font("Consolas", 7f);
            using var targetInk = new SolidBrush(DawPalette.TextFaint);
            g.DrawString($"P{token.PatternNumber:00}", targetFont, targetInk, box.X + 7, box.Y + 34);
        }

        g.Clip = previous;
    }

    private void DrawClip(Graphics g, int patternNumber, Rectangle box, bool selected, bool hovered, bool playing, bool restart)
    {
        var colour = DawPalette.ForInstrument(patternNumber);
        var clipped = Rectangle.Intersect(box, new Rectangle(GutterWidth, 0, Width - GutterWidth, Height));
        if (clipped.Width <= 0)
            return;

        using (var fill = new SolidBrush(playing
                   ? DawPalette.WithAlpha(colour, 55)
                   : hovered ? Color.FromArgb(0x1B, 0x23, 0x33) : Color.FromArgb(0x14, 0x1B, 0x29)))
            g.FillRectangle(fill, clipped);

        // The pattern's own colour down the leading edge, which is what makes a repeated pattern
        // recognisable across all three lanes at a glance.
        if (box.X >= GutterWidth)
        {
            using var edge = new SolidBrush(colour);
            g.FillRectangle(edge, box.X, box.Y, 3, box.Height);
        }

        using (var border = new Pen(selected ? colour : DawPalette.BorderSoft, selected ? 1.6f : 1f))
            g.DrawRectangle(border, clipped.X, clipped.Y, clipped.Width - 1, clipped.Height - 1);

        if (clipped.Width > 34)
        {
            using var font = new Font("Consolas", 7.5f);
            using var ink = new SolidBrush(colour);
            g.DrawString($"{patternNumber:00}", font, ink, clipped.X + 7, clipped.Y + 4);
        }

        if (restart)
        {
            using var mark = new Pen(DawPalette.Effect, 1.6f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(mark, clipped.X, clipped.Y, clipped.Width - 1, clipped.Height - 1);
        }
    }
}
