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

    public ArrangeView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        BackColor = DawPalette.Background;
        TabStop = true;

        _scroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_scroll);

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

    private int RowsOf(int patternNumber) =>
        _song?.Patterns.FirstOrDefault(p => p.Number == patternNumber)?.Rows.Count ?? 0;

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

        int start = GutterWidth + OffsetOf(order, at.Index) - _scroll.Value;
        int rows = Math.Max(1, (int)Math.Round((x - start) / (double)PixelsPerRow));
        if (rows == pattern.Rows.Count)
        {
            return;
        }

        while (pattern.Rows.Count > rows)
        {
            pattern.Rows.RemoveAt(pattern.Rows.Count - 1);
        }

        while (pattern.Rows.Count < rows)
        {
            pattern.Rows.Add(new HoldRow());
        }

        _resizeChanged = true;
        UpdateScrollRange();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_resizing is not null)
        {
            DragClipEnd(e.X);
            base.OnMouseMove(e);
            return;
        }

        Cursor = EndHandleAt(e.Location) is not null ? Cursors.SizeWE : Cursors.Default;

        var at = ClipAt(e.Location) ?? (-1, -1);
        int restart = RestartAt(e.Location);
        if (at != _hovered || restart != _hoveredRestart)
        {
            _hovered = at;
            _hoveredRestart = restart;
            Cursor = at.Item2 >= 0 || restart >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = (-1, -1);
        _hoveredRestart = -1;
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

        if (e.Button == MouseButtons.Left && EndHandleAt(e.Location) is { } handle)
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

        if (ClipAt(e.Location) is { } at)
        {
            _selectedChannel = at.Channel;
            _selectedIndex = at.Index;
            _onRestart = false;
            Invalidate();
            AnnounceSelection();
        }
        else if (RestartAt(e.Location) is var restart and >= 0)
        {
            SelectRestart(restart);
        }

        base.OnMouseDown(e);
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
                // A channel with no entries at all cannot be assembled - Tables.s asserts it - so
                // the last clip in a lane refuses to go.
                if (order.Tokens.Count > 1)
                {
                    order.RemoveEntryAt(_selectedIndex);
                    _selectedIndex = Math.Min(_selectedIndex, order.Tokens.Count - 1);
                    Edited();
                }

                e.Handled = true;
                break;

            case Keys.Insert:
                if (order.Tokens[_selectedIndex] is PatternRefToken existing)
                {
                    order.InsertEntry(_selectedIndex + 1, existing.PatternNumber);
                    _selectedIndex++;
                    Edited();
                }

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
        }
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
                selected: channel == _selectedChannel && i == _selectedIndex && !_onRestart,
                hovered: _hovered == (channel, i),
                playing: _playheads[channel] == i,
                // Where the RST block points, picked out while it is pointed at - hovered or
                // selected - rather than always: a mark on the first clip read as happening there.
                restart: order.RestartPosition - 1 == i
                         && (_hoveredRestart == channel || (_onRestart && _selectedChannel == channel)));
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
