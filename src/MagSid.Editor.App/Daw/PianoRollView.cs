using System.Drawing.Drawing2D;
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
    // One strip above the beat bar, not two: the pattern's name, what it holds, the bar numbers,
    // the ruling and the switch between rulings all fit on a line, and the second line was mostly
    // air taken off the roll.
    private const int RulerHeight = 26;
    private const int HeaderHeight = 0;
    private const int KeyHeight = 15;
    private const int RowWidth = 22;

    // The note table's whole range, C-0..B-7. Drums sit at the very ends - SOS's kick is a C-0 and
    // its snare a C-7, their pitch coming from the note program - and a roll that stopped short of
    // them drew those rows as if they were empty.
    private const int LowestOctave = 0;
    private const int HighestOctave = 7;

    /// <summary>Held notes, for the keyboard strip. A set rather than one note: three can sound
    /// at once, which is what the chip has.</summary>
    private readonly HashSet<NoteName> _heldKeys = [];

    private static readonly Color SoundingKey = DawPalette.Oscillator;

    private static readonly string[] PitchNames =
        ["B", "A#", "A", "G#", "G", "F#", "F", "E", "D#", "D", "C#", "C"];

    private readonly SlimScrollBar _scrollX = new(Orientation.Horizontal) { Dock = DockStyle.Bottom, Height = 12 };
    private readonly SlimScrollBar _scrollY = new(Orientation.Vertical) { Dock = DockStyle.Right, Width = 12 };

    private Song? _song;
    private Pattern? _pattern;
    private int _channel = -1;

    // Loops drawn unrolled: a column per tick, a loop's later passes as linked copies of the
    // first - the timeline Retro and Arrange draw too. _columnInfo says which row and pass each
    // column is; _playTicks / _recordTicks are where the channel is in ticks, which is the column.
    private bool _unrollLoops = true;
    private PatternLoops.UnrolledRow[] _columnInfo = [];
    private double _playTicks = -1;
    private double _recordTicks = -1;

    /// <summary>The pass of the column last clicked - a later pass is the first again, so what
    /// changes the rows themselves is refused there.</summary>
    private int _selectedPass = 1;

    /// <summary>A short message for the status bar - something refused, and why.</summary>
    public event Action<string>? Notice;

    public bool UnrollLoops
    {
        get => _unrollLoops;
        set
        {
            if (_unrollLoops == value)
                return;

            _unrollLoops = value;
            Reread();
            UpdateScrollRange();
            Invalidate();
        }
    }

    // Recording: the pattern the take goes into, the row the recording channel is at (fed only
    // while that channel plays it), the key being held and where it went down, and whether the
    // take has written anything - committed as one edit, one undo step, when recording stops.
    private Pattern? _recordPattern;
    private int _recordRow = -1;
    private double _recordFraction;
    private (NoteName Note, int Row, int Column)? _recordHeld;
    private bool _takeWritten;
    private int _defaultInstrument = 1;
    private RollGrid _grid = RollGrid.Beats;

    private IReadOnlyList<NoteBlock> _notes = [];

    // The slide lane: the rate in force on every row, and which rows can take one at all.
    private int[] _slideRates = [];
    private bool[] _slidable = [];

    // A stroke being drawn in the slide lane: column -> rate, written when the button comes up.
    private Dictionary<int, int>? _slideStroke;

    // The slide lane's selection under Object Selection: a run of columns, both ends included,
    // and the column a drag started on (-1 when none is under way).
    private (int From, int To)? _slideSelection;
    private int _slideSelectAnchor = -1;
    private (int Column, int Rate) _slideStrokeLast;
    private (int Column, int Rate)? _slideHover;
    private IReadOnlyList<ReleaseBlock> _releases = [];

    private RollTool _tool = RollTool.Draw;

    /// <summary>The roll wants another tool - Ctrl+A asks for Object Selection, since a selection
    /// is only acted on there. The tool row owns which tool is chosen, so it is asked rather than
    /// told, and answers by setting <see cref="Tool"/>.</summary>
    public event Action<RollTool>? ToolRequested;

    // Object selection: the notes chosen, a rubber band being dragged, and a move in progress
    // measured in beat columns and keys from where the drag began.
    private readonly HashSet<int> _selection = [];
    private Point? _bandStart;
    private Rectangle _band;
    private bool _moving;
    private int _moveFromColumn;
    private int _moveFromKey;
    private int _moveColumns;
    private int _moveKeys;

    // The line tool's anchor cell and where the pointer is now.
    private (int Row, int Key)? _lineStart;
    private Point _linePointer;

    // An erase drag that has taken at least one note, and so owes an edit on release.
    private bool _erasing;
    private bool _erased;

    /// <summary>What a click in the grid does. Changing tool drops the selected notes: a selection
    /// carried into a tool that does not act on one would leave keys acting on something no
    /// longer meant.</summary>
    public RollTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            _lineStart = null;
            _bandStart = null;
            _selection.Clear();
            _slideSelection = null;
            Cursor = CursorFor(value);
            Invalidate();
        }
    }

    private static Cursor CursorFor(RollTool tool) => tool switch
    {
        RollTool.Select => Cursors.Default,
        RollTool.Erase => Cursors.No,
        RollTool.Trim => Cursors.SizeWE,
        RollTool.Split => Cursors.VSplit,
        RollTool.Glue => Cursors.Hand,
        _ => Cursors.Cross,
    };

    /// <summary>The beat column under an x position, unclamped.</summary>
    private int ColumnAtX(int x) => (x - KeyboardWidth + _scrollX.Value) / RowWidth;

    /// <summary>The column boundary nearest an x position - where the trim and split tools cut,
    /// since a cut goes between beats rather than through one.</summary>
    private int BoundaryAtX(int x) =>
        (int)Math.Round((x - KeyboardWidth + _scrollX.Value) / (double)RowWidth);

    /// <summary>The note whose block is under a cell, on its own key lane - a click beside a note
    /// at a different pitch is not a click on it.</summary>
    private NoteBlock? NoteAt((int Row, int Key) cell) =>
        _notes.FirstOrDefault(n => cell.Row >= n.Row && cell.Row < n.Row + n.Length && KeyForNote(n.Note) == cell.Key);

    /// <summary>Everything a left click does under a tool other than Draw. Returns true when the
    /// tool took the click - every tool does, so the drawing behaviour never leaks into another.
    /// </summary>
    private bool ToolMouseDown(MouseEventArgs e, Pattern pattern, (int Row, int Key) cell)
    {
        var note = NoteAt(cell);

        switch (_tool)
        {
            case RollTool.Select:
                // A note's end is still its handle under the selection tool, as in Cubase - and
                // grabbing the end of a selected note stretches every selected note by the same
                // number of beats.
                if (note is not null && OverEndHandle(note, e.X))
                {
                    BeginResize(note, together: _selection.Contains(note.Row));
                    return true;
                }

                if (note is null)
                {
                    if (!ModifierKeys.HasFlag(Keys.Control))
                        _selection.Clear();

                    _bandStart = e.Location;
                    _band = new Rectangle(e.Location, Size.Empty);
                    Capture = true;
                    Invalidate();
                    return true;
                }

                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    if (!_selection.Remove(note.Row))
                        _selection.Add(note.Row);

                    Invalidate();
                    return true;
                }

                if (!_selection.Contains(note.Row))
                {
                    _selection.Clear();
                    _selection.Add(note.Row);
                }

                _selectedRow = note.Row;
                Audition(note.Note, note.InstrumentNumber);
                _moving = true;
                _moveFromColumn = ColumnAtX(e.X);
                _moveFromKey = cell.Key;
                _moveColumns = _moveKeys = 0;
                Capture = true;
                Invalidate();
                return true;

            case RollTool.Erase:
                _erasing = true;
                _erased = false;
                Capture = true;
                EraseUnder(pattern, cell);
                return true;

            case RollTool.Trim:
                // Everything after the boundary nearest the click goes, and a release stops the
                // voice there. A note is never trimmed to nothing.
                if (note is not null
                    && PatternNotes.SetLength(pattern, note.Row, Math.Max(1, BoundaryAtX(e.X) - ColumnOf(note.Row))))
                {
                    Commit();
                }

                return true;

            case RollTool.Split:
                if (note is not null)
                {
                    int boundary = BoundaryAtX(e.X);
                    int start = ColumnOf(note.Row);
                    if (boundary > start && boundary < start + note.Length
                        && PatternNotes.Split(pattern, note.Row, RowOf(boundary)))
                    {
                        Commit();
                    }
                }

                return true;

            case RollTool.Glue:
                if (note is not null && PatternNotes.Glue(pattern, note.Row))
                    Commit();

                return true;

            case RollTool.Line:
                _lineStart = cell;
                _linePointer = e.Location;
                Capture = true;
                Invalidate();
                return true;
        }

        return false;
    }

    private void EraseUnder(Pattern pattern, (int Row, int Key) cell)
    {
        if (NoteAt(cell) is not { } note || !PatternNotes.Erase(pattern, note.Row))
            return;

        _erased = true;
        _selection.Remove(note.Row);
        Reread();
        Invalidate();
    }

    private int KeyAtY(int y) => Math.Clamp((y - GridTop + _scrollY.Value) / KeyHeight, 0, KeyCount - 1);

    /// <summary>A drag under one of the tools. Returns true when one was in progress.</summary>
    private bool ToolMouseMove(MouseEventArgs e)
    {
        if (_bandStart is { } start)
        {
            _band = Rectangle.FromLTRB(
                Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
            Invalidate();
            return true;
        }

        if (_moving)
        {
            int columns = ColumnAtX(e.X) - _moveFromColumn;
            int keys = KeyAtY(e.Y) - _moveFromKey;
            if (columns != _moveColumns || keys != _moveKeys)
            {
                bool pitchChanged = keys != _moveKeys;
                _moveColumns = columns;
                _moveKeys = keys;

                // One note dragged to a new pitch sounds at that pitch, as the key for it would -
                // so the ear follows the hand. Not a whole selection: a chord shape moved as one
                // is judged by where it lands, and three voices restarting on every step is noise.
                if (pitchChanged && _selection.Count == 1)
                    AuditionMovedNote();

                Invalidate();
            }

            return true;
        }

        if (_erasing)
        {
            if (_pattern is { } pattern && CellAt(e.Location) is { } cell)
                EraseUnder(pattern, cell);

            return true;
        }

        if (_lineStart is not null)
        {
            _linePointer = e.Location;
            Invalidate();
            return true;
        }

        return false;
    }

    /// <summary>Ends whatever drag a tool started, and makes it one edit.</summary>
    private void ToolMouseUp(MouseEventArgs e)
    {
        if (_bandStart is not null)
        {
            _bandStart = null;
            foreach (var note in _notes)
            {
                if (BlockBounds(note.Row, note.Length, KeyForNote(note.Note)) is { } bounds && bounds.IntersectsWith(_band))
                    _selection.Add(note.Row);
            }

            _band = Rectangle.Empty;
            Capture = false;
            Invalidate();
        }

        if (_moving)
        {
            _moving = false;
            Capture = false;
            if ((_moveColumns != 0 || _moveKeys != 0) && _pattern is { } pattern)
                ApplyMove(pattern);

            _moveColumns = _moveKeys = 0;
            Invalidate();
        }

        if (_erasing)
        {
            _erasing = false;
            Capture = false;
            if (_erased)
            {
                _erased = false;
                Commit();
            }
        }

        if (_lineStart is { } start && _pattern is { } drawn && ColumnCount > 0)
        {
            _lineStart = null;
            Capture = false;

            int endRow = RowOf(Math.Clamp(ColumnAtX(e.X), 0, ColumnCount - 1));
            PatternNotes.DrawLine(drawn, start.Row, NoteForKey(start.Key), endRow, NoteForKey(KeyAtY(e.Y)), _defaultInstrument);
            Commit();
        }
    }

    /// <summary>Plays the one note being dragged at the pitch it would land on now.</summary>
    private void AuditionMovedNote()
    {
        int row = _selection.First();
        if (_notes.FirstOrDefault(n => n.Row == row) is not { } note)
            return;

        int key = Math.Clamp(KeyForNote(note.Note) + _moveKeys, 0, KeyCount - 1);
        Audition(NoteForKey(key), note.InstrumentNumber);
    }

    /// <summary>Puts the dragged selection down where the drag left it - the same distance in beats
    /// and in keys for every note in it, so a chord shape or a phrase moves as one.</summary>
    private void ApplyMove(Pattern pattern)
    {
        var moves = new List<(int FromRow, int ToRow, NoteName Note)>();
        foreach (int row in _selection)
        {
            if (_notes.FirstOrDefault(n => n.Row == row) is not { } note)
                continue;

            int column = Math.Clamp(ColumnOf(row) + _moveColumns, 0, ColumnCount - 1);
            int key = Math.Clamp(KeyForNote(note.Note) + _moveKeys, 0, KeyCount - 1);
            moves.Add((row, RowOf(column), NoteForKey(key)));
        }

        PatternNotes.MoveNotes(pattern, moves);

        // The selection follows the notes to where they went.
        _selection.Clear();
        foreach (var move in moves)
            _selection.Add(move.ToRow);

        if (moves.Count > 0)
            _selectedRow = moves[0].ToRow;

        Commit();
    }

    /// <summary>
    /// Keys that act on the selected notes rather than on the selected row: Ctrl+A selects them all,
    /// Ctrl+X cuts them,
    /// Ctrl+C copies them, Delete removes them and Escape lets go of them. Ctrl+V pastes what was
    /// copied, starting at the selected row - which a click in the beat bar sets without writing
    /// anything, so an empty spot can be aimed at. Returns true when the key belonged here.
    /// </summary>
    private bool HandleToolKey(KeyEventArgs e, Pattern pattern)
    {
        bool selected = _tool == RollTool.Select && _selection.Count > 0;
        bool slides = _tool == RollTool.Select && _slideSelection is not null;

        switch (e.KeyCode)
        {
            // The slide lane's selection first: while it is there, it is what the keys act on.
            case Keys.C when e.Control && slides:
                CopySlides();
                break;

            case Keys.X when e.Control && slides:
                CopySlides();
                ClearSlides(pattern);
                break;

            case Keys.Delete when slides:
                ClearSlides(pattern);
                break;

            case Keys.Escape when _slideSelection is not null:
                _slideSelection = null;
                Invalidate();
                break;

            case Keys.V when e.Control && PatternClipboard.LastCopied == ClipboardKind.Slides
                             && PatternClipboard.Slides.Count > 0 && _selectedRow >= 0:
                PasteSlides(pattern);
                break;

            // Every note in the pattern, under Object Selection so the selection can be cut,
            // copied, moved or deleted at once.
            case Keys.A when e.Control:
                if (_tool != RollTool.Select)
                    ToolRequested?.Invoke(RollTool.Select);

                if (_tool != RollTool.Select)
                    Tool = RollTool.Select;

                _selection.Clear();
                foreach (var note in _notes)
                    _selection.Add(note.Row);

                Invalidate();
                break;

            case Keys.Escape when _selection.Count > 0:
                _selection.Clear();
                Invalidate();
                break;

            case Keys.C when e.Control && selected:
                CopySelection();
                break;

            case Keys.X when e.Control && selected:
                CopySelection();
                EraseSelection(pattern);
                break;

            case Keys.Delete when selected:
                EraseSelection(pattern);
                break;

            case Keys.V when e.Control && PatternClipboard.LastCopied == ClipboardKind.Notes
                             && PatternClipboard.Notes.Count > 0 && _selectedRow >= 0:
                PasteNotes(pattern);
                break;

            default:
                return false;
        }

        e.Handled = e.SuppressKeyPress = true;
        return true;
    }

    private void CopySelection()
    {
        var notes = _notes.Where(n => _selection.Contains(n.Row)).OrderBy(n => n.Row).ToList();
        if (notes.Count == 0)
            return;

        int first = ColumnOf(notes[0].Row);
        PatternClipboard.CopyNotes(notes.Select(n =>
            new CopiedNote(ColumnOf(n.Row) - first, n.Note, n.InstrumentNumber, n.Length)));
    }

    /// <summary>The selected stretch of the slide lane: the rate on each row a slide can go on,
    /// by its column's distance from the first.</summary>
    private void CopySlides()
    {
        if (_slideSelection is not { } range)
            return;

        var slides = new List<CopiedSlide>();
        for (int column = range.From; column <= range.To; column++)
        {
            int row = RowOf(column);
            if (row >= 0 && row < _slidable.Length && _slidable[row])
                slides.Add(new CopiedSlide(column - range.From, _slideRates[row]));
        }

        if (slides.Count > 0)
            PatternClipboard.CopySlides(slides);
    }

    /// <summary>No slide over the selected stretch - the rows after it keep the rate they had.</summary>
    private void ClearSlides(Pattern pattern)
    {
        if (_slideSelection is not { } range)
            return;

        var rates = new Dictionary<int, int>();
        for (int column = range.From; column <= range.To; column++)
        {
            if (RowOf(column) is var row and >= 0)
                rates[row] = 0;
        }

        PatternSlides.SetRates(pattern, rates);
        Commit();
    }

    /// <summary>
    /// Puts the copied slide rates down from the selected row on - a click in the slide lane or
    /// the beat bar picks it, in this pattern or another - and leaves the pasted stretch selected.
    /// Rows past the pattern's end are cut off, rows that cannot take a slide are skipped, and the
    /// rows after the paste keep the rate they had.
    /// </summary>
    private void PasteSlides(Pattern pattern)
    {
        int start = ColumnOf(_selectedRow);
        int last = start;
        var rates = new Dictionary<int, int>();

        foreach (var slide in PatternClipboard.Slides)
        {
            int column = start + slide.ColumnOffset;
            if (column >= ColumnCount || RowOf(column) is not (var row and >= 0))
                continue;

            rates[row] = slide.Rate;
            last = Math.Max(last, column);
        }

        PatternSlides.SetRates(pattern, rates);
        _selection.Clear();
        _slideSelection = (start, last);
        Commit();
    }

    private void EraseSelection(Pattern pattern)
    {
        foreach (int row in _selection.OrderByDescending(r => r))
            PatternNotes.Erase(pattern, row);

        _selection.Clear();
        Commit();
    }

    /// <summary>Puts the copied notes down from the selected row on, keeping their spacing in beats
    /// and their lengths, and leaves them selected - ready to drag straight on to where they
    /// belong. What runs past the end of the pattern is cut off; a paste never lengthens it.
    /// </summary>
    private void PasteNotes(Pattern pattern)
    {
        int start = ColumnOf(_selectedRow);
        var placed = PatternClipboard.Notes
            .Where(n => start + n.BeatOffset < ColumnCount)
            .Select(n => (Row: RowOf(start + n.BeatOffset), n.Note, n.Instrument, n.Length))
            .ToList();

        PatternNotes.PlaceNotes(pattern, placed);

        _selection.Clear();
        foreach (var note in placed)
            _selection.Add(note.Row);

        Commit();
    }

    /// <summary>What the tools have in hand, drawn over the notes: the notes being dragged at where
    /// they would land, the rubber band, and the line about to be drawn.</summary>
    private void DrawToolOverlays(Graphics g)
    {
        int bottom = GridBottom;

        if (_moving && (_moveColumns != 0 || _moveKeys != 0))
        {
            using var ghost = new Pen(Color.White) { DashStyle = DashStyle.Dash };
            foreach (int row in _selection)
            {
                if (_notes.FirstOrDefault(n => n.Row == row) is not { } note)
                    continue;

                int key = Math.Clamp(KeyForNote(note.Note) + _moveKeys, 0, KeyCount - 1);
                if (BlockBounds(row, note.Length, key) is { } bounds)
                {
                    bounds.Offset(_moveColumns * RowWidth, 0);
                    g.DrawRectangle(ghost, bounds);
                }
            }
        }

        if (_bandStart is not null && !_band.IsEmpty)
        {
            using (var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Oscillator, 24)))
                g.FillRectangle(fill, _band);

            using var edge = new Pen(DawPalette.Oscillator) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(edge, _band);
        }

        if (_lineStart is { } start)
        {
            int x = KeyboardWidth + (ColumnOf(start.Row) * RowWidth) - _scrollX.Value + (RowWidth / 2);
            int y = GridTop + (start.Key * KeyHeight) - _scrollY.Value + (KeyHeight / 2);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var line = new Pen(DawPalette.Oscillator, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(line, x, y, _linePointer.X, _linePointer.Y);
            g.SmoothingMode = SmoothingMode.Default;
        }
    }

    /// <summary>The note whose end is being dragged, or -1. Only the right edge: a note's start is
    /// its row, and dragging that would be moving it rather than resizing it.</summary>
    private int _resizingRow = -1;

    private bool _resizeChanged;

    // Beat columns against pattern rows - see BuildColumns.
    private int[] _rowOfColumn = [];
    private int[] _columnOfRow = [];

    private int _selectedRow = -1;
    private int _playRow = -1;

    // How far through the sounding row the playhead is, 0 to 1 - see SetPlayRow.
    private double _playFraction;
    private (int Row, int Key) _hovered = (-1, -1);

    /// <summary>How long the pointer rests on a note before its instrument is named above it.</summary>
    private const int InstrumentLabelDelay = 2000;

    // The note the pointer rests on, the pointer's x over it, and whether its label is up. The
    // label goes where the pointer is rather than over the note's block, so it names a loop's
    // linked copy where that copy is drawn.
    private readonly System.Windows.Forms.Timer _labelTimer = new() { Interval = InstrumentLabelDelay };
    private NoteBlock? _labelNote;
    private int _labelX;
    private bool _labelShown;

    public PianoRollView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        BackColor = DawPalette.Background;
        TabStop = true;

        _scrollX.Scroll += (_, _) => { HideInstrumentLabel(); Invalidate(); };
        _scrollY.Scroll += (_, _) => { HideInstrumentLabel(); Invalidate(); };
        Controls.Add(_scrollX);
        Controls.Add(_scrollY);

        _labelTimer.Tick += (_, _) =>
        {
            _labelTimer.Stop();
            if (_labelNote is null)
                return;

            _labelShown = true;
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _labelTimer.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>Starts the wait for a note's label when the pointer comes onto a note, and takes
    /// the label down when it goes off one - onto another note, the wait starts again.</summary>
    private void HoverNote(NoteBlock? note, int x)
    {
        if (note == _labelNote)
            return;

        HideInstrumentLabel();
        _labelNote = note;
        _labelX = x;
        if (note is not null)
            _labelTimer.Start();
    }

    private void ShowInstrumentLabel(NoteBlock note, int x)
    {
        _labelTimer.Stop();
        _labelNote = note;
        _labelX = x;
        _labelShown = true;
        Invalidate();
    }

    private void HideInstrumentLabel()
    {
        _labelTimer.Stop();
        _labelNote = null;
        if (_labelShown)
        {
            _labelShown = false;
            Invalidate();
        }
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

    /// <summary>Letting go of the arrow that moved a note up or down lets go of the note, as
    /// lifting a finger off its key would.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down)
            ReleaseAudition();

        base.OnKeyUp(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_slideSelectAnchor >= 0)
        {
            _slideSelectAnchor = -1;
            Capture = false;
            if (_slideSelection is { } picked)
                _selectedRow = RowOf(picked.From);

            Invalidate();
            base.OnMouseUp(e);
            return;
        }

        if (_slideStroke is { } stroke)
        {
            _slideStroke = null;
            Capture = false;
            if (_pattern is { } pattern)
            {
                var rates = new Dictionary<int, int>();
                foreach (var (column, rate) in stroke)
                {
                    int row = RowOf(column);
                    if (row >= 0)
                        rates[row] = rate;
                }

                PatternSlides.SetRates(pattern, rates);
                Commit();
            }

            base.OnMouseUp(e);
            return;
        }

        ToolMouseUp(e);

        // One edit for the whole drag, not one per pixel: the song is marked dirty and the views
        // rebuilt on Edited, and a hundred of those while a mouse moves is a hundred too many.
        if (_resizingRow >= 0)
        {
            _resizingRow = -1;
            Capture = false;

            if (_resizeChanged)
            {
                _resizeChanged = false;
                Commit();
            }
        }

        ReleaseAudition();
        base.OnMouseUp(e);
    }

    /// <summary>Capture lost without a mouse-up - alt-tab mid-click, a dialog popping up. The note
    /// would otherwise hang until the next click.</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture && _slideStroke is not null)
        {
            _slideStroke = null;            // lost mid-stroke: nothing written
            Invalidate();
        }

        if (!Capture)
            _slideSelectAnchor = -1;        // the selection made so far stays

        if (!Capture)
        {
            ReleaseAudition();

            // A resize left hanging would follow the pointer again on the next move, with no
            // button held.
            if (_resizingRow >= 0)
            {
                _resizingRow = -1;
                if (_resizeChanged)
                {
                    _resizeChanged = false;
                    Commit();
                }
            }
        }

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
        _slideSelection = null;
        Reread();
        UpdateScrollRange();
        Invalidate();
    }

    public void SetDefaultInstrument(int instrumentNumber) => _defaultInstrument = instrumentNumber;

    /// <summary>
    /// Writes a note at the cursor and steps past it - what a keyboard is for.
    ///
    /// One row per key, not the grid's spacing: rows are ticks, so stepping by four would leave
    /// gaps a player did not ask for. Lengthening a note is what holding the row after it does, and
    /// that is a separate gesture.
    ///
    /// Returns false when there is nowhere to write - no pattern, or the cursor sitting on a loop
    /// marker, which owns its row.
    /// </summary>
    public bool IsRecording => _recordPattern is not null;

    /// <summary>The number of the pattern a take goes into, or -1.</summary>
    public int RecordPatternNumber => _recordPattern?.Number ?? -1;

    /// <summary>Starts a take into the pattern on screen. The host starts the song from this
    /// pattern's place in it and feeds <see cref="SetRecordRow"/>.</summary>
    public void BeginRecording()
    {
        if (_pattern is null)
            return;

        _recordPattern = _pattern;
        _recordRow = -1;
        _recordHeld = null;
        _takeWritten = false;
        Invalidate();
    }

    /// <summary>Ends the take: a key still held is let go where playback got to, and whatever
    /// was written becomes one edit.</summary>
    public void EndRecording()
    {
        if (_recordPattern is null)
            return;

        if (_recordHeld is { } held)
            RecordRelease(held.Note);

        _recordPattern = null;
        _recordRow = -1;
        if (_takeWritten)
            Commit();
        else
            Invalidate();
    }

    /// <summary>The punch-out: a key still held is let go at the row reached, and nothing more is
    /// written until the punch-in comes round again.</summary>
    public void PunchOut()
    {
        if (_recordHeld is { } held)
            RecordRelease(held.Note);

        _recordRow = -1;
    }

    /// <summary>The row the recording channel is playing in the take's pattern, and how far
    /// through it - or -1 while that channel is somewhere else.</summary>
    public void SetRecordRow(int row, double fraction, double ticksIntoEntry = -1)
    {
        _recordRow = row;
        _recordFraction = fraction;
        _recordTicks = row >= 0 && _unrollLoops ? ticksIntoEntry : -1;
    }

    /// <summary>The column a key lands on when the ticks are known: the nearest, not past the end.</summary>
    private int RecordColumn() => Math.Clamp((int)Math.Floor(_recordTicks + 0.5), 0, Math.Max(0, ColumnCount - 1));

    /// <summary>The row a key lands on: the one playing, or the next if more than half of it has
    /// gone - the nearest row, which is what a tracker's realtime record quantises to. Not past
    /// the pattern's end, which belongs to whatever the song plays next.</summary>
    private int RecordQuantised(Pattern pattern) =>
        _recordTicks >= 0 && ColumnCount > 0 ? RowOf(RecordColumn())
        : _recordFraction >= 0.5 && _recordRow + 1 < pattern.Rows.Count ? _recordRow + 1 : _recordRow;

    /// <summary>A key went down during a take. Writes the note where playback is; its length is
    /// settled when the key comes up. One channel plays one note, so a new key ends the last.</summary>
    public bool RecordNote(NoteName note, int instrumentNumber)
    {
        if (_recordPattern is not { } pattern || _recordRow < 0 || pattern.Rows.Count == 0)
            return false;

        if (_recordHeld is { } previous)
            RecordRelease(previous.Note);

        if (WritableRowFrom(pattern, RecordQuantised(pattern)) is not { } row)
            return false;

        PatternNotes.SetNote(pattern, row, note, instrumentNumber);
        _recordHeld = (note, row, _recordTicks >= 0 ? RecordColumn() : -1);
        _takeWritten = true;
        if (ReferenceEquals(pattern, _pattern))
        {
            Reread();
            Invalidate();
        }

        return true;
    }

    /// <summary>The held key came up: the note lasts to the row playback has reached, rounded the
    /// same way, and a "---" stops it there. Released after the pattern ended or wrapped, it runs
    /// to the pattern's end.</summary>
    public void RecordRelease(NoteName note)
    {
        if (_recordPattern is not { } pattern || _recordHeld is not { } held || held.Note != note)
            return;

        // Unlike a key going down, a release may round past the last row: it then runs to the end.
        _recordHeld = null;
        int end = _recordRow < 0 ? pattern.Rows.Count : _recordRow + (_recordFraction >= 0.5 ? 1 : 0);
        int length = end > held.Row ? end - held.Row : end == held.Row ? 1 : pattern.Rows.Count - held.Row;

        // By columns when the ticks are known: as far as the key was held, but only over the
        // rows that follow the note on its own pass - a loop's jump back ends it there.
        if (held.Column >= 0 && _recordTicks >= 0)
        {
            int endColumn = (int)Math.Floor(_recordTicks + 0.5);
            length = 1;
            for (int c = held.Column + 1; c < Math.Min(endColumn, ColumnCount) && RowOf(c) == held.Row + (c - held.Column); c++)
                length++;
        }

        PatternNotes.SetLength(pattern, held.Row, length);
        if (ReferenceEquals(pattern, _pattern))
        {
            Reread();
            Invalidate();
        }
    }

    public bool StepInput(NoteName note)
    {
        if (_pattern is not { } pattern || pattern.Rows.Count == 0)
            return false;

        // Where the cursor sits, or the next place a note can actually go. A loop marker owns its
        // row outright, so parking on one would leave every key after it doing nothing.
        if (WritableRowFrom(pattern, Math.Max(0, _selectedRow)) is not { } row)
            return false;

        PatternNotes.SetNote(pattern, row, note, _defaultInstrument);

        // Round to the top rather than stopping at the bottom. Clamping meant every key after the
        // last row overwrote that one row, which looks like the keyboard has stopped working.
        _selectedRow = WritableRowFrom(pattern, (row + 1) % pattern.Rows.Count) ?? row;

        EnsureRowVisible(_selectedRow);
        Commit();
        return true;
    }

    /// <summary>The first row from <paramref name="from"/> onwards that can hold a note, wrapping
    /// once. Null when the pattern is nothing but loop markers, which cannot be played into.</summary>
    private static int? WritableRowFrom(Pattern pattern, int from)
    {
        for (int i = 0; i < pattern.Rows.Count; i++)
        {
            int row = (from + i) % pattern.Rows.Count;
            if (pattern.Rows[row] is not (LoopStartRow or LoopEndRow))
                return row;
        }

        return null;
    }

    /// <summary>Scrolls the least amount that brings a row on screen - so wrapping to the top takes
    /// the view with it instead of leaving the cursor somewhere nobody is looking.</summary>
    private void EnsureRowVisible(int row)
    {
        if (!_scrollX.Enabled)
            return;

        int visible = Math.Max(1, Width - KeyboardWidth - _scrollY.Width);
        int left = ColumnOf(row) * RowWidth;
        int right = left + RowWidth;
        int max = Math.Max(0, _scrollX.Maximum - _scrollX.LargeChange + 1);

        int target = _scrollX.Value;
        if (left < _scrollX.Value)
            target = left;
        else if (right > _scrollX.Value + visible)
            target = right - visible;

        _scrollX.Value = Math.Clamp(target, 0, max);
    }

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

    /// <summary>
    /// Which row is sounding, or -1, and how far through it.
    ///
    /// Called on every poll, not only when the row changes: the vertical centring eases toward its
    /// target over several frames and needs the ticks in between to get there - and so does the
    /// playhead, which glides from beat to beat at the song's own speed. The fraction comes from
    /// the sequencer's tick countdown and the audio clock (see PlayheadPosition), so nothing here
    /// has to guess how long a row is.
    /// </summary>
    public void SetPlayRow(int row, double fraction, double ticksIntoEntry = -1)
    {
        // Where the line is now, before either value changes - the region to repaint has to cover
        // where it was as well as where it goes.
        int previous = _playRow;
        int before = PlayheadX(_playRow, _playFraction);

        _playRow = row;
        _playFraction = row < 0 ? 0 : Math.Clamp(fraction, 0.0, 1.0);

        // Unrolled, the ticks into the clip are the column - on whichever pass of a loop.
        _playTicks = row >= 0 && _unrollLoops ? ticksIntoEntry : -1;

        bool scrolled = AutoScroll && row >= 0 && (FollowPlayRow() | CenterOnNotes());

        if (scrolled || (previous < 0) != (row < 0))
        {
            Invalidate();
            return;
        }

        // Only the sliver the line moved through. A full repaint sixty times a second to drag one
        // vertical line across a beat would be the most expensive thing the view does.
        int after = PlayheadX(_playRow, _playFraction);
        if (after == before)
        {
            return;
        }

        int left = (before == int.MinValue ? after : after == int.MinValue ? before : Math.Min(before, after)) - 2;
        int right = Math.Max(before, after) + 2;
        Invalidate(new Rectangle(left, GridTop, right - left, Height - GridTop));
    }

    private int PlayheadX(int row, double fraction) => row < 0
        ? int.MinValue
        : KeyboardWidth + (int)(PlayColumn(row, fraction) * RowWidth) - _scrollX.Value;

    private double PlayColumn(int row, double fraction) =>
        _playTicks >= 0 && row == _playRow ? _playTicks : ColumnOf(row) + fraction;

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

        int firstColumn = _scrollX.Value / RowWidth;
        int lastColumn = (_scrollX.Value + Math.Max(1, Width - KeyboardWidth - _scrollY.Width)) / RowWidth;

        var visible = _notes.Where(n =>
            ColumnOf(n.Row) <= lastColumn && ColumnOf(n.Row) + n.Length > firstColumn
            && KeyForNote(n.Note) >= 0).ToList();
        if (visible.Count == 0)
            visible = _notes.Where(n => KeyForNote(n.Note) >= 0).ToList();
        if (visible.Count == 0)
            return false;

        int top = visible.Min(n => KeyForNote(n.Note));
        int bottom = visible.Max(n => KeyForNote(n.Note));
        int visibleHeight = Math.Max(1, GridBottom - GridTop);

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

        int x = (int)(PlayColumn(_playRow, _playFraction) * RowWidth);
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
            _slideRates = PatternSlides.Rates(pattern);
            _slidable = PatternSlides.Slidable(pattern);
        }
        else
        {
            _notes = [];
            _releases = [];
            _slideRates = [];
            _slidable = [];
        }

        BuildColumns();
    }

    /// <summary>
    /// Which pattern row each beat column shows, and the way back.
    ///
    /// A loop marker is an instruction, not a beat - it takes a row in the stream but sounds for no
    /// time at all. Giving it a column of its own pushed every note after it one beat to the right
    /// of where the bar lines said it was, which is what made an LE look like it sat a beat late
    /// against the same pattern in the tracker. Columns count playable rows only, exactly as the
    /// tracker numbers its lines, and a marker is drawn on the column it brackets: an LS on the row
    /// after it, an LE on the row before.
    /// </summary>
    private void BuildColumns()
    {
        var rows = _pattern?.Rows;
        if (rows is null)
        {
            _rowOfColumn = [];
            _columnOfRow = [];
            return;
        }

        var columns = new List<int>(rows.Count);
        _columnOfRow = new int[rows.Count];

        // Unrolled, a column is a tick: a loop's section once per pass, a row on its first pass.
        // A row the song never reaches - after an endless loop - borrows the column before it.
        var played = _unrollLoops && _song is { } song && _pattern is { } shown
            ? PatternLoops.Unroll(shown, shown.Rows.Any(r => r is LoopEndRow { RepeatCount: null }) ? PatternLoops.EndlessCap(song) : 0)
            : PatternLoops.Compact(_pattern!);
        _columnInfo = [.. played];
        Array.Fill(_columnOfRow, -1);
        foreach (var info in played)
        {
            if (_columnOfRow[info.Row] < 0)
                _columnOfRow[info.Row] = columns.Count;
            columns.Add(info.Row);
        }

        for (int row = 0; row < rows.Count; row++)
        {
            if (_columnOfRow[row] < 0 && rows[row] is not (LoopStartRow or LoopEndRow))
                _columnOfRow[row] = row > 0 ? Math.Max(0, _columnOfRow[row - 1]) : 0;
        }

        // A marker borrows the column of the row it brackets, so clicking its label lands on the
        // marker's own row and the label sits where the tracker shows it.
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row] is LoopStartRow)
            {
                _columnOfRow[row] = row + 1 < rows.Count ? _columnOfRow[row + 1] : Math.Max(0, columns.Count - 1);
            }
            else if (rows[row] is LoopEndRow)
            {
                _columnOfRow[row] = row > 0 ? _columnOfRow[row - 1] : 0;
            }
        }

        _rowOfColumn = [.. columns];
    }

    /// <summary>The column a pattern row is drawn in.</summary>
    private int ColumnOf(int row) =>
        row >= 0 && row < _columnOfRow.Length ? _columnOfRow[row] : 0;

    /// <summary>The pattern row a column stands for, or -1 past the end.</summary>
    private int RowOf(int column) =>
        column >= 0 && column < _rowOfColumn.Length ? _rowOfColumn[column] : -1;

    private int ColumnCount => _rowOfColumn.Length;

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
        int columns = ColumnCount;
        int visibleWidth = Math.Max(1, Width - KeyboardWidth - _scrollY.Width);
        int visibleHeight = Math.Max(1, Height - RulerHeight - HeaderHeight - _scrollX.Height - SlideLaneHeight);

        _scrollX.Minimum = 0;
        _scrollX.Maximum = Math.Max(0, (columns * RowWidth) + 40);
        _scrollX.LargeChange = visibleWidth;
        _scrollX.SmallChange = RowWidth * 4;
        _scrollX.Enabled = columns * RowWidth > visibleWidth;

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

    /// <summary>The grid starts below the beat bar, which is a band of its own rather than a slice
    /// taken out of the header - the header already has the pattern's name on one side and the
    /// grid switch on the other, and a strip carved out of it would have been drawn over both.
    /// </summary>
    private int GridTop => RulerHeight + HeaderHeight + BeatBarHeight;

    /// <summary>The slide lane under the notes: a bar per row for the slide rate in force there,
    /// drawn with the mouse like a controller lane, and the notes above it showing the glide.</summary>
    private const int SlideLaneHeight = 64;

    /// <summary>Where the notes stop and the slide lane begins.</summary>
    private int GridBottom => Height - _scrollX.Height - SlideLaneHeight;

    private int SlideLaneMid => GridBottom + (SlideLaneHeight / 2);

    /// <summary>Half the lane's height a bar can use. The scale is a square root, so the small
    /// rates a slide mostly uses are tall enough to see and to draw.</summary>
    private const int SlideHalf = (SlideLaneHeight / 2) - 6;

    private static float SlideOffsetY(int rate) =>
        rate == 0 ? 0f : -Math.Sign(rate) * (float)Math.Sqrt(Math.Abs(rate) / 128.0) * SlideHalf;

    private int SlideRateAtY(int y)
    {
        double d = Math.Clamp((SlideLaneMid - y) / (double)SlideHalf, -1.0, 1.0);
        if (Math.Abs(d) < 0.06)
            return 0;                       // a little dead zone, so 0 is easy to draw
        int rate = (int)Math.Round(Math.Sign(d) * d * d * 128);
        return Math.Clamp(rate, SlideRow.MinRate, SlideRow.MaxRate);
    }

    /// <summary>The column of the slide lane under a point, or null outside it.</summary>
    private int? SlideLaneColumnAt(Point point)
    {
        if (_pattern is null || point.X < KeyboardWidth || point.X > Width - _scrollY.Width
            || point.Y < GridBottom || point.Y >= Height - _scrollX.Height)
        {
            return null;
        }

        int column = ColumnAtX(point.X);
        return column >= 0 && column < ColumnCount ? column : null;
    }

    /// <summary>The strip of row cells between the header and the grid. Clicking one picks that
    /// row without writing anything into it - the only way to aim a marker, a gate-off or a paste
    /// at an empty row, since every click inside the grid itself means a note. Loop markers wear
    /// their labels here too, which is how they are selected now that they no longer run the
    /// height of the grid.</summary>
    private const int BeatBarHeight = 16;

    private int BeatBarTop => RulerHeight + HeaderHeight;

    /// <summary>The loop marker drawn in a column, or -1.</summary>
    private int MarkerRowInColumn(Pattern pattern, int column)
    {
        for (int row = 0; row < pattern.Rows.Count; row++)
        {
            if (pattern.Rows[row] is (LoopStartRow or LoopEndRow) && ColumnOf(row) == column)
            {
                return row;
            }
        }

        return -1;
    }

    /// <summary>The row whose cell in the beat bar is under a point, if the point is in the strip
    /// at all. Rows only - the strip has no pitch, which is the whole point of it.</summary>
    private int? BeatBarRowAt(Point point)
    {
        if (_pattern is not { } pattern
            || point.Y < BeatBarTop || point.Y >= GridTop
            || point.X < KeyboardWidth || point.X > Width - _scrollY.Width)
        {
            return null;
        }

        int column = (point.X - KeyboardWidth + _scrollX.Value) / RowWidth;

        // A marker shares its column with the row it brackets, and the strip is the only way to
        // reach the marker - the note keeps its own block in the grid.
        int marker = MarkerRowInColumn(pattern, column);
        int row = marker >= 0 ? marker : RowOf(column);
        return row >= 0 ? row : null;
    }

    private (int Row, int Key)? CellAt(Point point)
    {
        if (_pattern is null || point.X < KeyboardWidth || point.Y < GridTop || point.Y >= GridBottom)
            return null;

        int row = RowOf((point.X - KeyboardWidth + _scrollX.Value) / RowWidth);
        int key = (point.Y - GridTop + _scrollY.Value) / KeyHeight;

        return row >= 0 && key >= 0 && key < KeyCount ? (row, key) : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_slideStroke is not null)
        {
            ContinueSlideStroke(e);
            base.OnMouseMove(e);
            return;
        }

        if (_slideSelectAnchor >= 0)
        {
            int column = Math.Clamp(ColumnAtX(e.X), 0, Math.Max(0, ColumnCount - 1));
            var selection = (Math.Min(_slideSelectAnchor, column), Math.Max(_slideSelectAnchor, column));
            if (selection != _slideSelection)
            {
                _slideSelection = selection;
                Invalidate();
            }

            base.OnMouseMove(e);
            return;
        }

        var slideHover = SlideLaneColumnAt(e.Location) is { } hoverColumn
            ? (hoverColumn, SlideRateAtY(e.Y))
            : ((int, int)?)null;
        if (slideHover != _slideHover)
        {
            _slideHover = slideHover;
            Invalidate();
        }

        if (_resizingRow >= 0)
        {
            DragNoteEnd(e.X);
            base.OnMouseMove(e);
            return;
        }

        if (ToolMouseMove(e))
        {
            base.OnMouseMove(e);
            return;
        }

        var at = CellAt(e.Location) ?? (-1, -1);
        if (at != _hovered)
        {
            _hovered = at;
            Invalidate();
        }

        // Held down, the wheel button's label stays while the pointer stays on its note.
        HoverNote(at.Item1 >= 0 && e.Button is MouseButtons.None or MouseButtons.Middle ? NoteAt(at) : null, e.X);

        // Only over the handle itself, so the cursor says which few pixels do the resizing.
        var under = at.Item1 >= 0
            ? _notes.FirstOrDefault(n => at.Item1 >= n.Row && at.Item1 < n.Row + n.Length)
            : null;

        bool handle = _tool is RollTool.Draw or RollTool.Select
                      && under is not null && KeyForNote(under.Note) == at.Item2 && OverEndHandle(under, e.X);
        Cursor = handle ? Cursors.SizeWE : CursorFor(_tool);

        base.OnMouseMove(e);
    }

    /// <summary>
    /// How far a drag may pull a note's end: over the holds and slide rows behind it, and over its
    /// own release and whatever that release was holding, but never over the next note. Stopping
    /// at a slide row - one right after the note - left the note one row long at the first move,
    /// and its release written over the slide.
    ///
    /// SetLength would happily take it further - a tracker lets a note be written straight over the
    /// rows after it, and that is right for typing. It is wrong for a mouse, where overshooting by
    /// a few pixels would delete a note the hand never meant to touch.
    /// </summary>
    private static int DraggableLength(Pattern pattern, int row)
    {
        int length = 1;
        for (int next = row + 1; next < pattern.Rows.Count && pattern.Rows[next] is HoldRow or NoteOffRow or SlideRow; next++)
        {
            length++;
        }

        return length;
    }

    /// <summary>The right few pixels of a note's block, where a drag resizes it.</summary>
    private bool OverEndHandle(NoteBlock note, int x)
    {
        int key = KeyForNote(note.Note);
        return BlockBounds(note.Row, note.Length, key) is { } bounds
               && x >= bounds.Right - 5 && x <= bounds.Right + 1;
    }

    /// <summary>
    /// Shortens or lengthens the dragged note to wherever the pointer is.
    ///
    /// Nothing is said about gate-offs here because the format already answers it: a note reaches
    /// as far as the next event and no further, so shortening one into the holds behind it leaves
    /// rows with nothing sounding, and SetLength writes the release that stops it. A note that runs
    /// straight into the next note has no slack to give back and needs no release at all.
    /// </summary>
    private void DragNoteEnd(int x)
    {
        if (_pattern is not { } pattern || !_resizeFrom.TryGetValue(_resizingRow, out int grabbedFrom))
        {
            return;
        }

        // How far the grabbed note's end has moved, in beats, from where the drag began - and every
        // note being stretched moves its own end by that much from its own starting length. Each
        // is measured from the start of the drag rather than from the last step, so going back
        // and forth never drifts.
        int column = (x - KeyboardWidth + _scrollX.Value) / RowWidth;
        int delta = (column - ColumnOf(_resizingRow) + 1) - grabbedFrom;
        if (delta == _resizeDelta || _resizeSnapshot is not { } snapshot)
        {
            return;
        }

        // From the rows as they were when the drag began, every time: an end dragged back over
        // slide rows puts its release on them on the way, and only starting again from the
        // original leaves them as they were once the end has moved on.
        _resizeDelta = delta;
        pattern.Rows.Clear();
        pattern.Rows.AddRange(snapshot);

        foreach (var (row, from) in _resizeFrom)
        {
            int length = Math.Clamp(from + delta, 1, DraggableLength(pattern, row));
            PatternNotes.SetLength(pattern, row, length);
        }

        _resizeChanged = !pattern.Rows.SequenceEqual(snapshot);
        Reread();
        Invalidate();
    }

    /// <summary>The notes a resize drag is stretching, and how long each was when it began.</summary>
    private readonly Dictionary<int, int> _resizeFrom = [];

    /// <summary>The pattern's rows when the resize drag began, and the change in length it last
    /// applied to them.</summary>
    private List<PatternRow>? _resizeSnapshot;
    private int _resizeDelta;

    /// <summary>Starts dragging a note's end - that note alone, or with <paramref name="together"/>
    /// every selected note, each keeping its own length plus whatever the drag adds.</summary>
    private void BeginResize(NoteBlock grabbed, bool together)
    {
        _resizeFrom.Clear();
        foreach (var note in _notes)
        {
            if (note.Row == grabbed.Row || (together && _selection.Contains(note.Row)))
                _resizeFrom[note.Row] = note.Length;
        }

        _resizingRow = grabbed.Row;
        _resizeChanged = false;
        _resizeSnapshot = _pattern is { } pattern ? [.. pattern.Rows] : null;
        _resizeDelta = 0;
        _selectedRow = grabbed.Row;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_slideHover is not null)
        {
            _slideHover = null;
            Invalidate();
        }

        _hovered = (-1, -1);
        HideInstrumentLabel();
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        HideInstrumentLabel();

        // Which pass a click in the grid, the beat bar or the slide lane lands on.
        if (e.X >= KeyboardWidth && e.Y >= BeatBarTop && _pattern is not null)
        {
            int column = ColumnAtX(e.X);
            _selectedPass = column >= 0 && column < _columnInfo.Length ? _columnInfo[column].Pass : 1;
        }

        // The header's grid toggle.
        if (e.Y < RulerHeight + HeaderHeight && e.Y >= RulerHeight && GridToggleBounds.Contains(e.Location))
        {
            Grid = _grid == RollGrid.Beats ? RollGrid.Rows : RollGrid.Beats;
            return;
        }

        // A click anywhere but the slide lane lets go of what was selected there.
        bool inSlideLane = SlideLaneColumnAt(e.Location) is not null;
        if (_slideSelection is not null && !inSlideLane)
        {
            _slideSelection = null;
            Invalidate();
        }

        // The slide lane. Draw draws a stroke, written when the button comes up - right-click
        // draws 0. Object Selection picks a run of rows to copy, cut, delete or paste over. The
        // other tools leave the lane alone.
        if (SlideLaneColumnAt(e.Location) is { } slideColumn)
        {
            if (_tool == RollTool.Draw && e.Button is MouseButtons.Left or MouseButtons.Right)
            {
                int rate = e.Button == MouseButtons.Right ? 0 : SlideRateAtY(e.Y);
                _slideStroke = new Dictionary<int, int> { [slideColumn] = rate };
                _slideStrokeLast = (slideColumn, rate);
                Capture = true;
                Invalidate();
            }
            else if (_tool == RollTool.Select && e.Button == MouseButtons.Left)
            {
                _selection.Clear();
                _slideSelectAnchor = slideColumn;
                _slideSelection = (slideColumn, slideColumn);
                _selectedRow = RowOf(slideColumn);
                Capture = true;
                Invalidate();
            }

            return;
        }

        // The beat bar. Clicking a cell picks that row and writes nothing - which is how an empty
        // row is aimed at for a marker, a gate-off or a paste. Right-click clears a loop marker
        // there, the way right-click clears a note in the grid.
        if (BeatBarRowAt(e.Location) is { } barRow && _pattern is { } marked)
        {
            if (e.Button == MouseButtons.Right)
            {
                PatternNotes.RemoveMarker(marked, barRow);
                Commit();
            }

            _selectedRow = barRow;
            Invalidate();
            return;
        }

        if (_pattern is not { } pattern || CellAt(e.Location) is not { } cell)
            return;

        // Right-click deletes the note under it, whatever the tool - and leaves silence there,
        // rather than the tracker's hold that would let the note before grow into the gap.
        if (e.Button == MouseButtons.Right)
        {
            if (NoteAt(cell) is { } doomed)
                PatternNotes.Erase(pattern, doomed.Row);
            else
                PatternNotes.Remove(pattern, cell.Row);

            _selection.Remove(cell.Row);
            Commit();
            return;
        }

        if (e.Button == MouseButtons.Left && _tool != RollTool.Draw && ToolMouseDown(e, pattern, cell))
        {
            base.OnMouseDown(e);
            return;
        }

        // Ctrl+click writes a gate-off instead of a note. Which key column it lands in does not
        // matter - a release has no pitch - so the row alone decides.
        if (ModifierKeys.HasFlag(Keys.Control))
        {
            PatternNotes.SetRelease(pattern, cell.Row);
            _selectedRow = cell.Row;
            Commit();
            return;
        }

        // A marker owns its row, so a click there selects it instead of writing a note over
        // something that took a deliberate keystroke to place.
        if (pattern.Rows[cell.Row] is LoopStartRow or LoopEndRow)
        {
            _selectedRow = cell.Row;
            Invalidate();
            return;
        }

        var existing = _notes.FirstOrDefault(n => cell.Row >= n.Row && cell.Row < n.Row + n.Length);

        // The last few pixels of a block are its handle. A new note runs to the end of the
        // pattern, because every row after it is a hold and a hold is the note still sounding -
        // so shortening one is the commonest edit there is, and it should not need the keyboard.
        if (existing is not null && KeyForNote(existing.Note) == cell.Key && OverEndHandle(existing, e.X))
        {
            BeginResize(existing, together: false);
            return;
        }

        if (existing is not null && KeyForNote(existing.Note) == cell.Key)
        {
            _selectedRow = existing.Row;
            Audition(existing.Note, existing.InstrumentNumber);
            // The wheel button names the instrument at once, without the wait.
            if (e.Button == MouseButtons.Middle)
                ShowInstrumentLabel(existing, e.X);

            Invalidate();
            return;
        }

        // Drawn, not stamped: the button going down is where the note starts and the button
        // coming up is where it ends. Without this a new note ran to the end of the pattern,
        // because every row after it was a hold - so a click with no drag is one beat long, and a
        // drag reaches exactly the beat it is let go on, with the gate-off after it that stops the
        // voice there. The same resize the end handle uses, so it obeys the same limits: it stops
        // at the next note rather than writing over it.
        var placed = NoteForKey(cell.Key);
        PatternNotes.SetNote(pattern, cell.Row, placed, _defaultInstrument);
        _selectedRow = cell.Row;
        Audition(placed, _defaultInstrument);

        Reread();
        if (_notes.FirstOrDefault(n => n.Row == cell.Row) is { } drawn)
        {
            BeginResize(drawn, together: false);
            _resizeChanged = true;     // the note itself is already an edit, drag or no drag
            DragNoteEnd(e.X);
        }

        Invalidate();

        base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Shift scrolls sideways, as it does everywhere else. Typed as the base class because
        // HScrollBar and VScrollBar share one only through it.
        SlimScrollBar bar = ModifierKeys.HasFlag(Keys.Shift) ? _scrollX : _scrollY;
        if (!bar.Enabled)
            return;

        int step = bar.SmallChange * 2 * Math.Sign(-e.Delta);
        bar.Value = Math.Clamp(bar.Value + step, bar.Minimum, Math.Max(0, bar.Maximum - bar.LargeChange + 1));
        HideInstrumentLabel();
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Delete or Keys.Return
        or Keys.Insert or Keys.End;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Before the selected-row guard: a selection of notes or a range is something to act on
        // even when no single row is picked.
        if (_pattern is { } selected && HandleToolKey(e, selected))
        {
            base.OnKeyDown(e);
            return;
        }

        if (_pattern is not { } pattern || _selectedRow < 0)
        {
            base.OnKeyDown(e);
            return;
        }

        if (HandleMarkerKey(e, pattern) || HandleRowKey(e, pattern))
        {
            base.OnKeyDown(e);
            return;
        }

        // The tracker's own key for it, and the one thing the roll could not say: silence from
        // here. Shortening a note gives a release at its end, but a row with nothing before it
        // had no way to carry one.
        if (e.KeyCode is Keys.Return or Keys.CapsLock)
        {
            PatternNotes.SetRelease(pattern, _selectedRow);
            Commit();
            e.Handled = true;
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
                    var moved = NoteForKey(key + (e.KeyCode == Keys.Up ? -1 : 1));
                    PatternNotes.Move(pattern, _selectedRow, _selectedRow, moved);
                    Commit();

                    // Sounds at its new pitch for as long as the arrow is held, the same as a
                    // note dragged up or down - and a held arrow walking it along plays each step.
                    Audition(moved, note.InstrumentNumber);
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

    /// <summary>
    /// What the pattern itself is made of, rather than what is drawn in it: Insert opens a row,
    /// Shift+Delete closes one, Shift+End cuts everything after the selected row, and Shift+C and
    /// Shift+V copy and paste the whole pattern. The tracker's own keys, and the clipboard is
    /// shared with it, so a pattern copied in either view pastes into the other.
    ///
    /// Returns true when the key belonged here. Note that plain Delete is left alone - it removes
    /// the note under the cursor, which is the commoner thing to want in a roll.
    /// </summary>
    private bool HandleRowKey(KeyEventArgs e, Pattern pattern)
    {
        bool takesRow = e.KeyCode is Keys.Insert
                        || (e.KeyCode == Keys.End && e.Shift)
                        || (e.KeyCode == Keys.Delete && (e.Shift || _selectedRow >= pattern.Rows.Count
                                                         || pattern.Rows[_selectedRow] is not (NoteRow or NoteOffRow)));
        if (takesRow && RefuseOnLinkedPass())
        {
            e.Handled = true;
            return true;
        }

        switch (e.KeyCode)
        {
            case Keys.Insert when _selectedRow >= 0:
                pattern.Rows.Insert(Math.Min(_selectedRow, pattern.Rows.Count), new HoldRow());
                break;

            // Delete takes the event on the row if there is one, and the row itself if there is
            // not - so on a row picked in the beat bar, where there is nothing to take, it is the
            // other half of Insert. Shift+Delete always takes the row.
            case Keys.Delete when !e.Shift && _selectedRow < pattern.Rows.Count
                                 && pattern.Rows[_selectedRow] is NoteRow or NoteOffRow:
                PatternNotes.Remove(pattern, _selectedRow);
                break;

            case Keys.Delete when _selectedRow >= 0 && _selectedRow < pattern.Rows.Count
                                  && pattern.Rows.Count > 1:
                pattern.Rows.RemoveAt(_selectedRow);
                _selectedRow = Math.Min(_selectedRow, pattern.Rows.Count - 1);
                break;

            // A pattern of nothing would have no row to put anything back on, so the first row
            // stays whatever happens.
            case Keys.End when e.Shift && _selectedRow >= 0 && _selectedRow < pattern.Rows.Count - 1:
                pattern.Rows.RemoveRange(_selectedRow + 1, pattern.Rows.Count - _selectedRow - 1);
                break;

            case Keys.C when e.Shift:
                PatternClipboard.Copy(pattern);
                e.Handled = true;
                return true;    // read-only: nothing to commit

            case Keys.V when e.Shift:
                if (!PatternClipboard.PasteInto(pattern, Math.Max(0, _selectedRow)))
                {
                    return false;
                }

                break;

            default:
                return false;
        }

        Commit();
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// The loop markers: Ctrl+L puts an LS on the selected row, Ctrl+E an LE - the tracker's own
    /// bindings. On a row that already carries one, the arrows change an LE's repeat count and
    /// Delete takes the marker away.
    ///
    /// Handled before the note keys because a marker owns its row outright - there is no note there
    /// to move or shorten, and falling through would leave the row unreachable from the keyboard.
    /// Returns true when the key belonged here.
    /// </summary>
    private int _loopDigit = -1;
    private DateTime _loopDigitTime;

    /// <summary>An LE is selected in a focused roll: the digit keys type its count rather than play.</summary>
    public bool LoopEndSelected =>
        _pattern is { } pattern && _selectedRow >= 0 && _selectedRow < pattern.Rows.Count
        && pattern.Rows[_selectedRow] is LoopEndRow;

    /// <summary>A later pass of an unrolled loop is the first pass again, so what changes the rows
    /// themselves - their number, the loop markers - is made there. Says so and returns true when
    /// the last click was on a later pass.</summary>
    private bool RefuseOnLinkedPass()
    {
        if (!_unrollLoops || _selectedPass <= 1)
            return false;

        Notice?.Invoke($"This is pass {_selectedPass} of a loop - insert, delete and loop changes go in pass 1");
        return true;
    }

    private bool HandleMarkerKey(KeyEventArgs e, Pattern pattern)
    {
        if (e.Control && e.KeyCode is Keys.L or Keys.E && RefuseOnLinkedPass())
        {
            e.Handled = true;
            return true;
        }

        var marker = PatternNotes.Markers(pattern).FirstOrDefault(m => m.Row == _selectedRow);

        switch (e.KeyCode)
        {
            // Ctrl+L and Ctrl+E, the tracker's own bindings. Bare S and E used to do this, but
            // they are also C# and E on the piano keys, and the roll plays those now.
            // Inserted as rows of their own - LS before the selected row, LE after it - so no note
            // loses a row to them and the loop is as long as the rows it brackets. The marker is
            // selected afterwards, ready for its count to be typed.
            case Keys.L when e.Control:
                if (PatternNotes.InsertLoopStart(pattern, _selectedRow) is var start and >= 0)
                {
                    _selectedRow = start;
                    Commit();
                }

                e.Handled = true;
                return true;

            case Keys.E when e.Control:
                if (PatternNotes.InsertLoopEnd(pattern, _selectedRow, null) is var end and >= 0)
                {
                    _selectedRow = end;
                    _loopDigit = -1;
                    Commit();
                }

                e.Handled = true;
                return true;

            // The count typed straight into a selected LE: two digits within a moment make one
            // number, the way a clip's pattern is typed in the arrange lanes. 0 is a bare LE, which
            // loops forever.
            case >= Keys.D0 and <= Keys.D9 or >= Keys.NumPad0 and <= Keys.NumPad9
                when marker is { IsStart: false } && !e.Control && !e.Alt:
            {
                int digit = e.KeyCode >= Keys.NumPad0 ? e.KeyCode - Keys.NumPad0 : e.KeyCode - Keys.D0;
                bool combine = _loopDigit >= 0 && (DateTime.UtcNow - _loopDigitTime) < TimeSpan.FromMilliseconds(800);
                int count = combine ? (_loopDigit * 10) + digit : digit;
                _loopDigit = combine ? -1 : digit;
                _loopDigitTime = DateTime.UtcNow;

                PatternNotes.SetLoopEnd(pattern, _selectedRow, count);
                Commit();
                e.Handled = e.SuppressKeyPress = true;
                return true;
            }

            case Keys.Delete when marker is not null:
                PatternNotes.RemoveMarker(pattern, _selectedRow);
                Commit();
                e.Handled = true;
                return true;

            // A bare LE loops forever, which is repeat count 0 in the byte - so counting up from
            // nothing lands on 1 and counting down off 1 goes back to bare.
            case Keys.Up or Keys.Down when marker is { IsStart: false }:
            {
                int next = (marker.RepeatCount ?? 0) + (e.KeyCode == Keys.Up ? 1 : -1);
                PatternNotes.SetLoopEnd(pattern, _selectedRow, next);
                Commit();
                e.Handled = true;
                return true;
            }

            default:
                return marker is not null && e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down;
        }
    }

    /// <summary>Re-reads the pattern it is already showing - for when something outside the roll
    /// changed it, such as a clip dragged longer in the arrange lanes.</summary>
    public void ReloadPattern()
    {
        Reread();
        UpdateScrollRange();
        Invalidate();
    }

    private void Commit()
    {
        Reread();
        Invalidate();
        Edited?.Invoke();
    }

    private Rectangle GridToggleBounds => new(Width - _scrollY.Width - 168, 3, 156, 20);

    /// <summary>Where the header's name and row count end. The bar numbers share the strip with
    /// them, and none is drawn under them.</summary>
    private int _headerTextRight;

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

        // Everything of the notes stops above the slide lane.
        var state = g.Save();
        g.SetClip(new Rectangle(0, 0, Width, Math.Max(0, GridBottom)));
        DrawLanes(g);
        DrawColumns(g);
        DrawLinkedPasses(g);
        DrawBlocks(g);
        DrawToolOverlays(g);
        DrawBeatBar(g);
        DrawPassTags(g);
        DrawKeyboard(g);
        DrawInstrumentLabel(g);
        g.Restore(state);

        DrawSlideLane(g);
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
            string meta = $"{channel}{pattern.Rows.Count} rows · {_notes.Count} notes";
            int metaX = 56;
            g.DrawString(meta, metaFont, metaInk, metaX, 6);
            _headerTextRight = metaX + (int)Math.Ceiling(g.MeasureString(meta, metaFont).Width);
        }
        else
        {
            _headerTextRight = 0;
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
            hintFont, hintInk, new RectangleF(0, box.Y + 5, box.X - 8, 12), far);
    }

    private void DrawRulerNumbers(Graphics g)
    {
        if (_pattern is not { } pattern)
            return;

        using var font = new Font("Consolas", 7f);
        using var strong = new SolidBrush(DawPalette.TextDim);
        using var tick = new Pen(DawPalette.BorderSoft);

        int every = _grid == RollGrid.Beats ? 16 : 4;

        _ = pattern;

        for (int column = 0; column < ColumnCount; column += every)
        {
            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            if (x < KeyboardWidth || x > Width || x < _headerTextRight)
                continue;

            g.DrawLine(tick, x, 14, x, RulerHeight);
            string label = _grid == RollGrid.Beats ? ((column / 16) + 1).ToString() : column.ToString("X2");
            g.DrawString(label, font, strong, x + 3, 6);
        }
    }

    private void DrawLanes(Graphics g)
    {
        int top = GridTop;
        int bottom = GridBottom;

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
        int bottom = GridBottom;

        _ = pattern;

        for (int column = 0; column <= ColumnCount; column++)
        {
            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            if (x < KeyboardWidth || x > Width - _scrollY.Width)
                continue;

            Color colour = _grid == RollGrid.Beats
                ? column % 16 == 0 ? Color.FromArgb(0x2C, 0x38, 0x52)
                    : column % 4 == 0 ? Color.FromArgb(0x1C, 0x24, 0x36) : Color.FromArgb(0x14, 0x1A, 0x27)
                : column % 4 == 0 ? Color.FromArgb(0x22, 0x2C, 0x42) : Color.FromArgb(0x17, 0x1E, 0x2C);

            using var pen = new Pen(colour);
            g.DrawLine(pen, x, top, x, bottom);
        }

    }

    /// <summary>
    /// The beat bar: one cell per row, tinted by where it falls in the bar, with the selected row
    /// lit and any loop marker's label sitting in its own cell.
    ///
    /// Markers used to run the full height of the grid, which put a band across whatever was
    /// playing underneath them. Here they are out of the way of the notes and still the easiest
    /// thing on screen to hit.
    /// </summary>
    private void DrawBeatBar(Graphics g)
    {
        if (_pattern is not { } pattern)
        {
            return;
        }

        int left = KeyboardWidth;
        int right = Width - _scrollY.Width;

        using (var backing = new SolidBrush(DawPalette.Inset))
            g.FillRectangle(backing, left, BeatBarTop, right - left, BeatBarHeight);

        _ = pattern;

        for (int column = 0; column < ColumnCount; column++)
        {
            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            if (x + RowWidth < left || x > right)
            {
                continue;
            }

            int row = RowOf(column);
            int width = Math.Min(RowWidth, right - x);
            var cell = new Rectangle(x, BeatBarTop, width, BeatBarHeight);

            // Every fourth beat column is a beat and every sixteenth a bar, the same counting the
            // grid behind it uses - so a glance at the strip says where you are without reading
            // the ruler.
            Color fill = column % 16 == 0
                ? Color.FromArgb(0x24, 0x2E, 0x45)
                : column % 4 == 0 ? Color.FromArgb(0x1A, 0x22, 0x33) : Color.FromArgb(0x12, 0x17, 0x22);

            using (var brush = new SolidBrush(fill))
                g.FillRectangle(brush, cell);

            using (var edge = new Pen(Color.FromArgb(0x0A, 0x0D, 0x14)))
                g.DrawLine(edge, x, BeatBarTop, x, BeatBarTop + BeatBarHeight);

            if (row == _selectedRow)
            {
                using var lit = new SolidBrush(DawPalette.WithAlpha(DawPalette.Oscillator, 120));
                g.FillRectangle(lit, cell);
            }
        }

        foreach (var (_, x, label, selected) in VisibleMarkers())
        {
            var tab = new Rectangle(x, BeatBarTop, RowWidth, BeatBarHeight);

            using (var fill = new SolidBrush(selected
                       ? DawPalette.Effect
                       : DawPalette.WithAlpha(DawPalette.Effect, 130)))
                g.FillRectangle(fill, tab);

            using (var edge = new Pen(DawPalette.Effect))
                g.DrawRectangle(edge, tab);

            using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
            using var ink = new SolidBrush(selected ? Color.FromArgb(0x10, 0x0B, 0x02) : DawPalette.Text);
            g.DrawString(label, font, ink, x + 1, BeatBarTop + 2);
        }

        using (var frame = new Pen(DawPalette.Border))
            g.DrawLine(frame, left, BeatBarTop, right, BeatBarTop);
    }

    /// <summary>Every loop marker currently on screen, with where it sits and what it says. The
    /// repeat count rides with the label: a bare LE loops forever and a numbered one plays the
    /// section that many times in total, which is too different to leave unsaid.</summary>
    private IEnumerable<(int Row, int X, string Label, bool Selected)> VisibleMarkers()
    {
        if (_pattern is not { } pattern)
        {
            yield break;
        }

        for (int row = 0; row < pattern.Rows.Count; row++)
        {
            if (pattern.Rows[row] is not (LoopStartRow or LoopEndRow))
            {
                continue;
            }

            // On the column it brackets, not on one of its own - an LS belongs to the row after
            // it and an LE to the row before, which is where the tracker prints them too.
            int x = KeyboardWidth + (ColumnOf(row) * RowWidth) - _scrollX.Value;
            if (x < KeyboardWidth || x > Width - _scrollY.Width)
            {
                continue;
            }

            string label = pattern.Rows[row] is LoopStartRow
                ? "LS"
                : pattern.Rows[row] is LoopEndRow { RepeatCount: { } count } ? $"LE{count}" : "LE";

            yield return (row, x, label, row == _selectedRow);
        }
    }

    /// <summary>A loop's later passes: a violet wash over their columns and "↻2/4" in the beat bar
    /// where each starts - the same rows again, linked to the first pass.</summary>
    private void DrawLinkedPasses(Graphics g)
    {
        if (!_unrollLoops)
            return;

        var linked = Color.FromArgb(0x7F, 0x77, 0xDD);
        using var wash = new SolidBrush(Color.FromArgb(26, linked));
        using var rail = new SolidBrush(Color.FromArgb(150, linked));
        using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
        using var ink = new SolidBrush(Color.FromArgb(220, 0xC8, 0xC2, 0xF4));

        for (int column = 0; column < _columnInfo.Length; column++)
        {
            var info = _columnInfo[column];
            if (!info.Linked)
                continue;

            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            if (x + RowWidth < KeyboardWidth || x > Width - _scrollY.Width)
                continue;

            g.FillRectangle(wash, x, GridTop, RowWidth, GridBottom - GridTop);
            if (info.PassStart)
                g.FillRectangle(rail, x, GridTop, 2, GridBottom - GridTop);
        }
    }

    /// <summary>"\u21BB2/4" in the beat bar where each later pass starts - over the bar's own cells.</summary>
    private void DrawPassTags(Graphics g)
    {
        if (!_unrollLoops)
            return;

        var linked = Color.FromArgb(0x7F, 0x77, 0xDD);
        using var rail = new SolidBrush(Color.FromArgb(200, linked));
        using var font = new Font("Segoe UI", 6.5f, FontStyle.Bold);
        using var ink = new SolidBrush(Color.FromArgb(0xC8, 0xC2, 0xF4));

        for (int column = 0; column < _columnInfo.Length; column++)
        {
            var info = _columnInfo[column];
            if (!info.PassStart)
                continue;

            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            if (x + RowWidth < KeyboardWidth || x > Width - _scrollY.Width)
                continue;

            g.FillRectangle(rail, x, BeatBarTop, 2, GridTop - BeatBarTop);
            string tag = info.Passes is > 0 and < 10 ? $"\u21BB{info.Pass}/{info.Passes}" : $"\u21BB{info.Pass}";
            g.DrawString(tag, font, ink, x + 3, BeatBarTop + 1);
        }
    }

    /// <summary>A note's copies on a loop's later passes: lighter, dashed - the same note, which an
    /// edit on any of them changes.</summary>
    private void DrawLinkedNotes(Graphics g)
    {
        if (!_unrollLoops)
            return;

        var byRow = _notes.ToDictionary(n => n.Row);
        for (int column = 0; column < _columnInfo.Length; column++)
        {
            var info = _columnInfo[column];
            if (!info.Linked || !byRow.TryGetValue(info.Row, out var note) || KeyForNote(note.Note) is not (var key and >= 0))
                continue;

            int length = 1;
            while (column + length < _columnInfo.Length && length < note.Length && _columnInfo[column + length].Row == note.Row + length)
                length++;

            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            int y = GridTop + (key * KeyHeight) - _scrollY.Value;
            var bounds = new Rectangle(x + 1, y + 1, Math.Max(3, (length * RowWidth) - 2), KeyHeight - 2);
            if (bounds.Right < KeyboardWidth || bounds.X > Width - _scrollY.Width || bounds.Bottom < GridTop || bounds.Y > GridBottom)
                continue;

            var colour = DawPalette.ForInstrument(note.InstrumentNumber);
            using (var fill = new SolidBrush(Color.FromArgb(120, colour)))
                g.FillRectangle(fill, bounds);
            using (var border = new Pen(Color.FromArgb(200, colour)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                g.DrawRectangle(border, bounds);
        }
    }

    private void DrawBlocks(Graphics g)
    {
        DrawLinkedNotes(g);

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
            bool selected = note.Row == _selectedRow || _selection.Contains(note.Row);

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

        // The glides go on top of every block, so a note sliding into another's lane stays visible.
        foreach (var note in _notes)
        {
            if (KeyForNote(note.Note) is var key and >= 0 && BlockBounds(note.Row, note.Length, key) is { } bounds)
                DrawGlide(g, note, bounds, DawPalette.ForInstrument(note.InstrumentNumber));
        }

        _ = top;
    }

    /// <summary>The resting pointer's note named: its instrument's number and name, in the
    /// instrument's colour, above the note's lane - below it on the top lane, where above is the
    /// beat bar. A note edited since the pointer came to rest is no longer the one named.</summary>
    private void DrawInstrumentLabel(Graphics g)
    {
        if (!_labelShown || _labelNote is not { } note || !_notes.Contains(note) || KeyForNote(note.Note) is not (var key and >= 0))
            return;

        var instrument = _song?.Instruments.FirstOrDefault(i => i.Number == note.InstrumentNumber);
        string text = instrument is null
            ? $"{note.InstrumentNumber:D2}"
            : $"{note.InstrumentNumber:D2} {InstrumentNames.Display(instrument)}";

        using var font = new Font("Segoe UI", 8.25f, FontStyle.Bold);
        var size = Size.Ceiling(g.MeasureString(text, font));
        int width = size.Width + 8, height = size.Height + 2;
        int lane = GridTop + (key * KeyHeight) - _scrollY.Value;
        int right = Math.Max(KeyboardWidth + 2, Width - _scrollY.Width - width - 2);
        int x = Math.Clamp(_labelX - (width / 2), KeyboardWidth + 2, right);
        int y = lane - height - 2 >= GridTop ? lane - height - 2 : lane + KeyHeight + 2;
        var box = new Rectangle(x, y, width, height);

        var colour = DawPalette.ForInstrument(note.InstrumentNumber);
        using (var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Background, 235)))
            g.FillRectangle(fill, box);
        using (var border = new Pen(colour))
            g.DrawRectangle(border, box);
        using var ink = new SolidBrush(colour);
        g.DrawString(text, font, ink, x + 4, y + 1);
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

        int x = KeyboardWidth + (ColumnOf(row) * RowWidth) - _scrollX.Value;
        int y = GridTop + (key * KeyHeight) - _scrollY.Value;
        int width = (length * RowWidth) - 2;
        int height = KeyHeight - 2;

        if (x + width < KeyboardWidth || x > Width - _scrollY.Width
            || y + height < GridTop || y > GridBottom)
            return null;

        return new Rectangle(x + 1, y + 1, Math.Max(3, width), height);
    }

    /// <summary>Lights a key while its note is actually being held, from the MIDI keyboard or
    /// from anywhere else that sounds a held note. Repaints only the keyboard strip, because this
    /// runs on every key-down and key-up and the grid beside it has not changed.</summary>
    public void SetNoteSounding(NoteName note, bool sounding)
    {
        if (sounding ? !_heldKeys.Add(note) : !_heldKeys.Remove(note))
        {
            return;
        }

        Invalidate(new Rectangle(0, GridTop, KeyboardWidth + 1, Math.Max(0, Height - GridTop)));
    }

    /// <summary>Every key out at once - for a panic silence, where no key-up is coming.</summary>
    public void ClearSoundingNotes()
    {
        if (_heldKeys.Count == 0)
        {
            return;
        }

        _heldKeys.Clear();
        Invalidate(new Rectangle(0, GridTop, KeyboardWidth + 1, Math.Max(0, Height - GridTop)));
    }

    private void DrawKeyboard(Graphics g)
    {
        int top = GridTop;
        int bottom = GridBottom;

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
            bool lit = _heldKeys.Count > 0 && _heldKeys.Contains(NoteForKey(key));

            Color face = lit
                ? black ? ControlPaint.Dark(SoundingKey, 0.12f) : SoundingKey
                : black ? Color.FromArgb(0x18, 0x1E, 0x2C) : Color.FromArgb(0xC0, 0xC6, 0xD4);

            using (var fill = new SolidBrush(face))
                g.FillRectangle(fill, 2, y + 1, KeyboardWidth - 6, KeyHeight - 2);

            // A lit key spills into the grid, so a held note is visible without looking away from
            // where the notes are.
            if (lit)
            {
                using var spill = new SolidBrush(DawPalette.WithAlpha(SoundingKey, 90));
                g.FillRectangle(spill, KeyboardWidth + 1, y + 1, 6, KeyHeight - 2);
            }

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

    /// <summary>One more column of a stroke: every column the pointer passed since the last one
    /// gets a rate on the straight line between them, so a quick sweep leaves no gaps.</summary>
    private void ContinueSlideStroke(MouseEventArgs e)
    {
        if (_slideStroke is not { } stroke)
            return;

        int column = Math.Clamp(ColumnAtX(e.X), 0, Math.Max(0, ColumnCount - 1));
        int rate = (e.Button & MouseButtons.Right) != 0 ? 0 : SlideRateAtY(e.Y);
        var (lastColumn, lastRate) = _slideStrokeLast;

        int steps = Math.Abs(column - lastColumn);
        for (int i = 0; i <= steps; i++)
        {
            int c = lastColumn + (Math.Sign(column - lastColumn) * i);
            double t = steps == 0 ? 1 : i / (double)steps;
            stroke[c] = (int)Math.Round(lastRate + ((rate - lastRate) * t));
        }

        _slideStrokeLast = (column, rate);
        _slideHover = (column, rate);
        Invalidate();
    }

    /// <summary>"1xx" or "2xx", the way the row will read in the tracker.</summary>
    private static string SlideText(int rate) => new SlideRow(rate).Text;

    /// <summary>
    /// The slide lane: a bar per column for the rate in force on that row - up for "1xx", down for
    /// "2xx" - with the rows where the rate changes, the ones that become slide rows and cost two
    /// bytes, marked and labelled. A note's own row and a note off cannot take a slide, and show
    /// as a small dot - the rows after a note off can, since the release slides on.
    /// </summary>
    private void DrawSlideLane(Graphics g)
    {
        int top = GridBottom;
        int bottom = Height - _scrollX.Height;
        int right = Width - _scrollY.Width;
        if (bottom - top < 8)
            return;

        using (var fill = new SolidBrush(Color.FromArgb(0x0C, 0x10, 0x18)))
            g.FillRectangle(fill, 0, top, Width, bottom - top);
        using (var border = new Pen(DawPalette.Border))
        {
            g.DrawLine(border, 0, top, Width, top);
            g.DrawLine(border, KeyboardWidth, top, KeyboardWidth, bottom);
        }

        using (var mid = new Pen(DawPalette.WithAlpha(DawPalette.Effect, 40)))
            g.DrawLine(mid, KeyboardWidth, SlideLaneMid, right, SlideLaneMid);

        using (var labelFont = new Font("Segoe UI", 7f, FontStyle.Bold))
        using (var labelInk = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 170)))
        using (var hintFont = new Font("Consolas", 6.5f))
        using (var hintInk = new SolidBrush(DawPalette.TextFaint))
        {
            g.DrawString("SLIDE", labelFont, labelInk, 8, top + 4);
            g.DrawString("1xx up", hintFont, hintInk, 8, SlideLaneMid - 14);
            g.DrawString("2xx down", hintFont, hintInk, 8, SlideLaneMid + 4);
        }

        if (_pattern is not { } pattern)
            return;

        var clip = g.Save();
        g.SetClip(new Rectangle(KeyboardWidth + 1, top + 1, Math.Max(0, right - KeyboardWidth - 1), bottom - top - 1));

        // The selected stretch, under the bars.
        if (_slideSelection is { } range)
        {
            int x0 = KeyboardWidth + (range.From * RowWidth) - _scrollX.Value;
            int x1 = KeyboardWidth + ((range.To + 1) * RowWidth) - _scrollX.Value;
            using (var fill = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 36)))
                g.FillRectangle(fill, x0, top + 1, x1 - x0, bottom - top - 2);
            using (var edge = new Pen(DawPalette.WithAlpha(DawPalette.Effect, 170)))
                g.DrawRectangle(edge, x0, top + 2, x1 - x0 - 1, bottom - top - 5);
        }

        using var barInk = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 110));
        using var changeInk = new SolidBrush(DawPalette.Effect);
        using var lockedInk = new SolidBrush(DawPalette.WithAlpha(DawPalette.TextFaint, 120));
        using var valueFont = new Font("Consolas", 6.5f);
        using var valueInk = new SolidBrush(DawPalette.WithAlpha(DawPalette.Effect, 220));

        int previousRate = 0;
        for (int column = 0; column < ColumnCount; column++)
        {
            int row = RowOf(column);
            if (row < 0 || row >= _slideRates.Length || row >= _slidable.Length || row >= pattern.Rows.Count)
                continue;

            int rate = _slideStroke is { } stroke && stroke.TryGetValue(column, out int drawn) && _slidable[row]
                ? drawn
                : _slideRates[row];
            bool changes = _slidable[row] && rate != previousRate;
            previousRate = pattern.Rows[row] is NoteRow ? 0 : rate;

            int x = KeyboardWidth + (column * RowWidth) - _scrollX.Value;
            if (x + RowWidth < KeyboardWidth || x > right)
                continue;

            if (!_slidable[row])
            {
                g.FillEllipse(lockedInk, x + (RowWidth / 2) - 1.5f, SlideLaneMid - 1.5f, 3, 3);
                continue;
            }

            if (rate == 0 && !changes)
                continue;

            float offset = SlideOffsetY(rate);
            float y = Math.Min(SlideLaneMid, SlideLaneMid + offset);
            float h = Math.Max(1.5f, Math.Abs(offset));
            g.FillRectangle(changes ? changeInk : barInk, x + 3, y, RowWidth - 6, h);

            // Where it changes, the value - the row the tracker will show.
            if (changes)
            {
                float labelY = rate >= 0 ? SlideLaneMid + 2 : SlideLaneMid - 11;
                g.DrawString(SlideText(rate), valueFont, valueInk, x, labelY);
            }
        }

        // What a click here would write.
        if (_tool == RollTool.Draw && _slideHover is { } hover && _slideStroke is null && RowOf(hover.Column) is var hoverRow and >= 0
            && hoverRow < _slidable.Length && _slidable[hoverRow])
        {
            int x = KeyboardWidth + (hover.Column * RowWidth) - _scrollX.Value;
            using var ghost = new Pen(DawPalette.WithAlpha(DawPalette.Effect, 160)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            float offset = SlideOffsetY(hover.Rate);
            g.DrawRectangle(ghost, x + 3, Math.Min(SlideLaneMid, SlideLaneMid + offset), RowWidth - 7, Math.Max(1, Math.Abs(offset)));
            using var tipFont = new Font("Consolas", 7f, FontStyle.Bold);
            using var tipInk = new SolidBrush(DawPalette.Effect);
            g.DrawString(SlideText(hover.Rate), tipFont, tipInk, x + RowWidth, top + 3);
        }

        g.Restore(clip);
    }

    /// <summary>
    /// The glide a note's slides give it, drawn over the note: frame by frame the way the player
    /// adds the rate to the 16-bit frequency, turned back into semitones on the roll. A note starts
    /// unslid, and on each row's first frame the rate from before that row's slide row still
    /// applies - the player moves the pitch before it reads the row.
    /// </summary>
    private void DrawGlide(Graphics g, NoteBlock note, Rectangle bounds, Color colour)
    {
        if (_song is null || note.Row >= _slideRates.Length)
            return;

        int framesPerRow = (int)Math.Round(_song.AverageFramesPerRow);    // a funk pair drawn evenly
        var (lo, hi) = MagSid.Core.NotePalTable.GetFreq(note.Note);
        double baseFrequency = (hi << 8) | lo;
        if (baseFrequency <= 0)
            return;

        var offsets = PatternSlides.Glide(_slideRates, note.Row, note.Length, framesPerRow);
        if (offsets.Length < 2 || offsets.All(o => o == 0))
            return;

        // From the last unmoved frame on - the stretch before it would only cross out the name.
        int first = Math.Max(0, Array.FindIndex(offsets, o => o != 0) - 1);
        var points = new List<PointF>(offsets.Length + 1);
        float centreY = bounds.Y + (bounds.Height / 2f);
        for (int i = first; i < offsets.Length; i++)
        {
            int row = note.Row + (i / framesPerRow);
            int frame = i % framesPerRow;
            int columnX = KeyboardWidth + (ColumnOf(row) * RowWidth) - _scrollX.Value;
            double semitones = 12 * Math.Log2(Math.Max(1, baseFrequency + offsets[i]) / baseFrequency);
            float x = columnX + (frame * RowWidth / (float)framesPerRow);
            points.Add(new PointF(x, centreY - (float)(semitones * KeyHeight)));
        }

        points.Add(new PointF(bounds.Right, points[^1].Y));

        var smooth = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var glow = new Pen(DawPalette.WithAlpha(Color.White, 60), 4f))
            g.DrawLines(glow, points.ToArray());
        using (var pen = new Pen(ControlPaint.Light(colour, 0.6f), 1.8f))
            g.DrawLines(pen, points.ToArray());
        using (var dot = new SolidBrush(Color.White))
            g.FillEllipse(dot, points[^1].X - 2.5f, points[^1].Y - 2.5f, 5, 5);
        g.SmoothingMode = smooth;
    }

    private void DrawPlayhead(Graphics g)
    {
        if (_playRow < 0)
            return;

        int x = PlayheadX(_playRow, _playFraction);
        if (x < KeyboardWidth || x > Width - _scrollY.Width)
            return;

        using var pen = new Pen(Color.FromArgb(0xF0, 0x4A, 0x6E), 1.5f);
        g.DrawLine(pen, x, GridTop, x, Height - _scrollX.Height);
    }
}
