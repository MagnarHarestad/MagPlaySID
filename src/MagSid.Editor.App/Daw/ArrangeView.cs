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
    private const int HintHeight = 26;
    private const int ScrollHeight = 12;

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

    public ArrangeView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        BackColor = DawPalette.Background;
        TabStop = true;

        _scroll.Scroll += (_, _) => Invalidate();
        Controls.Add(_scroll);
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
        Invalidate();
    }

    /// <summary>Autoscroll: moves the selection to whatever the selected channel is playing, which
    /// takes the roll below along with it, and scrolls the lanes so that clip is in view.</summary>
    public void FollowPlayhead()
    {
        if (_song is not { } song)
            return;

        int playing = _playheads[_selectedChannel];
        if (playing < 0 || playing == _selectedIndex
            || playing >= song.ChannelOrders[_selectedChannel].Tokens.Count)
            return;

        _selectedIndex = playing;
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = ClipAt(e.Location) ?? (-1, -1);
        if (at != _hovered)
        {
            _hovered = at;
            Cursor = at.Item2 >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = (-1, -1);
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

        if (ClipAt(e.Location) is { } at)
        {
            _selectedChannel = at.Channel;
            _selectedIndex = at.Index;
            Invalidate();
            AnnounceSelection();
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
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Delete or Keys.Insert
        || (keyData & Keys.KeyCode) is Keys.Left or Keys.Right;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_song is not { } song || _selectedIndex < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var order = song.ChannelOrders[_selectedChannel];

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

            case Keys.Right:
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
            // the file format writes it. The lane already draws it as a loop sign on the entry it
            // points at, which is the feedback this needs.
            case Keys.R when e.Control:
                order.RestartPosition = _selectedIndex + 1;
                Edited();
                e.Handled = true;
                break;
        }

        AnnounceSelection();
        base.OnKeyDown(e);
    }

    private void ClampSelection()
    {
        if (_song is not { } song)
            return;

        int count = song.ChannelOrders[_selectedChannel].Tokens.Count;
        _selectedIndex = count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, count - 1);
        Invalidate();
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

        DrawHint(g);
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
                selected: channel == _selectedChannel && i == _selectedIndex,
                hovered: _hovered == (channel, i),
                playing: _playheads[channel] == i,
                restart: order.RestartPosition - 1 == i);
        }

        // The end of the list, so a lane that stops early is obvious rather than just empty.
        int endX = GutterWidth + LaneLength(order) - _scroll.Value;
        if (endX >= GutterWidth && endX <= Width)
        {
            using var endPen = new Pen(DawPalette.WithAlpha(DawPalette.TextFaint, 120)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawLine(endPen, endX, top + 4, endX, top + LaneHeight - 4);
        }
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

        if (restart && clipped.Width > 12)
        {
            using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
            using var ink = new SolidBrush(DawPalette.Effect);
            g.DrawString("↻", font, ink, clipped.Right - 14, clipped.Y + 3);
        }
    }

    private void DrawHint(Graphics g)
    {
        int y = LaneTop(Song.ChannelCount) + 8;
        if (y > Height - 20)
            return;

        using var font = new Font("Segoe UI", 7.5f);
        using var ink = new SolidBrush(DawPalette.TextFaint);
        g.DrawString(
            "Click a clip to edit it in the roll below  ·  Ctrl+←→ moves it  ·  Insert duplicates  ·  Delete removes  ·  Ctrl+R restarts here  ·  click a channel name to mute  ·  "
            + "↻ marks where the channel restarts",
            font, ink, 12, y);
    }
}
