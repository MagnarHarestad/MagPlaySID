using MagSid.Core.Model;

namespace MagSid.Editor.App.Daw;

/// <summary>How the roll rules its columns.</summary>
internal enum RollGrid
{
    /// <summary>Four rows to a beat, sixteen to a bar, numbered like a DAW. Rows are ticks and not
    /// beats, so this is a convention laid over them rather than a fact about the song - but it is
    /// the convention anyone writing music already counts in, which is why it is the default.</summary>
    Beats,

    /// <summary>A line per row, numbered in hex as the tracker numbers them. Truthful: this is what
    /// a pattern actually is.</summary>
    Rows,
}

/// <summary>
/// One pattern as notes on a keyboard grid.
///
/// A note's width is how long it sounds. Hold rows are not drawn at all - a hold IS the length of
/// the note before it, which is the single biggest difference between this and the tracker, and the
/// reason every edit goes through <see cref="PatternNotes"/> rather than touching rows directly.
///
/// A release is drawn hollow on the lane of the note it follows: the voice is neither sounding nor
/// silent there, and a roll that showed nothing would make a long release look like a gap.
/// </summary>
internal sealed class PianoRollView : Control
{
    private const int KeyboardWidth = 72;
    private const int RulerHeight = 24;
    private const int HeaderHeight = 32;
    private const int KeyHeight = 15;
    private const int RowWidth = 22;

    private const int LowestOctave = 1;
    private const int HighestOctave = 6;

    private static readonly string[] PitchNames =
        ["B", "A#", "A", "G#", "G", "F#", "F", "E", "D#", "D", "C#", "C"];

    private readonly HScrollBar _scrollX = new() { Dock = DockStyle.Bottom, Height = 16 };
    private readonly VScrollBar _scrollY = new() { Dock = DockStyle.Right, Width = 16 };

    private Song? _song;
    private Pattern? _pattern;
    private int _channel = -1;
    private int _defaultInstrument = 1;
    private RollGrid _grid = RollGrid.Beats;

    private IReadOnlyList<NoteBlock> _notes = [];
    private IReadOnlyList<ReleaseBlock> _releases = [];

    private int _selectedRow = -1;
    private int _playRow = -1;
    private (int Row, int Key) _hovered = (-1, -1);

    public PianoRollView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        BackColor = DawPalette.Background;
        TabStop = true;

        _scrollX.Scroll += (_, _) => Invalidate();
        _scrollY.Scroll += (_, _) => Invalidate();
        Controls.Add(_scrollX);
        Controls.Add(_scrollY);
    }

    /// <summary>An edit landed. The host marks the song dirty, which is what captures the undo
    /// snapshot, and refreshes the tracker showing the same pattern.</summary>
    public event Action? Edited;

    /// <summary>Sound a note, so placing one is heard rather than only seen - the same audition the
    /// tracker gives when a note is typed.</summary>
    public event Action<NoteName, int>? NotePreviewRequested;

    /// <summary>The mouse came up on an auditioned note - gate off, so it releases instead of
    /// sustaining until the next one.</summary>
    public event Action<NoteName>? NotePreviewReleased;

    private NoteName? _sounding;

    private void Audition(NoteName note, int instrument)
    {
        ReleaseAudition();
        _sounding = note;
        NotePreviewRequested?.Invoke(note, instrument);
    }

    private void ReleaseAudition()
    {
        if (_sounding is not { } note)
            return;

        _sounding = null;
        NotePreviewReleased?.Invoke(note);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        ReleaseAudition();
        base.OnMouseUp(e);
    }

    /// <summary>Capture lost without a mouse-up - alt-tab mid-click, a dialog popping up. The note
    /// would otherwise hang until the next click.</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture)
            ReleaseAudition();

        base.OnMouseCaptureChanged(e);
    }

    /// <summary>Which ruling the roll is using. The toggle in the header sets it.</summary>
    public RollGrid Grid
    {
        get => _grid;
        set
        {
            if (_grid == value)
                return;

            _grid = value;
            Invalidate();
        }
    }

    public Pattern? Pattern => _pattern;

    public void LoadSong(Song song)
    {
        _song = song;

        // Keep showing the same pattern number across a reload - undo replaces the Song object, and
        // landing back on pattern 1 every time would be its own small bug.
        int wanted = _pattern?.Number ?? 1;
        ShowPattern(wanted);
    }

    /// <summary>Shows a pattern. <paramref name="channel"/> is the arrangement lane it was picked
    /// from, for the header only - the same pattern can sit on all three.</summary>
    public void ShowPattern(int patternNumber, int channel)
    {
        _channel = channel;
        ShowPattern(patternNumber);
    }

    public void ShowPattern(int patternNumber)
    {
        _pattern = _song?.Patterns.FirstOrDefault(p => p.Number == patternNumber)
                   ?? _song?.Patterns.FirstOrDefault();
        _selectedRow = -1;
        Reread();
        UpdateScrollRange();
        Invalidate();
    }

    public void SetDefaultInstrument(int instrumentNumber) => _defaultInstrument = instrumentNumber;

    /// <summary>Re-voices the selected note with another instrument, keeping its pitch and length,
    /// and auditions it so the change is heard. Nothing selected, nothing happens.</summary>
    public void SetSelectedNoteInstrument(int instrumentNumber)
    {
        if (_pattern is not { } pattern || _selectedRow < 0 || _selectedRow >= pattern.Rows.Count
            || pattern.Rows[_selectedRow] is not NoteRow note || note.InstrumentNumber == instrumentNumber)
            return;

        pattern.Rows[_selectedRow] = note with { InstrumentNumber = instrumentNumber };
        Commit();

        // A click in a list has no mouse-up here to end the audition, so it is released straight
        // away - JamEngine still lets the WAVE program play through once before the gate closes.
        Audition(note.Note, instrumentNumber);
        ReleaseAudition();
    }

    /// <summary>Keep the playhead in view while the song plays. Turning it off leaves the roll
    /// wherever it is, so a pattern can be edited while the tune carries on.</summary>
    public bool AutoScroll { get; set; } = true;

    /// <summary>Which row is sounding, or -1.</summary>
    public void SetPlayRow(int row)
    {
        // Every poll, not only when the row changes: the vertical centring eases toward its target
        // over several frames and needs the ticks in between to get there.
        int previous = _playRow;
        _playRow = row;

        bool scrolled = AutoScroll && row >= 0 && (FollowPlayRow() | CenterOnNotes());

        if (previous != row || scrolled)
            Invalidate();
    }

    /// <summary>
    /// Autoscroll's vertical half: keeps the notes on screen in the middle of the roll. The target
    /// is the middle of their range when that range fits, so the highest and lowest notes sit an
    /// equal distance from the edges; a range too tall to fit centres on the duration-weighted
    /// average instead, which is where the melody actually spends its time. Only notes in the
    /// visible columns count, so a pattern that climbs is followed page by page rather than
    /// averaged into one fixed position. The scroll eases a third of the way there per tick -
    /// a jump every page turn would make the keyboard hard to read.
    /// </summary>
    private bool CenterOnNotes()
    {
        if (!_scrollY.Enabled || _notes.Count == 0)
            return false;

        int firstRow = _scrollX.Value / RowWidth;
        int lastRow = (_scrollX.Value + Math.Max(1, Width - KeyboardWidth - _scrollY.Width)) / RowWidth;

        var visible = _notes.Where(n => n.Row <= lastRow && n.Row + n.Length > firstRow && KeyForNote(n.Note) >= 0).ToList();
        if (visible.Count == 0)
            visible = _notes.Where(n => KeyForNote(n.Note) >= 0).ToList();
        if (visible.Count == 0)
            return false;

        int top = visible.Min(n => KeyForNote(n.Note));
        int bottom = visible.Max(n => KeyForNote(n.Note));
        int visibleHeight = Math.Max(1, Height - GridTop - _scrollX.Height);

        double centreKey = (bottom - top + 1) * KeyHeight <= visibleHeight
            ? (top + bottom) / 2.0
            : visible.Sum(n => (double)KeyForNote(n.Note) * n.Length) / visible.Sum(n => n.Length);

        int max = Math.Max(0, _scrollY.Maximum - _scrollY.LargeChange + 1);
        int target = Math.Clamp((int)(((centreKey + 0.5) * KeyHeight) - (visibleHeight / 2.0)), 0, max);

        int distance = target - _scrollY.Value;
        if (distance == 0)
            return false;

        int step = Math.Abs(distance) <= 2 ? distance : (int)Math.Round(distance / 3.0);
        _scrollY.Value = Math.Clamp(_scrollY.Value + step, 0, max);
        return true;
    }

    /// <summary>Turns a page rather than sliding every row: the notes hold still to be read and
    /// the view jumps once the playhead reaches the right edge - or has fallen off the left one,
    /// as it does when the next pattern starts.</summary>
    private bool FollowPlayRow()
    {
        if (!_scrollX.Enabled)
            return false;

        int x = _playRow * RowWidth;
        int visible = Math.Max(1, Width - KeyboardWidth - _scrollY.Width);
        int max = Math.Max(0, _scrollX.Maximum - _scrollX.LargeChange + 1);

        if (x >= _scrollX.Value && x <= _scrollX.Value + visible - (RowWidth * 2))
            return false;

        _scrollX.Value = Math.Clamp(x - (RowWidth * 2), 0, max);
        return true;
    }

    private void Reread()
    {
        if (_pattern is { } pattern)
        {
            _notes = PatternNotes.Read(pattern);
            _releases = PatternNotes.Releases(pattern);
        }
        else
        {
            _notes = [];
            _releases = [];
        }
    }

    private static int KeyCount => (HighestOctave - LowestOctave + 1) * 12;

    /// <summary>Key 0 is the top lane, the highest note. Screens grow downward and pitch does not.</summary>
    private static NoteName NoteForKey(int key)
    {
        int fromTop = Math.Clamp(key, 0, KeyCount - 1);
        int octave = HighestOctave - (fromTop / 12);
        string pitch = PitchNames[fromTop % 12];
        return NoteName.Parse(pitch + (pitch.Length == 1 ? "-" : string.Empty) + octave);
    }

    private static int KeyForNote(NoteName note)
    {
        int index = Array.IndexOf(PitchNames, note.IsSharp ? note.Letter + "#" : note.Letter.ToString());
        if (index < 0)
            return -1;

        int key = ((HighestOctave - note.Octave) * 12) + index;
        return key >= 0 && key < KeyCount ? key : -1;
    }

    private void UpdateScrollRange()
    {
        int rows = _pattern?.Rows.Count ?? 0;
        int visibleWidth = Math.Max(1, Width - KeyboardWidth - _scrollY.Width);
        int visibleHeight = Math.Max(1, Height - RulerHeight - HeaderHeight - _scrollX.Height);

        _scrollX.Minimum = 0;
        _scrollX.Maximum = Math.Max(0, (rows * RowWidth) + 40);
        _scrollX.LargeChange = visibleWidth;
        _scrollX.SmallChange = RowWidth * 4;
        _scrollX.Enabled = rows * RowWidth > visibleWidth;

        _scrollY.Minimum = 0;
        _scrollY.Maximum = Math.Max(0, (KeyCount * KeyHeight) + 20);
        _scrollY.LargeChange = visibleHeight;
        _scrollY.SmallChange = KeyHeight * 4;
        _scrollY.Enabled = KeyCount * KeyHeight > visibleHeight;

        // Open on the octaves a C64 tune actually lives in rather than the very top.
        if (_scrollY.Enabled && _scrollY.Value == 0)
            _scrollY.Value = Math.Min(_scrollY.Maximum - _scrollY.LargeChange + 1, 14 * KeyHeight);
    }

    protected override void OnResize(EventArgs e)
    {
        UpdateScrollRange();
        base.OnResize(e);
    }

    private int GridTop => RulerHeight + HeaderHeight;

    private (int Row, int Key)? CellAt(Point point)
    {
        if (_pattern is null || point.X < KeyboardWidth || point.Y < GridTop)
            return null;

        int row = (point.X - KeyboardWidth + _scrollX.Value) / RowWidth;
        int key = (point.Y - GridTop + _scrollY.Value) / KeyHeight;

        return row >= 0 && row < _pattern.Rows.Count && key >= 0 && key < KeyCount ? (row, key) : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = CellAt(e.Location) ?? (-1, -1);
        if (at != _hovered)
        {
            _hovered = at;
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

        // The header's grid toggle.
        if (e.Y < RulerHeight + HeaderHeight && e.Y >= RulerHeight && GridToggleBounds.Contains(e.Location))
        {
            Grid = _grid == RollGrid.Beats ? RollGrid.Rows : RollGrid.Beats;
            return;
        }

        if (_pattern is not { } pattern || CellAt(e.Location) is not { } cell)
            return;

        if (e.Button == MouseButtons.Right)
        {
            PatternNotes.Remove(pattern, cell.Row);
            Commit();
            return;
        }

        var existing = _notes.FirstOrDefault(n => cell.Row >= n.Row && cell.Row < n.Row + n.Length);
        if (existing is not null && KeyForNote(existing.Note) == cell.Key)
        {
            _selectedRow = existing.Row;
            Audition(existing.Note, existing.InstrumentNumber);
            Invalidate();
            return;
        }

        var placed = NoteForKey(cell.Key);
        PatternNotes.SetNote(pattern, cell.Row, placed, _defaultInstrument);
        _selectedRow = cell.Row;
        Audition(placed, _defaultInstrument);
        Commit();

        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Shift scrolls sideways, as it does everywhere else. Typed as the base class because
        // HScrollBar and VScrollBar share one only through it.
        ScrollBar bar = ModifierKeys.HasFlag(Keys.Shift) ? _scrollX : _scrollY;
        if (!bar.Enabled)
            return;

        int step = bar.SmallChange * 2 * Math.Sign(-e.Delta);
        bar.Value = Math.Clamp(bar.Value + step, bar.Minimum, Math.Max(0, bar.Maximum - bar.LargeChange + 1));
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Delete;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_pattern is not { } pattern || _selectedRow < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        var note = _notes.FirstOrDefault(n => n.Row == _selectedRow);
        if (note is null)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Delete:
                PatternNotes.Remove(pattern, _selectedRow);
                _selectedRow = -1;
                Commit();
                e.Handled = true;
                break;

            case Keys.Right when e.Control:
                PatternNotes.SetLength(pattern, _selectedRow, note.Length + 1);
                Commit();
                e.Handled = true;
                break;

            case Keys.Left when e.Control:
                PatternNotes.SetLength(pattern, _selectedRow, note.Length - 1);
                Commit();
                e.Handled = true;
                break;

            case Keys.Up or Keys.Down:
            {
                int key = KeyForNote(note.Note);
                if (key >= 0)
                {
                    PatternNotes.Move(pattern, _selectedRow, _selectedRow,
                        NoteForKey(key + (e.KeyCode == Keys.Up ? -1 : 1)));
                    Commit();
                }

                e.Handled = true;
                break;
            }

            case Keys.Left or Keys.Right:
            {
                int target = _selectedRow + (e.KeyCode == Keys.Left ? -1 : 1);
                PatternNotes.Move(pattern, _selectedRow, target, note.Note);
                _selectedRow = target;
                Commit();
                e.Handled = true;
                break;
            }
        }

        base.OnKeyDown(e);
    }

    private void Commit()
    {
        Reread();
        Invalidate();
        Edited?.Invoke();
    }

    private Rectangle GridToggleBounds => new(Width - _scrollY.Width - 168, RulerHeight + 6, 156, 20);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(DawPalette.Background);

        DrawRulerAndHeader(g);

        if (_pattern is null)
        {
            using var font = new Font("Segoe UI", 10f);
            using var ink = new SolidBrush(DawPalette.TextFaint);
            g.DrawString("No pattern", font, ink, KeyboardWidth + 20, GridTop + 20);
            return;
        }

        DrawLanes(g);
        DrawColumns(g);
        DrawBlocks(g);
        DrawKeyboard(g);
        DrawPlayhead(g);
    }

    private void DrawRulerAndHeader(Graphics g)
    {
        using (var fill = new SolidBrush(DawPalette.PanelHeader))
            g.FillRectangle(fill, 0, 0, Width, RulerHeight + HeaderHeight);
        using (var rule = new Pen(DawPalette.Border))
        {
            g.DrawLine(rule, 0, RulerHeight, Width, RulerHeight);
            g.DrawLine(rule, 0, RulerHeight + HeaderHeight, Width, RulerHeight + HeaderHeight);
        }

        using (var font = new Font("Segoe UI", 7.5f, FontStyle.Bold))
        using (var ink = new SolidBrush(DawPalette.Oscillator))
            g.DrawString(_pattern is { } p ? $"PAT{p.Number:00}" : "—", font, ink, 12, 6);

        if (_pattern is { } pattern)
        {
            using var metaFont = new Font("Consolas", 7.5f);
            using var metaInk = new SolidBrush(DawPalette.TextFaint);
            string channel = _channel >= 0 ? $"CH {_channel + 1} · " : string.Empty;
            g.DrawString($"{channel}{pattern.Rows.Count} rows · {_notes.Count} notes", metaFont, metaInk, 80, 6);
        }

        DrawGridToggle(g);
        DrawRulerNumbers(g);
    }

    /// <summary>The grid switch. Two labelled halves rather than a checkbox, because the choice is
    /// between two things and neither is "off".</summary>
    private void DrawGridToggle(Graphics g)
    {
        var box = GridToggleBounds;

        using (var fill = new SolidBrush(Color.FromArgb(0x10, 0x14, 0x1E)))
            g.FillRectangle(fill, box);
        using (var border = new Pen(DawPalette.Border))
            g.DrawRectangle(border, box);

        using var font = new Font("Segoe UI", 7f, FontStyle.Bold);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        foreach (var (mode, label, index) in new[] { (RollGrid.Beats, "BEATS", 0), (RollGrid.Rows, "ROWS", 1) })
        {
            var half = new Rectangle(box.X + 1 + (index * (box.Width / 2)), box.Y + 1, (box.Width / 2) - 1, box.Height - 2);
            bool on = _grid == mode;

            if (on)
            {
                using var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Oscillator, 45));
                g.FillRectangle(fill, half);
            }

            using var ink = new SolidBrush(on ? DawPalette.Oscillator : DawPalette.TextDim);
            g.DrawString(label, font, ink, half, centred);
        }

        using var hintFont = new Font("Segoe UI", 6.5f);
        using var hintInk = new SolidBrush(DawPalette.TextFaint);
        var far = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(
            _grid == RollGrid.Beats ? "16 rows to a bar" : "one line per row, hex",
            hintFont, hintInk, new RectangleF(0, box.Bottom - 34, box.X - 8, 12), far);
    }

    private void DrawRulerNumbers(Graphics g)
    {
        if (_pattern is not { } pattern)
            return;

        using var font = new Font("Consolas", 7f);
        using var strong = new SolidBrush(DawPalette.TextDim);
        using var tick = new Pen(DawPalette.BorderSoft);

        int every = _grid == RollGrid.Beats ? 16 : 4;

        for (int row = 0; row < pattern.Rows.Count; row += every)
        {
            int x = KeyboardWidth + (row * RowWidth) - _scrollX.Value;
            if (x < KeyboardWidth || x > Width)
                continue;

            g.DrawLine(tick, x, 6, x, RulerHeight);
            string label = _grid == RollGrid.Beats ? ((row / 16) + 1).ToString() : row.ToString("X2");
            g.DrawString(label, font, strong, x + 3, 5);
        }
    }

    private void DrawLanes(Graphics g)
    {
        int top = GridTop;
        int bottom = Height - _scrollX.Height;

        for (int key = 0; key < KeyCount; key++)
        {
            int y = top + (key * KeyHeight) - _scrollY.Value;
            if (y + KeyHeight < top || y > bottom)
                continue;

            bool black = PitchNames[key % 12].Contains('#');
            using var fill = new SolidBrush(black
                ? Color.FromArgb(0x0D, 0x11, 0x19)
                : Color.FromArgb(0x11, 0x16, 0x1F));
            g.FillRectangle(fill, KeyboardWidth, y, Width - KeyboardWidth - _scrollY.Width, KeyHeight);

            using var line = new Pen(Color.FromArgb(0x12, 0x17, 0x24));
            g.DrawLine(line, KeyboardWidth, y + KeyHeight, Width - _scrollY.Width, y + KeyHeight);
        }
    }

    private void DrawColumns(Graphics g)
    {
        if (_pattern is not { } pattern)
            return;

        int top = GridTop;
        int bottom = Height - _scrollX.Height;

        for (int row = 0; row <= pattern.Rows.Count; row++)
        {
            int x = KeyboardWidth + (row * RowWidth) - _scrollX.Value;
            if (x < KeyboardWidth || x > Width - _scrollY.Width)
                continue;

            Color colour = _grid == RollGrid.Beats
                ? row % 16 == 0 ? Color.FromArgb(0x2C, 0x38, 0x52)
                    : row % 4 == 0 ? Color.FromArgb(0x1C, 0x24, 0x36) : Color.FromArgb(0x14, 0x1A, 0x27)
                : row % 4 == 0 ? Color.FromArgb(0x22, 0x2C, 0x42) : Color.FromArgb(0x17, 0x1E, 0x2C);

            using var pen = new Pen(colour);
            g.DrawLine(pen, x, top, x, bottom);
        }

        // Loop markers occupy a row of their own and stop a note reaching past them, so they are
        // drawn the full height rather than as a note-sized block.
        for (int row = 0; row < pattern.Rows.Count; row++)
        {
            if (pattern.Rows[row] is not (LoopStartRow or LoopEndRow))
                continue;

            int x = KeyboardWidth + (row * RowWidth) - _scrollX.Value;
            if (x < KeyboardWidth || x > Width - _scrollY.Width)
                continue;

            bool start = pattern.Rows[row] is LoopStartRow;
            using var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 30));
            g.FillRectangle(fill, x, top, RowWidth, bottom - top);

            using var pen = new Pen(DawPalette.Effect);
            g.DrawLine(pen, x, top, x, bottom);

            using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
            using var ink = new SolidBrush(DawPalette.Effect);
            g.DrawString(start ? "LS" : "LE", font, ink, x + 2, top + 3);
        }
    }

    private void DrawBlocks(Graphics g)
    {
        int top = GridTop;

        foreach (var release in _releases)
        {
            var box = BlockBounds(release.Row, release.Length, KeyOfPrecedingNote(release.Row));
            if (box is not { } bounds)
                continue;

            using var pen = new Pen(DawPalette.WithAlpha(DawPalette.TextDim, 170)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawRectangle(pen, bounds);
        }

        foreach (var note in _notes)
        {
            int key = KeyForNote(note.Note);
            if (key < 0)
                continue;

            var box = BlockBounds(note.Row, note.Length, key);
            if (box is not { } bounds)
                continue;

            var colour = DawPalette.ForInstrument(note.InstrumentNumber);
            bool selected = note.Row == _selectedRow;

            using (var fill = new SolidBrush(selected ? ControlPaint.Light(colour, 0.3f) : colour))
                g.FillRectangle(fill, bounds);
            using (var border = new Pen(selected ? Color.White : ControlPaint.Dark(colour, 0.3f)))
                g.DrawRectangle(border, bounds);

            if (bounds.Width > 30)
            {
                using var font = new Font("Consolas", 6.75f, FontStyle.Bold);
                using var ink = new SolidBrush(Color.FromArgb(0x0B, 0x0E, 0x15));
                g.DrawString(note.Note.ToString(), font, ink, bounds.X + 3, bounds.Y + 1);
            }
        }

        _ = top;
    }

    /// <summary>A release belongs on the lane of whatever it is releasing, which is the last note
    /// before it. Nothing before it means nothing is sounding, and it is not drawn.</summary>
    private int KeyOfPrecedingNote(int row)
    {
        NoteBlock? previous = null;
        foreach (var note in _notes)
        {
            if (note.Row < row)
                previous = note;
        }

        return previous is null ? -1 : KeyForNote(previous.Note);
    }

    private Rectangle? BlockBounds(int row, int length, int key)
    {
        if (key < 0)
            return null;

        int x = KeyboardWidth + (row * RowWidth) - _scrollX.Value;
        int y = GridTop + (key * KeyHeight) - _scrollY.Value;
        int width = (length * RowWidth) - 2;
        int height = KeyHeight - 2;

        if (x + width < KeyboardWidth || x > Width - _scrollY.Width
            || y + height < GridTop || y > Height - _scrollX.Height)
            return null;

        return new Rectangle(x + 1, y + 1, Math.Max(3, width), height);
    }

    private void DrawKeyboard(Graphics g)
    {
        int top = GridTop;
        int bottom = Height - _scrollX.Height;

        using (var fill = new SolidBrush(Color.FromArgb(0x10, 0x14, 0x1E)))
            g.FillRectangle(fill, 0, top, KeyboardWidth, bottom - top);

        using var font = new Font("Consolas", 6.75f);

        for (int key = 0; key < KeyCount; key++)
        {
            int y = top + (key * KeyHeight) - _scrollY.Value;
            if (y + KeyHeight < top || y > bottom)
                continue;

            string pitch = PitchNames[key % 12];
            bool black = pitch.Contains('#');

            using (var fill = new SolidBrush(black ? Color.FromArgb(0x18, 0x1E, 0x2C) : Color.FromArgb(0xC0, 0xC6, 0xD4)))
                g.FillRectangle(fill, 2, y + 1, KeyboardWidth - 6, KeyHeight - 2);

            if (pitch == "C")
            {
                using var ink = new SolidBrush(Color.FromArgb(0x10, 0x14, 0x1E));
                var far = new StringFormat { Alignment = StringAlignment.Far };
                g.DrawString(NoteForKey(key).ToString(), font, ink,
                    new RectangleF(0, y + 1, KeyboardWidth - 9, KeyHeight), far);
            }
        }

        using var border = new Pen(DawPalette.Border);
        g.DrawLine(border, KeyboardWidth, top, KeyboardWidth, bottom);
    }

    private void DrawPlayhead(Graphics g)
    {
        if (_playRow < 0)
            return;

        int x = KeyboardWidth + (_playRow * RowWidth) - _scrollX.Value;
        if (x < KeyboardWidth || x > Width - _scrollY.Width)
            return;

        using var pen = new Pen(Color.FromArgb(0xF0, 0x4A, 0x6E), 1.5f);
        g.DrawLine(pen, x, GridTop, x, Height - _scrollX.Height);
    }
}
