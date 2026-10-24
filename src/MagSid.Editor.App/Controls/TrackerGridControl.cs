using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MagSid.Core.Model;
using MagSid.Core.Validation;
using MagSid.Playback;

namespace MagSid.Editor.App.Controls;

/// <summary>
/// GoatTracker-style tracker grid: all three channels shown side by side for a chosen
/// "song position" (an index into each channel's ChannelOrder.Tokens - a pure UI navigation
/// concept, not a new Core model addition). Since patterns can have independent lengths per
/// channel (the whole point of the v7 rearchitecture), a channel whose pattern runs out of
/// rows before the others chains straight into the next pattern in *its own* song-order
/// (looping via RestartPosition, same as real playback would) rather than showing blank -
/// see <see cref="ResolveDisplayRow"/>/<see cref="ResolveEditTarget"/>. Editing follows the
/// same chain: typing on a row that has scrolled into the next pattern edits that pattern
/// directly. Owner-drawn and keyboard-driven (piano-key note entry, hex instrument entry)
/// rather than DataGridView/dropdown-based, replacing PatternGridControl.
///
/// Each channel also carries its own narrow "order track" column (leftmost within that
/// channel's group), which is the *only* editor for ORDER_CHn - it replaced a separate
/// per-channel order panel, so everything that panel could do has to be reachable here.
/// It shows "NN→" wherever the channel's chain crosses into a different order-list entry (the
/// same transitions already flagged for the "P0N" prefix, naming which ORDER_CHn position
/// produced it), and "RSTNN" on the channel's last row, where the list runs out and
/// RestartPosition takes over. The arrow is right-aligned and deliberately has no pattern
/// number after it: the Marker cell immediately to its right already draws that, so the two
/// read across as "00→P01" without either column repeating the other.
///
/// Clicking a cell calls <see cref="SetSongPosition"/>, which is shared across all three
/// channels (there's no per-channel song-position concept in the model), so it moves every
/// column's view, not just the one clicked - consistent with the toolbar's Prev/Next buttons.
/// With the cursor on the field (leftmost, left of Marker): digits retype the entry's pattern
/// number, or on the loop row the restart position (<see cref="EnterOrderDigit"/>); Insert
/// adds an entry, appending when on the loop row (<see cref="InsertOrderEntryAtCursor"/>);
/// Delete removes one (<see cref="RemoveOrderEntryAtCursor"/>); Ctrl+Up/Ctrl+Down reorder
/// (<see cref="MoveOrderEntryAtCursor"/>). All of them act only on rows where the track draws
/// something, so the field is inert exactly where it looks inert.
///
/// That last rule is the general one for typing anywhere in this grid: a digit edits whatever
/// the cell under the cursor is currently displaying. The Marker column is where it shows
/// most, since that one cell shows a pattern number ("PNN") on a boundary row and a loop's
/// repeat count ("LE04") elsewhere, and digits follow it either way - see
/// <see cref="EnterMarkerDigit"/>. So a pattern number can be retyped from either the order
/// track or the Marker cell beside it, both being views of the same order entry.
/// </summary>
public sealed class TrackerGridControl : Control
{
    private enum CursorField { Order, Marker, Note, Instrument }

    // Sized to the widest string each column actually paints - measured by rendering it and
    // finding the rightmost lit pixel, not estimated - counting the header labels too, which
    // are the wider ones here since HeaderFont is bold: Order fits "RST01"/"CH1" (38px),
    // Marker "PAT01" (37px, wider than its own "LE04" at 32), Note "NOT" (24px) and Inst
    // "INS" (24px, wider than "01" at 16). Each gets ~6px of slack. Too narrow and GDI+
    // silently paints straight over the next column, with no clipping and no error -
    // "00→P01" overran its old 46px column by a single pixel and came out as "00→P0P01"
    // on top of the Marker cell.
    private const int RowNumWidth = 56;
    private const int OrderColWidth = 44; // "00→" song-order index, or "RST01" on the last row
    private const int MarkerColWidth = 44; // "P01" / "LS" / "LE04" inline prefix, before the note
    private const int NoteColWidth = 30;
    private const int InstColWidth = 30;
    private const int ColumnGap = 10;
    private const int ChannelColWidth = OrderColWidth + MarkerColWidth + NoteColWidth + InstColWidth + ColumnGap;
    private const int GridContentWidth = RowNumWidth + 3 * ChannelColWidth;

    /// <summary>How wide the grid actually draws. Every column here is a fixed pixel size, so
    /// anything past this is dead space - MainForm sizes the window from it rather than
    /// hardcoding a width that drifts out of date the moment a column changes.</summary>
    public const int ContentWidth = GridContentWidth;
    private const int HeaderHeight = 36;
    private const int RowHeight = 18;

    // The shared roles come from EditorPalette rather than being spelled out again here. This
    // grid is where those values were first chosen and EditorPalette was extracted from it, but
    // the extraction never came back to replace the originals - so for a while the same seven
    // colours existed twice, which is exactly the "near-miss copy" EditorPalette's own comment
    // says it exists to prevent. The genuinely local inks below (note/hold/marker and friends)
    // stay here, because nothing else in the app has an opinion about them.
    private static readonly Color ColorBackground = EditorPalette.Background;
    private static readonly Color ColorRowNumber = Color.FromArgb(0x5C, 0x5C, 0x7C);
    private static readonly Color ColorCurrentRow = EditorPalette.Selection;
    private static readonly Color ColorCursorBorder = Color.FromArgb(0xFF, 0xD7, 0x00);
    private static readonly Color ColorNote = EditorPalette.Title;
    private static readonly Color ColorInstrument = Color.FromArgb(0xFF, 0xCC, 0x66);
    private static readonly Color ColorHold = Color.FromArgb(0x4A, 0x4A, 0x5C);
    private static readonly Color ColorNoteOff = Color.FromArgb(0xFF, 0x66, 0x66);
    private static readonly Color ColorLoopMarker = Color.FromArgb(0x66, 0xFF, 0x99);
    private static readonly Color ColorOrderTrack = Color.FromArgb(0x8A, 0xB4, 0xFF);
    private static readonly Color ColorHeaderBackground = EditorPalette.FieldBackground;
    private static readonly Color ColorHeaderText = EditorPalette.Heading;
    private static readonly Color ColorGridLine = EditorPalette.ButtonBackground;
    // Drawn instead of ColorGridLine, just for the row where a channel's chained view crosses
    // into a different pattern - a brighter divider line rather than a filled background band,
    // so the row grid stays easy to follow.
    private static readonly Color ColorPatternBoundary = EditorPalette.Border;

    private static readonly Font GridFont = new("Consolas", 9.5f, FontStyle.Regular);
    private static readonly Font HeaderFont = new("Consolas", 9f, FontStyle.Bold);

    // One brush/pen per colour above, built once. These used to be allocated with "using var"
    // inside the drawing methods - which for the cell-level ones meant one or two GDI+ objects
    // created and finalised *per visible cell, per repaint*: roughly 150 cells at this window
    // size, redrawn at 60fps while a song plays. The colours are all static readonly, so there
    // was never anything per-instance about them to justify the churn. Fonts here were already
    // shared this way; this just extends it to the rest.
    private static readonly Brush BrushBackground = new SolidBrush(ColorBackground);
    private static readonly Brush BrushRowNumber = new SolidBrush(ColorRowNumber);
    private static readonly Brush BrushCurrentRow = new SolidBrush(ColorCurrentRow);
    private static readonly Brush BrushNote = new SolidBrush(ColorNote);
    private static readonly Brush BrushInstrument = new SolidBrush(ColorInstrument);
    private static readonly Brush BrushHold = new SolidBrush(ColorHold);
    private static readonly Brush BrushNoteOff = new SolidBrush(ColorNoteOff);
    // A slide row ("1xx" up, "2xx" down) in the note column - an effect, not a note, so it gets
    // a colour of its own rather than the note's.
    private static readonly Brush BrushSlide = new SolidBrush(EditorPalette.Warning);

    // A later pass of an unrolled loop: a violet wash light enough to keep the current-row band
    // showing through, a rail down its left edge, and a dashed box on the cursor's row in the
    // other passes - the cells an edit at the cursor changes too.
    private static readonly Color ColorLinked = Color.FromArgb(0x7F, 0x77, 0xDD);
    private static readonly Brush BrushLinkedPass = new SolidBrush(Color.FromArgb(34, ColorLinked));
    private static readonly Brush BrushLinkedRail = new SolidBrush(Color.FromArgb(170, ColorLinked));
    private static readonly Pen PenLinkedEcho = new(Color.FromArgb(200, ColorLinked)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
    private static readonly Brush BrushLoopMarker = new SolidBrush(ColorLoopMarker);
    private static readonly Brush BrushOrderTrack = new SolidBrush(ColorOrderTrack);
    private static readonly Brush BrushHeaderBackground = new SolidBrush(ColorHeaderBackground);
    private static readonly Brush BrushHeaderText = new SolidBrush(ColorHeaderText);
    private static readonly Pen PenGridLine = new(ColorGridLine);
    private static readonly Pen PenPatternBoundary = new(ColorPatternBoundary);
    private static readonly Pen PenCursorBorder = new(ColorCursorBorder, 1.5f);

    /// <summary>Right-aligned so the order-track arrow sits against the divider and points
    /// straight at the pattern number beside it, rather than trailing off into empty column.
    /// Shared for the same reason the brushes are - it was being rebuilt per cell.</summary>
    private static readonly StringFormat OrderTrackFormat = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Far,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    private Song? _song;
    private int _songPosition;
    /// <summary>The first row drawn - always placed so the cursor lands on the window's centre
    /// line, the same line the playback view puts the sounding row on. Computed on demand rather
    /// than stored: a stored scroll offset has to be refreshed by every path that can move the
    /// cursor or resize the control, and the one that got missed (startup, before anything had
    /// moved) left the cursor pinned to the top of the window instead of the middle. Derived
    /// state cannot fall out of date.
    ///
    /// Goes negative for the first half-screen of a song, which is deliberate - see the drawing
    /// loop, which leaves those rows blank rather than sliding the cursor up the window.</summary>
    private int TopRow => _cursorRow - VisibleRows / 2;

    private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);

    /// <summary>Rows that touch the grid area, the part-row at the bottom included. VisibleRows
    /// counts only whole ones - right for centring the cursor - but drawing only those left the
    /// strip under the last one empty, and while playing, where the rows glide up by part of a
    /// row, an empty band opened at the bottom instead of the next row coming into view.</summary>
    private int DrawnRows => Math.Max(1, (Height - HeaderHeight + RowHeight - 1) / RowHeight);
    private int _cursorRow;
    private int _cursorChannel;
    private CursorField _cursorField;
    private int _octave = 4;

    // GoatTracker's own EDIT/JAM toggle (Space or F5, depending on the menu's Swap Spacebar
    // Behavior switch - MainForm owns both keys). Edit
    // (the default, matching GoatTracker) means
    // a note key writes into the pattern at the cursor, same as today. Jam turns every
    // pattern-mutating command into a no-op except note entry itself, which keeps playing the
    // instrument live (NoteEntered still fires) but never touches Rows or moves the cursor - so
    // auditioning sounds while browsing or listening along can't ever overwrite what's there.
    // Navigation and Shift+C (copy) stay live in both modes since neither can lose data.
    private bool _editMode = true;

    private int _pendingInstrumentDigit = -1;
    private DateTime _lastInstrumentDigitTime;

    private int _pendingMarkerDigit = -1;
    private DateTime _lastMarkerDigitTime;

    // Order field digit entry has two distinct meanings depending on which row the cursor is
    // on (see EnterOrderDigit) - kept as separate combo-timing state from each other and from
    // the other digit fields so a fast cursor move between contexts can't leak a pending digit
    // meant for one into the other.
    private int _pendingOrderDigit = -1;
    private DateTime _lastOrderDigitTime;
    private int _pendingLoopDigit = -1;
    private DateTime _lastLoopDigitTime;

    // Shift+C/Shift+V: copy/paste an entire pattern's rows. PatternRow subtypes are immutable
    // records, so the clipboard can hold the same row instances directly - no cloning needed,
    // they're never mutated in place, only replaced wholesale via `pattern.Rows[i] = ...`.

    // Playback-follow: while active, each channel's column scrolls independently, centered on
    // its own live position, reusing the exact same ResolveDisplayRow/DrawCell/PrefixForRow
    // machinery as the static edit view (so holds, LS/LE prefixes, everything looks identical
    // to editing). The center row for each channel is computed incrementally as
    // SongSequencer.RowTriggered events arrive (see SetChannelPlaybackAnchor), keyed off
    // OrderEntrySequence (bumped only when AdvanceOrder genuinely moves to a new pattern
    // occurrence, never by an in-pattern LS/LE loop-back): each new occurrence gets its own
    // fresh, ever-growing block of row numbers, so a channel whose order keeps cycling through
    // short patterns (e.g. RestartPosition looping a single pattern) keeps scrolling forward
    // forever instead of re-resolving to the same early rows - while a row *within* the same
    // occurrence (an LS/LE repeat) still resolves back to wherever it was the first time,
    // giving that channel's column a genuine "jump back to LS" each time the loop repeats.
    private readonly int?[] _lastOrderEntrySequence = new int?[3];
    private readonly int[] _occurrenceBaseRow = new int[3];
    private readonly int[] _nextAvailableRow = new int[3];

    /// <summary>The display row each channel's occurrence showed last - so a row a loop plays
    /// again lands on its next pass, not its first.</summary>
    private readonly int[] _lastNth = [-1, -1, -1];
    private bool _isPlaybackActive;

    /// <summary>One RowTriggered event, timestamped in audio samples rather than wall-clock -
    /// see <see cref="_playbackHistory"/>.</summary>
    private readonly record struct PlaybackAnchor(long SampleIndex, Pattern Pattern, int CenterRow, int Ticks);

    // Smooth-scroll: without this, DrawPlaybackFollowView only ever redraws when a new
    // RowTriggered event lands, so content visibly jumps a full RowHeight at a time instead of
    // scrolling. This timer repaints at a fixed, fast cadence *between* those events too;
    // DrawPlaybackFollowView then interpolates each channel's vertical draw offset from 0 up to
    // RowHeight over the elapsed fraction of *that row's own tick count* (one authored row line
    // is one tick - see Code\musicplayer.s / SongSequencer - but a row can set its own duration
    // to more than one, and different rows and different channels routinely disagree, e.g.
    // Lightforce's ported patterns mix duration-3 and duration-7 rows on the same channel).
    //
    // Each channel keeps its whole run of not-yet-superseded anchors, not just the latest one.
    // MainForm's poll timer drains SongPlaybackEngine's *entire* pending queue every tick (see
    // DrainPlaybackEvents), and a channel whose rows are shorter than that poll interval - a
    // duration-1 row is 20ms at Song.Speed=0 - routinely has two or more trigger events land in
    // the same drain. A single "latest anchor" field would overwrite the older one before it was
    // ever drawn even once, which is exactly what made the view look like it was snapping
    // straight to the next note instead of gliding down to it - the intermediate row's own glide
    // never got a rendered frame. Keeping the run and picking the right entry by comparing its
    // SampleIndex against the live audio clock (see AudioClockSamples) at *render* time instead
    // decouples "how many events happened to batch together" from "how many got shown" - every
    // row still gets its own glide, however the polling landed.
    //
    // SampleIndex, not a wall-clock timestamp: it's captured on the audio thread at the instant
    // SongPlaybackEngine actually produces that row's first sample, so it never drifts from what
    // the sound card is really doing - unlike a timestamp taken later, at whatever moment the UI
    // thread's poll timer happened to dequeue the event.
    private readonly List<PlaybackAnchor>[] _playbackHistory = [[], [], []];
    // 10, not the 16 you would write for "60fps". A WinForms Timer is WM_TIMER, which fires on
    // the system tick - 15.6ms - and rounds the requested interval UP to a whole number of
    // ticks. 16 therefore asks for two ticks, not one: measured over 200 frames with a
    // representative 6ms repaint, Interval=16 delivered a mean gap of 29.6ms and 178 of 199
    // frames later than 25ms. At Song.Speed=0 a row lasts a single 20ms frame, so that is the
    // view updating roughly every other row, which is exactly what it looked like.
    //
    // Anything comfortably below 15.6 rounds to one tick instead. Interval=10 measured a mean
    // of 15.8ms, p90 of 16.0 and not one frame over 25ms - steadier than an Application.Idle
    // render loop, which was the other candidate and came out at 37 late frames.
    private readonly System.Windows.Forms.Timer _smoothScrollTimer = new() { Interval = 10 };

    public event Action? Edited;

    /// <summary>A note was just typed into a pattern (Edit mode) or jammed live (Jam mode),
    /// carrying what was played and which instrument it used - MainForm auditions it. Raised as
    /// an event rather than played from here so this control stays free of any dependency on
    /// the audio stack.</summary>
    public event Action<NoteName, int>? NoteEntered;

    /// <summary>A key that sounded a note (jammed, or typed while editing) was released - see
    /// <see cref="TryBeginJamHold"/>. Fires in both modes: Edit mode's own note entry auditions
    /// through the same JamEngine voice a jammed note does, and that voice needs the same
    /// gate-off on release rather than being left to ring or to be silently stolen by the
    /// next keystroke.</summary>
    public event Action<NoteName>? NoteReleased;

    /// <summary>+/- (numpad or the main row): step the selected instrument up or down without
    /// leaving the pattern - MainForm answers by moving the Instruments panel's own selection,
    /// which is the thing <see cref="SelectedInstrumentNumber"/> actually reads.</summary>
    public event Action<int>? InstrumentStepRequested;

    /// <summary>Edit/Jam was flipped - MainForm answers with JamEngine.SilenceAll(), a "panic
    /// button" hard reset of every voice rather than a graceful release. See
    /// <see cref="ToggleEditMode"/>.</summary>
    public event Action? JamSilenceRequested;

    /// <summary>The instrument a fresh note should use when there is nothing else to go on:
    /// Jam mode always (there is no pattern cell to read one from - the note isn't being written
    /// anywhere), and Edit mode when the cursor is on a row with no note of its own yet to keep
    /// its instrument from. Wired by MainForm to the Instrument list's current selection, which
    /// is what the user is expected to have picked first. Falls back to the song's first
    /// instrument if this is left unset.</summary>
    public Func<int>? SelectedInstrumentNumber { get; set; }

    /// <summary>The live audio-sample clock, read fresh at every repaint (60fps, via
    /// _smoothScrollTimer) rather than on whatever cadence a poll timer happens to run at - see
    /// the <see cref="_playbackHistory"/> doc comment. Wired by MainForm to
    /// SongPlaybackEngine.SamplesProduced.</summary>
    public Func<long>? AudioClockSamples { get; set; }

    /// <summary>Fired whenever Edit/Jam flips - MainForm mirrors it onto the toolbar indicator
    /// next to Play, which has to reflect the current state regardless of where focus is and of
    /// which view is on screen.</summary>
    public event Action? EditModeChanged;

    private readonly bool[] _channelMuted = new bool[3];

    /// <summary>Clicking a channel's "CH1"/"CH2"/"CH3" header toggles it - a listening aid, not
    /// something that touches the song (see SongPlaybackEngine.SetVoiceMuted's own comment on why
    /// muting the SID's own mix is safe and reversible mid-note, unlike dropping register writes
    /// would be). MainForm applies the change to whichever playback engine is actually live right
    /// now, and to any new one it creates afterward.</summary>
    public event Action<int, bool>? ChannelMuteChanged;

    public bool IsChannelMuted(int channel) => _channelMuted[channel];

    /// <summary>Flips a channel's mute from outside - the DAW's arrange lanes. The state stays
    /// here rather than being copied there, so the two views always show the same thing and the
    /// engines are told about it exactly once.</summary>
    public void ToggleChannelMute(int channel)
    {
        _channelMuted[channel] = !_channelMuted[channel];
        ChannelMuteChanged?.Invoke(channel, _channelMuted[channel]);
        Invalidate();
    }

    public bool EditMode => _editMode;

    /// <summary>Flips Edit/Jam. The keystroke for it lives in MainForm.ProcessCmdKey rather than
    /// here, so it also works from the DAW view where this control is hidden; the toolbar
    /// indicator calls the same method on a click.</summary>
    public void ToggleEditMode()
    {
        _editMode = !_editMode;

        // A hard reset, not the usual graceful NoteReleased - switching modes mid-chord is a
        // "panic button" moment, not a key-up, so nothing should still be ringing out its own
        // release afterward. Clears the held-key bookkeeping directly rather than going through
        // ReleaseAllJamNotes, since that fires the graceful NoteReleased per key.
        _heldJamKeys.Clear();
        _heldJamNotes.Clear();
        JamSilenceRequested?.Invoke();

        EditModeChanged?.Invoke();
        Invalidate();
    }

    public int SongPosition => _songPosition;

    /// <summary>The edit cursor's row. Exposed so MainForm can tell whether the cursor is still
    /// where playback parked it - which is how it decides between resuming exactly and starting
    /// afresh from wherever the user has since moved to. Comparing the position beats hooking
    /// every path that can move a cursor (four arrow keys, Home/End, a mouse click, an edit that
    /// shifts rows), none of which would otherwise report it.</summary>
    public int CursorRow => _cursorRow;

    public int Octave => _octave;

    public TrackerGridControl()
    {
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = ColorBackground;
        TabStop = true;
        _smoothScrollTimer.Tick += (_, _) =>
        {
            if (_isPlaybackActive)
            {
                Invalidate();
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _smoothScrollTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    public void SetSong(Song song)
    {
        _song = song;
        _cursorRow = Math.Max(0, _cursorRow);

        // Mute is a per-listening-session convenience, not a song setting - carrying it over
        // into a different (or freshly reloaded) song would silently mute a channel nobody
        // touched, with the header the only clue why. MainForm's own playback engines never
        // outlive a SetSong either, so there's no live engine left to desync from here.
        for (int ch = 0; ch < 3; ch++)
        {
            if (_channelMuted[ch])
            {
                _channelMuted[ch] = false;
                ChannelMuteChanged?.Invoke(ch, false);
            }
        }

        Invalidate();
    }

    public void SetSongPosition(int position)
    {
        _songPosition = Math.Max(0, position);
        _cursorRow = 0;
        Invalidate();
    }

    /// <summary>Switches the grid between the static, editable per-song-position view and the
    /// live scrolling playback-follow view. The edit cursor/scroll position are untouched, so
    /// turning playback off returns exactly to where editing was left off.</summary>
    public void SetPlaybackActive(bool active)
    {
        _isPlaybackActive = active;
        if (active)
        {
            Array.Clear(_lastOrderEntrySequence);
            Array.Clear(_occurrenceBaseRow);
            Array.Clear(_nextAvailableRow);
            Array.Fill(_lastNth, -1);
            foreach (var history in _playbackHistory)
            {
                history.Clear();
            }
            _smoothScrollTimer.Start();
        }
        else
        {
            _smoothScrollTimer.Stop();
        }
        Invalidate();
    }

    /// <summary>Appends a channel's just-triggered row to its playback history. Safe to call
    /// only from the UI thread (see MainForm's poll timer, which drains SongPlaybackEngine's
    /// cross-thread queue of SongSequencer.RowTriggered events).</summary>
    public void SetChannelPlaybackAnchor(
        int channel, int patternNumber, int rowIndex, int orderEntrySequence, long sampleIndex)
    {
        if (_song is null)
        {
            return;
        }
        var pattern = _song.Patterns.FirstOrDefault(p => p.Number == patternNumber);
        if (pattern is null)
        {
            return;
        }

        // The row's own tick count: SongSequencer computes a triggered row's duration the same
        // way - one tick for the row itself, plus one more for each HoldRow immediately
        // following it in the pattern - so counting them here reproduces exactly the duration
        // the audio is using.
        int ticks = 1;
        for (int i = rowIndex + 1; i < pattern.Rows.Count && pattern.Rows[i] is HoldRow; i++)
        {
            ticks++;
        }

        // The channel's first row since Play: its rows start where its pattern is first met
        // walking its order from the song position. Started from the top of the song that is row
        // 0, but a song resumed part-way has each channel in a pattern of its own - with orders of
        // different lengths, not the one at the song position.
        bool firstSinceStart = _lastOrderEntrySequence[channel] is null;
        if (firstSinceStart)
        {
            _nextAvailableRow[channel] = FirstRowOfPattern(channel, pattern, _startEntry[channel]);
        }

        if (_lastOrderEntrySequence[channel] != orderEntrySequence)
        {
            // A genuinely new pattern occurrence began (AdvanceOrder ran) - reserve the next
            // fresh block of row numbers for it, so it never reuses an earlier occurrence's
            // rows even if it happens to be the same pattern (e.g. a channel whose order just
            // loops one pattern forever).
            _occurrenceBaseRow[channel] = _nextAvailableRow[channel];
            _nextAvailableRow[channel] += RealRowCount(pattern);
            _lastOrderEntrySequence[channel] = orderEntrySequence;

            // A resumed channel is already that many ticks into this entry: its first row is the
            // one heard last or the one after, on whichever pass of a loop that falls.
            _lastNth[channel] = firstSinceStart && _startTicks[channel] > 0 ? _startTicks[channel] - 2 : -1;
        }

        // Clamped rather than trusted: an anchor can only be stale if playback outlives the
        // song it came from, which MainForm now prevents - but a crash is too high a price for
        // relying on that, and a row number past the end of this pattern simply means "all of
        // them".
        // Which display row: the next one for this pattern row after the last row this
        // occurrence showed - a loop's later pass when it has come round again.
        var displayRows = DisplayRows(pattern);
        int realRowsBefore = -1;
        for (int n = Math.Max(0, _lastNth[channel] + 1); n < displayRows.Count && realRowsBefore < 0; n++)
        {
            if (displayRows[n].Row == rowIndex)
                realRowsBefore = n;
        }

        if (realRowsBefore < 0)
            realRowsBefore = RealRowsBefore(pattern, rowIndex);

        _lastNth[channel] = realRowsBefore;
        int centerRow = _occurrenceBaseRow[channel] + realRowsBefore;
        var history = _playbackHistory[channel];
        history.Add(new PlaybackAnchor(sampleIndex, pattern, centerRow, ticks));

        // A real-time UI has no use for an unbounded backlog - this only bites if rendering
        // somehow stalls for a while (window minimized, host machine hitches), and bounds how
        // far it has to fast-forward through once it resumes.
        const int MaxBacklog = 64;
        if (history.Count > MaxBacklog)
        {
            history.RemoveRange(0, history.Count - MaxBacklog);
        }

        Invalidate();
    }

    /// <summary>The anchor a channel's column is actually showing right now: prunes anchors the
    /// live audio clock has already fully passed (but always leaves the most recent one
    /// standing - see the <see cref="_playbackHistory"/> doc comment for why there can be more
    /// than one entry here at all), then returns whatever remains at the front. Shared by
    /// <see cref="DrawPlaybackFollowView"/> and <see cref="JumpEditCursorToLivePlaybackPosition"/>
    /// so "what's on screen" and "where stopping parks the cursor" can never disagree.</summary>
    private PlaybackAnchor? GetCurrentAnchor(int channel, long currentSamples)
    {
        var history = _playbackHistory[channel];
        while (history.Count > 1 && history[1].SampleIndex <= currentSamples)
        {
            history.RemoveAt(0);
        }
        return history.Count > 0 ? history[0] : null;
    }

    /// <summary>How many of an anchor's rows have been heard - its note and the holds after it,
    /// at one row a tick - and how far into the next, from the audio clock.</summary>
    private double RowsInto(PlaybackAnchor anchor, long currentSamples)
    {
        double tickSeconds = _song is null ? 0.12 : Math.Max(0.02, _song.AverageFramesPerRow / 50.0);
        int ticks = Math.Max(1, anchor.Ticks);
        double rowSamples = tickSeconds * ticks * SongPlaybackEngine.SampleRate;
        double fraction = rowSamples > 0 ? (currentSamples - anchor.SampleIndex) / rowSamples : 1.0;
        return Math.Clamp(fraction, 0.0, 1.0) * ticks;
    }

    /// <summary>The anchor's row being heard, counted from the row it triggered on.</summary>
    private int HeardRowsInto(PlaybackAnchor anchor, long currentSamples) =>
        Math.Min((int)RowsInto(anchor, currentSamples), Math.Max(1, anchor.Ticks) - 1);

    /// <summary>Called by MainForm as playback stops, before the engine is torn down: parks the
    /// edit cursor exactly where channel 1 was at that moment, so stop-then-play resumes from there
    /// instead of restarting the song - the same channel the playback-follow row-number gutter already
    /// uses as its shared reference. <see cref="ResolveDisplayRow"/> is the same machinery the
    /// static view already resolves every row through, so "where the cursor lands" and "what the
    /// static view then shows there" can't disagree with each other either.</summary>
    public void JumpEditCursorToLivePlaybackPosition()
    {
        if (_song is null)
        {
            return;
        }

        long currentSamples = AudioClockSamples?.Invoke() ?? 0;

        // The channel with the longest order is the song's timeline: the static view shows every
        // channel from one song position, and a shorter channel loops round to wherever it really
        // is from there. Channel 1 used to decide, and a channel 1 of one short looping pattern
        // put the cursor back at position 0 whatever the others were playing.
        int reference = Enumerable.Range(0, 3).OrderByDescending(SinglePassRowCount).First();
        if (GetCurrentAnchor(reference, currentSamples) is not { } anchor)
        {
            return; // nothing ever triggered there - nowhere sensible to jump to
        }

        // The row being heard, not the one its note started on: a held note's rows have been
        // passing all along, the same as the scrolling view draws them.
        if (ResolveDisplayRow(reference, anchor.CenterRow + HeardRowsInto(anchor, currentSamples)) is not { } resolved)
        {
            return;
        }

        SetSongPosition(resolved.OrderIndex);
        _cursorRow = resolved.Nth;
        Invalidate();
    }

    /// <summary>Which song-order entry and pattern row the edit cursor is sitting on, and the
    /// channel it is in - what "play from here" has to aim at. Null when the cursor is not over
    /// anything resolvable, e.g. past the end of a channel's chain.</summary>
    public (int Channel, int OrderIndex, int RowIndex, int TicksInto)? CursorSongLocation()
    {
        // A channel shorter than the cursor row has gone round its order by then, and its entry
        // and row name only the first time round: in Magnificent, whose channel 1 is one 384-row
        // pattern, row 390 there is that pattern's row 6, and F2 played from row 6. The longest
        // channel - the song's timeline - has not gone round yet, so the place is read there.
        int channel = _cursorChannel;
        if (_cursorRow >= SinglePassRowCount(channel))
        {
            int longest = Enumerable.Range(0, 3).OrderByDescending(SinglePassRowCount).First();
            if (_cursorRow < SinglePassRowCount(longest))
                channel = longest;
        }

        if (_song is null || ResolveDisplayRow(channel, _cursorRow) is not { } resolved)
        {
            return null;
        }

        // With loops unrolled a display row is a tick, so the row counts the ticks into its
        // entry - which pass of a loop, as well as which row.
        return (channel, resolved.OrderIndex, resolved.LocalRow, _unrollLoops ? resolved.Nth : -1);
    }

    /// <summary>Which pattern is currently on screen for each channel, at the cursor's row - null
    /// for a channel whose order doesn't reach that far (an empty or short order). For "Play
    /// Current pattern": each entry is the whole pattern, not the row under the cursor within
    /// it - looping the pattern from its own start is what that command means, regardless of
    /// which of its rows happens to be under the cursor right now.</summary>
    public Pattern?[] CurrentPatternsAtCursor()
    {
        var patterns = new Pattern?[3];
        for (int ch = 0; ch < 3; ch++)
        {
            patterns[ch] = ResolveDisplayRow(ch, _cursorRow)?.Pattern;
        }
        return patterns;
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & ~Keys.Modifiers)
        {
            case Keys.Up:
            case Keys.Down:
            case Keys.Left:
            case Keys.Right:
            case Keys.Tab:
            case Keys.Delete:
            case Keys.Insert:
            case Keys.Return:
            case Keys.Home:
            case Keys.End:
            case Keys.PageUp:
            case Keys.PageDown:
                return true;
        }
        return base.IsInputKey(keyData);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _slideEntry = null;

        // Header clicks are handled before the playback-active bail-out below, unlike every
        // other click in this grid - muting a channel is exactly the thing you want to do *while*
        // listening, so it can't wait for playback to stop the way cursor movement can.
        if (e.Y < HeaderHeight)
        {
            TryToggleChannelMute(e.X);
            return;
        }

        if (_isPlaybackActive)
        {
            return;
        }

        int row = TopRow + (e.Y - HeaderHeight) / RowHeight;
        int x = e.X - RowNumWidth;
        if (x < 0)
        {
            return;
        }

        int channel = x / ChannelColWidth;
        if (channel is < 0 or > 2)
        {
            return;
        }

        int xInChannel = x - channel * ChannelColWidth;
        if (xInChannel < OrderColWidth)
        {
            NavigateToOrderTrackClick(channel, Math.Max(0, row));
            return;
        }

        int xInField = xInChannel - OrderColWidth;
        _cursorChannel = channel;
        _cursorField = xInField switch
        {
            < MarkerColWidth => CursorField.Marker,
            < MarkerColWidth + NoteColWidth => CursorField.Note,
            _ => CursorField.Instrument,
        };
        _cursorRow = Math.Max(0, row);
        Invalidate();
    }

    /// <summary>Only the "CH1"/"CH2"/"CH3" label itself toggles mute - the Marker/NOT/INS header
    /// labels sharing the same header row don't do anything, same as they never have.</summary>
    private void TryToggleChannelMute(int mouseX)
    {
        int x = mouseX - RowNumWidth;
        if (x < 0)
        {
            return;
        }

        int channel = x / ChannelColWidth;
        if (channel is < 0 or > 2)
        {
            return;
        }

        int xInChannel = x - channel * ChannelColWidth;
        if (xInChannel >= OrderColWidth)
        {
            return;
        }

        _channelMuted[channel] = !_channelMuted[channel];
        ChannelMuteChanged?.Invoke(channel, _channelMuted[channel]);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool overMuteToggle = e.Y < HeaderHeight && IsOverOrderTrack(e.X);
        bool overOrderTrack = !_isPlaybackActive && e.Y >= HeaderHeight && IsOverOrderTrack(e.X);
        Cursor = overMuteToggle || overOrderTrack ? Cursors.Hand : Cursors.Default;
    }

    private static bool IsOverOrderTrack(int x)
    {
        int xInGrid = x - RowNumWidth;
        if (xInGrid < 0)
        {
            return false;
        }
        int channel = xInGrid / ChannelColWidth;
        return channel is >= 0 and <= 2 && xInGrid - channel * ChannelColWidth < OrderColWidth;
    }

    /// <summary>Clicking a channel's order-track cell jumps the shared _songPosition to
    /// whichever order-list entry produced the pattern shown at that row - a no-op if the row
    /// resolves to nothing (e.g. past the end of a non-looping order with no RestartPosition
    /// fallback). Also parks the cursor on that channel's Order field at the new row 0 (where
    /// the clicked entry now lives, since SetSongPosition resets the view there), ready for
    /// immediate digit entry - see EnterOrderDigit.</summary>
    private void NavigateToOrderTrackClick(int channel, int row)
    {
        if (ResolveDisplayRow(channel, row) is not { } resolved)
        {
            return;
        }
        SetSongPosition(resolved.OrderIndex);
        _cursorChannel = channel;
        _cursorField = CursorField.Order;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_isPlaybackActive)
        {
            return;
        }
        // Moves the cursor and lets the view follow, rather than sliding the view out from under
        // a stationary cursor: the cursor is meant to stay on the centre line, and scrolling it
        // off there would leave the next arrow key visibly snapping the whole page back.
        int delta = -Math.Sign(e.Delta) * 3;
        _cursorRow = Math.Max(0, _cursorRow + delta);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_song is null)
        {
            return;
        }

        if (_isPlaybackActive)
        {
            return;
        }

        // A slide being typed takes the keys first - letters and digits included, which would
        // otherwise play notes - until it is complete, abandoned, or interrupted.
        if (_slideEntry is not null && HandleSlideEntryKey(e.KeyCode))
        {
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        // Numpad 1 or 2 in the note column starts one: "1xx" up, "2xx" down. The numpad, because
        // the digit row is the upper octave of the piano.
        if (_editMode && _cursorField == CursorField.Note && !e.Control && !e.Alt
            && e.KeyCode is Keys.NumPad1 or Keys.NumPad2)
        {
            _slideEntry = e.KeyCode == Keys.NumPad1 ? "1" : "2";
            Invalidate();
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        if (e.Control && _editMode && _cursorField == CursorField.Order && e.KeyCode is Keys.Up or Keys.Down)
        {
            MoveOrderEntryAtCursor(e.KeyCode == Keys.Up ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (e.Control && _editMode && e.KeyCode == Keys.L)
        {
            ToggleLoopStartAtCursor();
            e.Handled = true;
            return;
        }
        if (e.Control && _editMode && e.KeyCode == Keys.E)
        {
            ToggleLoopEndAtCursor();
            e.Handled = true;
            return;
        }
        if (e.Shift && e.KeyCode == Keys.C)
        {
            // Read-only - copying the pattern at the cursor can't lose anything, so this stays
            // live in Jam mode too.
            CopyPatternAtCursor();
            e.Handled = true;
            return;
        }
        if (e.Shift && _editMode && e.KeyCode == Keys.V)
        {
            PastePatternAtCursor();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == PianoKeyMap.OctaveKey)
        {
            // ' alone lowers the octave, Shift+' raises it - resolved against the *current
            // Windows input layout* rather than hardcoded to the US Oem7 position, so this
            // lands on whatever key actually types ' for the active keyboard layout.
            _octave = PianoKeyMap.StepOctave(_octave, e.Shift);
            Invalidate();
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.Add or Keys.Oemplus)
        {
            InstrumentStepRequested?.Invoke(1);
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.Subtract or Keys.OemMinus)
        {
            InstrumentStepRequested?.Invoke(-1);
            e.Handled = true;
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.Up:
                MoveCursorRow(-1);
                e.Handled = true;
                return;
            case Keys.Down:
                MoveCursorRow(1);
                e.Handled = true;
                return;
            case Keys.Left:
                MoveCursorField(-1);
                e.Handled = true;
                return;
            case Keys.Right:
                MoveCursorField(1);
                e.Handled = true;
                return;
            case Keys.Tab:
                MoveCursorChannel(e.Shift ? -1 : 1);
                e.Handled = true;
                return;
            case Keys.Delete when _editMode:
                // In the Order field these act on the song-order list; everywhere else they
                // act on the pattern's own rows, as before.
                if (_cursorField == CursorField.Order)
                {
                    RemoveOrderEntryAtCursor();
                }
                else
                {
                    DeleteRowAtCursor();
                }
                e.Handled = true;
                return;
            case Keys.Insert when _editMode:
                if (_cursorField == CursorField.Order)
                {
                    InsertOrderEntryAtCursor();
                }
                else
                {
                    InsertHoldAtCursor();
                }
                e.Handled = true;
                return;
            case Keys.Back when _editMode:
                ClearRowAtCursor();
                e.Handled = true;
                return;
            case Keys.Home:
                SetCursorRow(0);
                e.Handled = true;
                return;
            // A bar of 16ths at a time - the toolbar's arrows use the same step.
            case Keys.PageUp:
                MoveCursorRowPage(-PageRows);
                e.Handled = true;
                return;
            case Keys.PageDown:
                MoveCursorRowPage(PageRows);
                e.Handled = true;
                return;
            case Keys.End when e.Shift && _editMode:
                TruncatePatternAtCursor();
                e.Handled = true;
                return;
            case Keys.End:
                SetCursorRow(LastRowForCursorChannel());
                e.Handled = true;
                return;
            case Keys.Return when _editMode:
            case Keys.CapsLock when _editMode:
                // Matches GoatTracker's own binding: RETURN (also CAPSLOCK) inserts "---"
                // (key off / gate off) at the cursor. Note: CapsLock still toggles the OS-level
                // Caps Lock indicator when pressed - that's an OS keyboard-state side effect
                // outside the app's control, not a bug here (GoatTracker has the same quirk).
                SetNoteOffAtCursor();
                e.Handled = true;
                return;
        }

        if (_cursorField == CursorField.Note)
        {
            if (PianoKeyMap.LowerPitch(e.KeyCode) is { } lowerPitch)
            {
                if (TryBeginJamHold(e.KeyCode, lowerPitch, _octave))
                {
                    EnterNote(lowerPitch, _octave);
                }
                e.Handled = true;
                return;
            }
            if (PianoKeyMap.UpperPitch(e.KeyCode) is { } upperPitch)
            {
                if (TryBeginJamHold(e.KeyCode, upperPitch, _octave + 1))
                {
                    EnterNote(upperPitch, _octave + 1);
                }
                e.Handled = true;
                return;
            }
        }
        else if (_cursorField == CursorField.Instrument && _editMode)
        {
            int? digit = HexDigitFromKey(e.KeyCode);
            if (digit is <= 9)
            {
                EnterInstrumentDigit(digit.Value);
                e.Handled = true;
            }
        }
        else if (_cursorField == CursorField.Marker && _editMode)
        {
            int? digit = HexDigitFromKey(e.KeyCode);
            if (digit is <= 9)
            {
                EnterMarkerDigit(digit.Value);
                e.Handled = true;
            }
        }
        else if (_cursorField == CursorField.Order && _editMode)
        {
            int? digit = HexDigitFromKey(e.KeyCode);
            if (digit is <= 9)
            {
                EnterOrderDigit(digit.Value);
                e.Handled = true;
            }
        }
    }

    /// <summary>The other half of <see cref="TryBeginJamHold"/>: releases whichever note this
    /// physical key was sounding, if any. Fires regardless of the current cursor field or
    /// Edit/Jam state - a key that started a jammed note has to release it even if the cursor
    /// moved off the Note field or the mode flipped while it was held, or the note is stuck
    /// sounding until the app closes.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_heldJamKeys.Remove(e.KeyCode) && _heldJamNotes.Remove(e.KeyCode, out var note))
        {
            NoteReleased?.Invoke(note);
        }
    }

    /// <summary>Losing focus (Alt+Tab, clicking another control) never delivers the matching
    /// KeyUp for whatever piano keys are still physically held - without this, jamming a chord
    /// and then clicking elsewhere would leave those voices stuck sounding forever.</summary>
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseAllJamNotes();
        if (_slideEntry is not null)
        {
            _slideEntry = null;
            Invalidate();
        }
    }

    private void ReleaseAllJamNotes()
    {
        if (_heldJamKeys.Count == 0)
        {
            return;
        }

        _heldJamKeys.Clear();
        foreach (var note in _heldJamNotes.Values)
        {
            NoteReleased?.Invoke(note);
        }
        _heldJamNotes.Clear();
    }

    /// <summary>A slide being typed: "1" or "2" for the direction, then up to the two hex digits
    /// of the rate. Null when nothing is being typed.</summary>
    private string? _slideEntry;

    /// <summary>
    /// One key of a slide being typed. Hex digits - numpad, digit row or A-F - fill in the rate,
    /// and the second one writes the row; Backspace takes one back and Escape drops the lot. Any
    /// other key abandons it and returns false, so that key then does whatever it normally does.
    /// </summary>
    private bool HandleSlideEntryKey(Keys key)
    {
        if (key == Keys.Escape)
        {
            _slideEntry = null;
            Invalidate();
            return true;
        }

        if (key == Keys.Back)
        {
            _slideEntry = _slideEntry!.Length > 1 ? _slideEntry[..^1] : null;
            Invalidate();
            return true;
        }

        if (HexDigitFromKey(key) is not { } digit)
        {
            _slideEntry = null;
            Invalidate();
            return false;
        }

        _slideEntry += digit.ToString("X");
        if (_slideEntry.Length == 3)
        {
            string text = _slideEntry;
            _slideEntry = null;

            if (SlideRow.TryParse(text, out var slide))
                SetRowAtCursor(slide);
            else
                System.Media.SystemSounds.Beep.Play();   // 1xx tops out at 7F, 2xx at 80
        }

        Invalidate();
        return true;
    }

    /// <summary>Writes a row at the cursor and steps down, the way a typed note does.</summary>
    private void SetRowAtCursor(PatternRow row)
    {
        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: true);
        if (target is null)
            return;

        var (pattern, localRow) = target.Value;
        EnsureRowCount(pattern, localRow + 1);
        pattern.Rows[localRow] = row;
        Edited?.Invoke();
        MoveCursorRow(1);
    }

    private static int? HexDigitFromKey(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => key - Keys.D0,
        >= Keys.NumPad0 and <= Keys.NumPad9 => key - Keys.NumPad0,
        Keys.A => 0xA,
        Keys.B => 0xB,
        Keys.C => 0xC,
        Keys.D => 0xD,
        Keys.E => 0xE,
        Keys.F => 0xF,
        _ => null,
    };

    /// <summary>Steps the cursor a fixed block of rows, stopping at the top of the song and at
    /// the end of the cursor channel's own chained content rather than running past either. The
    /// toolbar's arrows use this; a page is a plain row count, not a song-order position, because
    /// the three channels' order lists are independent - entry 4 on one channel is not the same
    /// moment as entry 4 on another, so a shared order index cannot mean anything here.</summary>
    public const int PageRows = 16;

    public void MoveCursorRowPage(int rows)
    {
        int last = LastRowForCursorChannel();
        _cursorRow = Math.Clamp(_cursorRow + rows, 0, Math.Max(0, last));
        Invalidate();
    }

    private void MoveCursorRow(int delta)
    {
        _cursorRow = Math.Max(0, _cursorRow + delta);
        Invalidate();
    }

    private void SetCursorRow(int row)
    {
        _cursorRow = Math.Max(0, row);
        Invalidate();
    }

    /// <summary>The last row index of one non-looping pass through the cursor's channel's
    /// order-list chain, starting at the current song position - "End" jumps to the end of
    /// *this* channel's chained content (which may span several patterns), not a grid-wide
    /// row count and not into a second lap of the loop.</summary>
    private int LastRowForCursorChannel()
    {
        int total = SinglePassRowCount(_cursorChannel);
        return total > 0 ? total - 1 : _cursorRow;
    }

    private void MoveCursorField(int delta)
    {
        // Left-to-right field order is Order, Marker, Note, Instrument; running off either end
        // moves to the adjacent channel's nearest field instead.
        if (delta > 0)
        {
            if (_cursorField == CursorField.Order)
            {
                _cursorField = CursorField.Marker;
            }
            else if (_cursorField == CursorField.Marker)
            {
                _cursorField = CursorField.Note;
            }
            else if (_cursorField == CursorField.Note)
            {
                _cursorField = CursorField.Instrument;
            }
            else
            {
                MoveCursorChannel(1);
                _cursorField = CursorField.Order;
            }
        }
        else
        {
            if (_cursorField == CursorField.Instrument)
            {
                _cursorField = CursorField.Note;
            }
            else if (_cursorField == CursorField.Note)
            {
                _cursorField = CursorField.Marker;
            }
            else if (_cursorField == CursorField.Marker)
            {
                _cursorField = CursorField.Order;
            }
            else
            {
                MoveCursorChannel(-1);
                _cursorField = CursorField.Instrument;
            }
        }
        Invalidate();
    }

    private void MoveCursorChannel(int delta)
    {
        _cursorChannel = ((_cursorChannel + delta) % 3 + 3) % 3;
        Invalidate();
    }

    /// <summary>Resolves what a channel shows at a given *global* grid row for reading/
    /// display: walks that channel's own song-order starting at _songPosition, chaining into
    /// the next pattern once the current one's rows run out, looping via RestartPosition once
    /// the order-list itself runs out - exactly matching what real playback would show at
    /// that point, so the read-side view never disagrees with SongSequencer's own semantics.
    /// OrderIndex is the 0-based index into the channel's own ChannelOrder.Tokens that this
    /// pattern came from - only meaningful at a pattern-transition row (see the order-track
    /// column in OnPaint/DrawPlaybackFollowView), where it names the exact order-list entry.
    /// null means there's genuinely nothing there (empty order, or every reachable pattern is
    /// itself empty).</summary>
    private (Pattern Pattern, int LocalRow, int OrderIndex, int Nth)? ResolveDisplayRow(int ch, int globalRow)
    {
        if (_song is null || globalRow < 0)
        {
            return null;
        }

        var order = _song.ChannelOrders[ch];
        if (order.Tokens.Count == 0)
        {
            return null;
        }

        int orderPos = _songPosition;
        int remaining = globalRow;
        int maxHops = (order.Tokens.Count + 1) * 200; // generous bound against pathological data

        for (int hop = 0; hop < maxHops; hop++)
        {
            if (orderPos >= order.Tokens.Count)
            {
                int restartIndex = order.RestartPosition - 1;
                if (restartIndex < 0 || restartIndex >= order.Tokens.Count)
                {
                    return null;
                }
                orderPos = restartIndex;
            }

            if (order.Tokens[orderPos] is not PatternRefToken(var patternNumber))
            {
                orderPos++;
                continue;
            }

            int tokenIndex = orderPos;
            var pattern = _song.Patterns.FirstOrDefault(p => p.Number == patternNumber);
            orderPos++;
            if (pattern is null)
            {
                continue;
            }

            int realCount = RealRowCount(pattern);
            if (remaining < realCount)
            {
                return (pattern, RealRowOriginalIndex(pattern, remaining), tokenIndex, remaining);
            }

            remaining -= realCount;
        }

        return null;
    }

    /// <summary>Resolves what a channel's cursor edits at a given *global* grid row: a single,
    /// non-looping pass through the order-list from _songPosition (unlike
    /// <see cref="ResolveDisplayRow"/>, this never follows RestartPosition back around) - so
    /// growing a pattern by typing past its content always extends the *last* pattern reached
    /// in that one pass, never aliases back onto a row the chain has already looped past once
    /// it starts repeating. With allowExtend, a row past all existing content resolves to the
    /// end of the last pattern in the chain (ready to be grown); without it (delete/truncate,
    /// which only make sense against something that already exists), it resolves to null.</summary>
    private (Pattern Pattern, int LocalRow)? ResolveEditTarget(int ch, int globalRow, bool allowExtend)
    {
        if (_song is null || globalRow < 0)
        {
            return null;
        }

        var order = _song.ChannelOrders[ch];
        if (_songPosition >= order.Tokens.Count)
        {
            return null;
        }

        int orderPos = _songPosition;
        int consumed = 0;
        Pattern? lastPattern = null;

        while (orderPos < order.Tokens.Count)
        {
            if (order.Tokens[orderPos] is not PatternRefToken(var patternNumber))
            {
                orderPos++;
                continue;
            }

            var pattern = _song.Patterns.FirstOrDefault(p => p.Number == patternNumber);
            orderPos++;
            if (pattern is null)
            {
                continue;
            }

            int realCount = RealRowCount(pattern);
            if (globalRow < consumed + realCount)
            {
                return (pattern, RealRowOriginalIndex(pattern, globalRow - consumed));
            }

            lastPattern = pattern;
            consumed += realCount;
        }

        if (!allowExtend || lastPattern is null)
        {
            return null;
        }

        // Extending past all existing content: new rows are always appended at the physical
        // end of Rows (even past a trailing LE, if the pattern happens to end with one) -
        // consumed already equals lastPattern's real-row total added to everything before it,
        // so globalRow - consumed is exactly how many *new* rows past that are needed.
        int extraRowsNeeded = globalRow - consumed;
        return (lastPattern, lastPattern.Rows.Count + extraRowsNeeded);
    }

    /// <summary>True on the last real row of the last pattern in a channel's order-list - the row
    /// after which RestartPosition takes over, on whichever pass through the loop it is.</summary>
    private bool IsOrderEndRow(int ch, (Pattern Pattern, int LocalRow, int OrderIndex, int Nth)? resolved)
    {
        if (_song is null || resolved is not { } r)
        {
            return false;
        }

        var tokens = _song.ChannelOrders[ch].Tokens;
        int lastPatternToken = -1;
        for (int i = tokens.Count - 1; i >= 0 && lastPatternToken < 0; i--)
        {
            if (tokens[i] is PatternRefToken)
            {
                lastPatternToken = i;
            }
        }

        int realCount = RealRowCount(r.Pattern);
        return r.OrderIndex == lastPatternToken && realCount > 0 && r.Nth == realCount - 1;
    }

    /// <summary>Total row count across one non-looping pass through a channel's order-list
    /// chain, starting at _songPosition - see <see cref="LastRowForCursorChannel"/>.</summary>
    private int SinglePassRowCount(int ch)
    {
        if (_song is null)
        {
            return 0;
        }

        var order = _song.ChannelOrders[ch];
        int orderPos = _songPosition;
        int total = 0;

        while (orderPos < order.Tokens.Count)
        {
            if (order.Tokens[orderPos] is not PatternRefToken(var patternNumber))
            {
                orderPos++;
                continue;
            }
            var pattern = _song.Patterns.FirstOrDefault(p => p.Number == patternNumber);
            orderPos++;
            if (pattern is not null)
            {
                total += RealRowCount(pattern);
            }
        }

        return total;
    }

    /// <summary>How many of a pattern's rows are real, displayable events - excludes
    /// LoopStartRow/LoopEndRow, which render as an inline prefix on an adjacent real row
    /// instead of occupying a display row of their own (see <see cref="PrefixForRow"/>).</summary>
    /// <summary>The order entry each channel was in when playback started, or -1 for the top
    /// of the song - see <see cref="SetPlaybackStartEntries"/>.</summary>
    private readonly int[] _startEntry = [-1, -1, -1];

    /// <summary>Which order entry each channel is in as a resumed song starts. A pattern can come
    /// twice in a row in an order, so its number alone does not say which of them is playing.</summary>
    public void SetPlaybackStartEntries(IReadOnlyList<int>? entries, IReadOnlyList<int>? ticksInto = null)
    {
        for (int ch = 0; ch < 3; ch++)
        {
            _startEntry[ch] = entries is not null && ch < entries.Count ? entries[ch] : -1;
            _startTicks[ch] = ticksInto is not null && ch < ticksInto.Count ? ticksInto[ch] : -1;
        }
    }

    /// <summary>How many ticks of its entry each channel had played as a resumed song starts -
    /// which, with loops unrolled, is the display row it is on, later passes included.</summary>
    private readonly int[] _startTicks = [-1, -1, -1];

    /// <summary>The display row where <paramref name="pattern"/> starts in a channel, walking its
    /// order from the song position and round its restart - from <paramref name="startEntry"/>
    /// on when that is known. 0 when it is not found.</summary>
    private int FirstRowOfPattern(int channel, Pattern pattern, int startEntry)
    {
        int row = 0;
        bool reached = startEntry < 0;
        for (int hop = 0; hop < 512; hop++)
        {
            if (ResolveDisplayRow(channel, row) is not { } resolved)
                return 0;

            reached |= resolved.OrderIndex == startEntry;
            if (reached && ReferenceEquals(resolved.Pattern, pattern))
                return row;

            int rows = RealRowCount(resolved.Pattern);
            if (rows == 0)
                return 0;

            row += rows - resolved.Nth;
        }

        return 0;
    }

    private bool _unrollLoops = true;

    /// <summary>
    /// Draw LS/LE loops as they play: a loop's section once per pass, later passes as linked
    /// copies of the first. A row is then a tick on every channel, so a looping channel lines up
    /// with the others - stopped, resumed or scrolled. Off, a loop's rows show once, as before.
    /// </summary>
    public bool UnrollLoops
    {
        get => _unrollLoops;
        set
        {
            if (_unrollLoops == value)
                return;

            _unrollLoops = value;
            _displayRows.Clear();
            Invalidate();
        }
    }

    private readonly Dictionary<Pattern, (string Shape, int Cap, IReadOnlyList<PatternLoops.UnrolledRow> Rows)> _displayRows = [];

    /// <summary>
    /// A pattern's display rows: one per tick as it plays when loops are unrolled, one per
    /// non-marker row when they are not. Kept per pattern and worked out again only when its
    /// shape - row count, markers and counts - changes, since the rows themselves do not matter.
    /// </summary>
    private IReadOnlyList<PatternLoops.UnrolledRow> DisplayRows(Pattern pattern)
    {
        string shape = ShapeOf(pattern, out bool endless);
        int cap = _unrollLoops && endless ? EndlessLoopCap() : 0;
        if (_displayRows.TryGetValue(pattern, out var cached) && cached.Shape == shape && cached.Cap == cap)
            return cached.Rows;

        var rows = _unrollLoops ? PatternLoops.Unroll(pattern, cap) : PatternLoops.Compact(pattern);
        _displayRows[pattern] = (shape, cap, rows);
        return rows;
    }

    private static string ShapeOf(Pattern pattern, out bool endless)
    {
        endless = false;
        var shape = new System.Text.StringBuilder().Append(pattern.Rows.Count);
        for (int i = 0; i < pattern.Rows.Count; i++)
        {
            switch (pattern.Rows[i])
            {
                case LoopStartRow:
                    shape.Append(',').Append(i).Append('S');
                    break;
                case LoopEndRow(var count):
                    shape.Append(',').Append(i).Append('E').Append(count ?? -1);
                    endless |= count is null;
                    break;
            }
        }

        return shape.ToString();
    }

    /// <summary>How far a loop that never ends is unrolled: to where the longest channel reaches
    /// its RST - the point where the song as a whole starts again - counting such loops once.</summary>
    private int EndlessLoopCap() => _song is null ? 0 : PatternLoops.EndlessCap(_song);

    private PatternLoops.UnrolledRow? DisplayRowAt(Pattern pattern, int nth)
    {
        var rows = DisplayRows(pattern);
        return nth >= 0 && nth < rows.Count ? rows[nth] : null;
    }

    /// <summary>The first display row at or after a pattern row - its first pass.</summary>
    private int RealRowsBefore(Pattern pattern, int localRow)
    {
        var rows = DisplayRows(pattern);
        for (int n = 0; n < rows.Count; n++)
        {
            if (rows[n].Row >= localRow)
                return n;
        }

        return rows.Count;
    }

    private int RealRowCount(Pattern pattern) => DisplayRows(pattern).Count;

    /// <summary>The Pattern.Rows index of the nth display row; past the end, where new rows would
    /// go when typing grows the pattern.</summary>
    private int RealRowOriginalIndex(Pattern pattern, int nthReal)
    {
        var rows = DisplayRows(pattern);
        return nthReal < rows.Count ? rows[nthReal].Row : pattern.Rows.Count + (nthReal - rows.Count);
    }

    /// <summary>The marker text for a display row: LS / LE on the first pass, as before, and on a
    /// later pass's first row which pass it is - "↻2/4", or "↻3" for a long or endless loop.</summary>
    private string? MarkerTextFor((Pattern Pattern, int LocalRow, int OrderIndex, int Nth) r)
    {
        if (DisplayRowAt(r.Pattern, r.Nth) is { Linked: true } d)
        {
            if (!d.PassStart)
                return null;

            return d.Passes is > 0 and < 10 ? $"↻{d.Pass}/{d.Passes}" : $"↻{d.Pass}";
        }

        return PrefixForRow(r.Pattern, r.LocalRow);
    }

    /// <summary>The cursor's row in another pass of the same loop: the same pattern row, which
    /// an edit at the cursor changes too.</summary>
    private bool IsEchoOfCursor(int channel, int row, Pattern pattern, int localRow)
    {
        if (!_unrollLoops || _isPlaybackActive || channel != _cursorChannel || row == _cursorRow)
            return false;

        return ResolveDisplayRow(_cursorChannel, _cursorRow) is { } cursor
               && ReferenceEquals(cursor.Pattern, pattern) && cursor.LocalRow == localRow
               && (DisplayRowAt(pattern, cursor.Nth)?.Passes ?? 1) != 1;
    }

    /// <summary>A later pass of an unrolled loop is the first pass again, so what changes the
    /// rows themselves - their number, the loop markers - is made there. Says so and returns true
    /// when the cursor is on a later pass.</summary>
    private bool RefuseOnLinkedPass()
    {
        if (ResolveDisplayRow(_cursorChannel, _cursorRow) is not { } r || DisplayRowAt(r.Pattern, r.Nth) is not { Linked: true } d)
            return false;

        Notice?.Invoke($"This is pass {d.Pass} of a loop - insert, delete and loop changes go in pass 1");
        return true;
    }

    /// <summary>A short message for the status bar - something refused, and why.</summary>
    public event Action<string>? Notice;

    /// <summary>The inline marker text (if any) that belongs on this real row: "LS" when the
    /// row immediately before it (in Rows) is a LoopStartRow, "LE"/"LE04" when the row
    /// immediately after it is a LoopEndRow. Combines both for the rare one-row-loop case.</summary>
    private static string? PrefixForRow(Pattern pattern, int rowIndex)
    {
        string? prefix = rowIndex > 0 && pattern.Rows[rowIndex - 1] is LoopStartRow ? "LS" : null;

        if (rowIndex + 1 < pattern.Rows.Count && pattern.Rows[rowIndex + 1] is LoopEndRow(var count))
        {
            string le = count is null ? "LE" : $"LE{count:D2}";
            prefix = prefix is null ? le : $"{prefix}/{le}";
        }

        return prefix;
    }

    /// <summary>Whether localRow is literally the pattern's first real row - used so the "P0N"
    /// pattern-start prefix shows correctly even on the very first row rendered in a repaint
    /// pass (where there's no earlier row in *this* pass to compare against and detect a
    /// transition from).</summary>
    private bool IsPatternStartRow(Pattern pattern, int localRow) =>
        RealRowCount(pattern) > 0 && RealRowOriginalIndex(pattern, 0) == localRow;

    private static void EnsureRowCount(Pattern pattern, int count)
    {
        while (pattern.Rows.Count < count)
        {
            pattern.Rows.Add(new HoldRow());
        }
    }

    /// <summary>Physical keys currently held that started a jammed note (Jam mode only) - guards
    /// against the OS's own key-repeat retriggering a note that is meant to sustain until
    /// KeyUp.</summary>
    private readonly HashSet<Keys> _heldJamKeys = new();

    /// <summary>Which note each held key in <see cref="_heldJamKeys"/> is sounding, captured at
    /// the moment it was pressed - not recomputed from the current octave on release, so
    /// changing octave (') while a chord is held cannot release the wrong note.</summary>
    private readonly Dictionary<Keys, NoteName> _heldJamNotes = new();

    /// <summary>Records which note this physical key is currently sounding, in both modes, so
    /// <see cref="OnKeyUp"/> can release it - a typed audition in Edit mode gets a gate-off on
    /// release exactly like a jammed one, not just whatever release curve the previous retrigger
    /// happened to leave running.
    ///
    /// Returns whether <see cref="EnterNote"/> should actually run this time. Edit mode: always
    /// true - every KeyDown, OS repeats included, still writes its own row and re-auditions.
    /// Jam mode: true only for the first KeyDown of a physical key, false for the OS's own
    /// repeats of an already-held one - a jammed note is meant to sustain via JamEngine's own
    /// automation until release, not retrigger every ~30ms the way held keys otherwise
    /// would.</summary>
    private bool TryBeginJamHold(Keys key, int pitchClass, int octave)
    {
        bool firstPress = _heldJamKeys.Add(key);
        _heldJamNotes[key] = PianoKeyMap.Note(pitchClass, octave);

        return _editMode || firstPress;
    }

    /// <summary>
    /// Enters a note that came from somewhere other than the computer keyboard - a MIDI keyboard.
    ///
    /// The same path a typed note takes, so edit mode still decides whether it is written or only
    /// played, the cursor still advances, and the instrument is still picked the same way. A second
    /// route into the song would be a second set of rules to keep in step with this one.
    /// </summary>
    public void EnterNote(NoteName note)
    {
        if (_song is null || _song.Instruments.Count == 0)
            return;

        // PitchClasses above runs C, C#, D ... B, which is the order NoteName's own table index
        // counts in - so the remainder is the index into it.
        EnterNote(note.TableIndex % 12, note.Octave);
    }

    private void EnterNote(int pitchClass, int octave)
    {
        if (_song!.Instruments.Count == 0)
        {
            MessageBox.Show(this, "Add an instrument before entering notes.", "No instruments",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var note = PianoKeyMap.Note(pitchClass, octave);

        if (!_editMode)
        {
            // Jam mode: play it live and stop there - no pattern cell to read an instrument
            // from, since none is being written (see SelectedInstrumentNumber).
            NoteEntered?.Invoke(note, SelectedInstrumentNumber?.Invoke() ?? _song.Instruments[0].Number);
            return;
        }

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: true);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        EnsureRowCount(pattern, localRow + 1);

        // Whatever is selected in the Instruments panel, on every note - including over a row that
        // already carries one.
        //
        // This used to leave an occupied row's instrument alone, so retyping a pitch kept whatever
        // was already there. That reads as the note being written ignoring the selection: pick
        // instrument 02, play a note onto a row holding 01, and 01 is what you get with nothing on
        // screen saying why. It is also what the DAW's piano roll has always done, so the two views
        // now write the same thing from the same selection.
        int instrumentNumber = SelectedInstrumentNumber?.Invoke() ?? _song.Instruments[0].Number;

        pattern.Rows[localRow] = new NoteRow(note, instrumentNumber);
        Edited?.Invoke();
        NoteEntered?.Invoke(note, instrumentNumber);
        MoveCursorRow(1);
    }

    private void SetNoteOffAtCursor()
    {
        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: true);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        EnsureRowCount(pattern, localRow + 1);
        pattern.Rows[localRow] = new NoteOffRow();
        Edited?.Invoke();
        MoveCursorRow(1);
    }

    private void EnterInstrumentDigit(int digit)
    {
        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: true);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        EnsureRowCount(pattern, localRow + 1);
        if (pattern.Rows[localRow] is not NoteRow noteRow)
        {
            return;
        }

        // Decimal, two digits typed in quick succession making one number: 1 then 4 is 14. A pair
        // past the last instrument there can be starts again from its second digit instead.
        bool combine = _pendingInstrumentDigit >= 0 &&
                       (DateTime.UtcNow - _lastInstrumentDigitTime) < TimeSpan.FromMilliseconds(800) &&
                       _pendingInstrumentDigit * 10 + digit <= SongValidator.MaxInstrumentNumber;
        int newValue = combine ? _pendingInstrumentDigit * 10 + digit : digit;
        _pendingInstrumentDigit = combine ? -1 : digit;
        _lastInstrumentDigitTime = DateTime.UtcNow;

        pattern.Rows[localRow] = new NoteRow(noteRow.Note, newValue);
        Edited?.Invoke();
        Invalidate();
    }

    private void DeleteRowAtCursor()
    {
        if (RefuseOnLinkedPass())
            return;

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        pattern.Rows.RemoveAt(localRow);
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Shift+End: sets the pattern at the cursor's chain position to end at the
    /// cursor's row - that row stays, everything after it (within that specific pattern) is
    /// dropped. A no-op when the cursor is already at or past that pattern's last row.</summary>
    private void TruncatePatternAtCursor()
    {
        if (RefuseOnLinkedPass())
            return;

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        if (localRow >= pattern.Rows.Count - 1)
        {
            return;
        }
        pattern.Rows.RemoveRange(localRow + 1, pattern.Rows.Count - localRow - 1);
        Edited?.Invoke();
        Invalidate();
    }

    private void InsertHoldAtCursor()
    {
        if (RefuseOnLinkedPass())
            return;

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: true);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        int insertAt = Math.Min(localRow, pattern.Rows.Count);
        pattern.Rows.Insert(insertAt, new HoldRow());
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Shift+C: copies the *entire* pattern resolved at the cursor (not just the
    /// cursor's row) into an internal clipboard, ready for Shift+V.</summary>
    private void CopyPatternAtCursor()
    {
        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, _) = target.Value;
        PatternClipboard.Copy(pattern);
    }

    /// <summary>Shift+V: overwrites the pattern at the cursor starting at the cursor's row
    /// with the last Shift+C copy, growing that pattern if needed to fit it.</summary>
    private void PastePatternAtCursor()
    {
        if (!PatternClipboard.HasContent)
        {
            return;
        }

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: true);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        PatternClipboard.PasteInto(pattern, localRow);
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Ctrl+L: toggles a loop-start anchor immediately before the cursor's row,
    /// without touching that row's own note/instrument content - LS is a separate inserted
    /// row that renders as an "LS" prefix on the cursor's row (see PrefixForRow), not an
    /// overwrite of it. Pressing it again on the same spot removes the anchor.</summary>
    private void ToggleLoopStartAtCursor()
    {
        if (RefuseOnLinkedPass())
            return;

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        if (localRow > 0 && pattern.Rows[localRow - 1] is LoopStartRow)
        {
            pattern.Rows.RemoveAt(localRow - 1);
        }
        else
        {
            pattern.Rows.Insert(localRow, new LoopStartRow());
        }
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Ctrl+E: toggles a bare (infinite) loop-end immediately after the cursor's row,
    /// without touching that row's own content - see ToggleLoopStartAtCursor. Once placed, its
    /// repeat count can be set by selecting the Marker field on this row and typing digits
    /// (EnterMarkerDigit) - LE04 = 4 total plays instead of looping forever.</summary>
    /// <summary>The repeat count of the last LE taken off a row, so putting one straight back
    /// restores it instead of silently downgrading an LE04 to an endless LE. Keyed on the row it
    /// came off: it is there for an immediate "undo that" re-toggle, not a general history -
    /// EditorSession's own undo stack covers everything else.</summary>
    private (Pattern Pattern, int Index, int? RepeatCount)? _removedLoopEnd;

    private void RememberRemovedLoopEnd(Pattern pattern, int index, int? repeatCount) =>
        _removedLoopEnd = (pattern, index, repeatCount);

    private int? RecallRemovedLoopEnd(Pattern pattern, int index) =>
        _removedLoopEnd is { } last && ReferenceEquals(last.Pattern, pattern) && last.Index == index
            ? last.RepeatCount
            : null;

    private void ToggleLoopEndAtCursor()
    {
        if (RefuseOnLinkedPass())
            return;

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        int leIndex = localRow + 1;
        if (leIndex < pattern.Rows.Count && pattern.Rows[leIndex] is LoopEndRow(var removedCount))
        {
            RememberRemovedLoopEnd(pattern, leIndex, removedCount);
            pattern.Rows.RemoveAt(leIndex);
        }
        else
        {
            // Toggling back on restores the repeat count that was just taken off this row.
            // Without it an LE04 returns as a bare LE, which loops FOREVER rather than four
            // times - a silent change to how the song plays, from a key that looks like it only
            // put back what it removed.
            pattern.Rows.Insert(leIndex, new LoopEndRow(RecallRemovedLoopEnd(pattern, leIndex)));
        }
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Backspace: erases what the cursor is on, without touching any *other* row -
    /// unlike Delete, which removes the row itself and shifts everything below it up by one.
    /// On the Marker column that means the row's loop marker(s) (a leading LS, a trailing LE,
    /// or both for a one-row loop); anywhere else the row goes back to a hold ("...") and the
    /// markers stay. This is the "erase what's here" key; Delete is the "close the gap" key.
    ///
    /// Steps the cursor down one row afterward, same as note entry (see SetNoteAtCursor/
    /// SetNoteOffAtCursor's own MoveCursorRow(1) call) - clearing a run of notes is a repeated
    /// Backspace, and without this each press would have to be followed by a separate Down just
    /// to reach the next row to clear.</summary>
    private void ClearRowAtCursor()
    {
        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        bool changed = false;

        // Marker and note are separate columns with the cursor in one or the other, so Backspace
        // erases whichever one it is actually sitting in. It used to do both at once, which meant
        // clearing a note silently took the loop out of the pattern with it.
        bool onMarker = _cursorField == CursorField.Marker;
        if (onMarker && RefuseOnLinkedPass())
            return;


        if (!onMarker && pattern.Rows[localRow] is not HoldRow)
        {
            pattern.Rows[localRow] = new HoldRow();
            changed = true;
        }

        if (onMarker)
        {
            // Trailing LE first, so removing the leading LS doesn't shift its index.
            int leIndex = localRow + 1;
            if (leIndex < pattern.Rows.Count && pattern.Rows[leIndex] is LoopEndRow(var clearedCount))
            {
                RememberRemovedLoopEnd(pattern, leIndex, clearedCount);
                pattern.Rows.RemoveAt(leIndex);
                changed = true;
            }

            if (localRow > 0 && pattern.Rows[localRow - 1] is LoopStartRow)
            {
                pattern.Rows.RemoveAt(localRow - 1);
                changed = true;
            }
        }

        if (changed)
        {
            Edited?.Invoke();
        }

        // Unconditional, same as SetNoteAtCursor/SetNoteOffAtCursor's own step - a run of
        // Backspace presses clearing several rows in a row needs every press to advance, not
        // just the ones that happened to land on a non-empty row.
        MoveCursorRow(1);
    }

    /// <summary>Digit entry in the Marker field, which edits whatever that cell is showing:
    /// on a pattern-boundary row the cell reads "PNN", so digits retype which pattern the song
    /// order plays there (exactly as the order track's own cell does); on any other row the
    /// cell reads an attached "LE"/"LE04", so digits set that loop's repeat count. Two-digit
    /// combo within 800ms either way, same convention as instrument hex entry.
    ///
    /// The boundary case has to come first: a boundary row draws "PNN" *instead of* any LS/LE
    /// prefix it also carries, so without this, digits there would edit an LE the cell isn't
    /// showing.</summary>
    private void EnterMarkerDigit(int digit)
    {
        if (RefuseOnLinkedPass())
            return;

        if (IsPatternBoundaryAtCursor())
        {
            EnterPatternNumberDigit(digit);
            return;
        }

        var target = ResolveEditTarget(_cursorChannel, _cursorRow, allowExtend: false);
        if (target is null)
        {
            return;
        }
        var (pattern, localRow) = target.Value;

        int leIndex = localRow + 1;
        if (leIndex >= pattern.Rows.Count || pattern.Rows[leIndex] is not LoopEndRow)
        {
            return;
        }

        bool combine = _pendingMarkerDigit >= 0 &&
                       (DateTime.UtcNow - _lastMarkerDigitTime) < TimeSpan.FromMilliseconds(800);
        int newValue = combine ? _pendingMarkerDigit * 10 + digit : digit;
        newValue = Math.Clamp(newValue, 1, 99);
        _pendingMarkerDigit = combine ? -1 : digit;
        _lastMarkerDigitTime = DateTime.UtcNow;

        pattern.Rows[leIndex] = new LoopEndRow(newValue);
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Types a decimal digit into the cursor channel's Order field - which of two
    /// things it does depends on which row the cursor is on, mirroring what's drawn there:
    /// on the channel's *last* row (see <see cref="LastRowForCursorChannel"/>) it retargets
    /// RestartPosition (the "LoopNN" cell); on any other order-track boundary row it retargets
    /// that entry's pattern number (the "NN→PNN" cell). A no-op everywhere else - e.g. a
    /// non-boundary row mid-pattern, where the Order field has nothing to edit.</summary>
    private void EnterOrderDigit(int digit)
    {
        if (_song is null)
        {
            return;
        }

        int total = SinglePassRowCount(_cursorChannel);
        if (total > 0 && _cursorRow == total - 1)
        {
            EnterLoopDigit(digit);
            return;
        }

        EnterPatternNumberDigit(digit);
    }

    /// <summary>Whether the cursor sits on the row a pattern actually starts on - the row that
    /// draws "PNN" in the Marker column and "NN→PNN" in the order track, and so the row where
    /// typing a pattern number means something.</summary>
    private bool IsPatternBoundaryAtCursor() =>
        ResolveDisplayRow(_cursorChannel, _cursorRow) is { } r && r.Nth == 0;

    /// <summary>Retypes which pattern the cursor row's song-order entry points at, two-digit
    /// combo within 800ms like instrument entry. Shared by the Order and Marker fields, since
    /// both draw that same pattern number on a boundary row ("NN→PNN" and "PNN") - and so share
    /// the pending-digit state too, or a two-digit number started in one column and finished in
    /// the other would silently restart from the second digit.
    ///
    /// Typing exactly one past the last pattern creates it, which is how new patterns get made
    /// (there is no separate "add pattern" command). Anything higher clamps into the existing
    /// range instead of creating: pattern numbers must stay contiguous 1..N - Startup\Tables.s
    /// asserts it - so only one can ever be added at a time, and requiring an *exact* match
    /// rather than clamping up to it is what stops a two-digit entry from creating a pattern on
    /// the first keystroke and then another on the second.</summary>
    private void EnterPatternNumberDigit(int digit)
    {
        if (_song is null || _song.Patterns.Count == 0 ||
            ResolveDisplayRow(_cursorChannel, _cursorRow) is not { } r ||
            r.Nth != 0)
        {
            return;
        }

        bool combine = _pendingOrderDigit >= 0 &&
                       (DateTime.UtcNow - _lastOrderDigitTime) < TimeSpan.FromMilliseconds(800);
        int typed = combine ? _pendingOrderDigit * 10 + digit : digit;
        _pendingOrderDigit = combine ? -1 : digit;
        _lastOrderDigitTime = DateTime.UtcNow;

        int newValue = _song.ResolveTypedPatternNumber(typed);
        _song.ChannelOrders[_cursorChannel].Tokens[r.OrderIndex] = new PatternRefToken(newValue);
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>The order-list index the Order field's structural operations act on - defined
    /// only on the rows where the track actually draws something (a pattern-boundary row, or
    /// the channel's loop row), so the field is inert exactly where it looks inert.</summary>
    private int? OrderEntryIndexAtCursor()
    {
        if (_song is null || ResolveDisplayRow(_cursorChannel, _cursorRow) is not { } r)
        {
            return null;
        }

        int total = SinglePassRowCount(_cursorChannel);
        bool isLoopRow = total > 0 && _cursorRow == total - 1;
        return isLoopRow || r.Nth == 0 ? r.OrderIndex : null;
    }

    /// <summary>The global grid row where an order entry's first row lands, over the same
    /// single non-looping pass from _songPosition that ResolveEditTarget walks. null when the
    /// entry sits before the current song position, and so isn't on screen at all.</summary>
    private int? FirstRowOfOrderEntry(int ch, int orderIndex)
    {
        if (_song is null)
        {
            return null;
        }
        var order = _song.ChannelOrders[ch];
        if (orderIndex < _songPosition || orderIndex >= order.Tokens.Count)
        {
            return null;
        }

        int row = 0;
        for (int i = _songPosition; i < orderIndex; i++)
        {
            if (order.Tokens[i] is not PatternRefToken(var number))
            {
                continue;
            }
            var pattern = _song.Patterns.FirstOrDefault(p => p.Number == number);
            if (pattern is not null)
            {
                row += RealRowCount(pattern);
            }
        }
        return row;
    }

    /// <summary>Insert in the Order field: adds another entry to the cursor channel's
    /// order-list, copying whichever pattern is already there so it shows up immediately and
    /// can then be retyped. On the loop row - the end of the list - it appends instead of
    /// inserting, which is how a song order gets extended.</summary>
    private void InsertOrderEntryAtCursor()
    {
        if (_song is null || _song.Patterns.Count == 0)
        {
            return;
        }
        var order = _song.ChannelOrders[_cursorChannel];

        if (order.Tokens.Count == 0)
        {
            // No entries means no rows, so there's nothing to copy and nowhere the cursor
            // could be resolving to - seed the channel from the lowest pattern instead.
            order.InsertEntry(0, _song.Patterns.Min(p => p.Number));
            Edited?.Invoke();
            Invalidate();
            return;
        }

        int total = SinglePassRowCount(_cursorChannel);
        bool isLoopRow = total > 0 && _cursorRow == total - 1;

        int insertAt;
        if (isLoopRow)
        {
            insertAt = order.Tokens.Count;
        }
        else if (OrderEntryIndexAtCursor() is { } index)
        {
            insertAt = index;
        }
        else
        {
            return;
        }

        int sourceIndex = Math.Clamp(insertAt, 0, order.Tokens.Count - 1);
        int patternNumber = order.Tokens[sourceIndex] is PatternRefToken(var n)
            ? n
            : _song.Patterns.Min(p => p.Number);

        order.InsertEntry(insertAt, patternNumber);
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Delete in the Order field: drops the cursor's entry from the order-list. Refuses
    /// to remove the last one - SongValidator rejects an empty order on save, and with no
    /// entries left there'd be no row on screen to put the cursor on to add one back.</summary>
    private void RemoveOrderEntryAtCursor()
    {
        if (_song is null)
        {
            return;
        }
        var order = _song.ChannelOrders[_cursorChannel];
        if (order.Tokens.Count <= 1 || OrderEntryIndexAtCursor() is not { } index)
        {
            return;
        }

        order.RemoveEntryAt(index);
        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>Ctrl+Up/Ctrl+Down in the Order field: moves the cursor's entry through the
    /// order-list. The cursor follows the entry to its new row, so holding the key keeps
    /// dragging the same entry rather than grabbing whatever slid in underneath.</summary>
    private void MoveOrderEntryAtCursor(int delta)
    {
        if (_song is null || OrderEntryIndexAtCursor() is not { } index)
        {
            return;
        }

        int moved = _song.ChannelOrders[_cursorChannel].MoveEntry(index, delta);
        if (moved < 0)
        {
            return;
        }

        if (FirstRowOfOrderEntry(_cursorChannel, moved) is { } row)
        {
            _cursorRow = row;
        }

        Edited?.Invoke();
        Invalidate();
    }

    /// <summary>The RestartPosition half of <see cref="EnterOrderDigit"/>: retypes which
    /// 1-based order-list position the cursor channel loops back to once its list runs out.</summary>
    private void EnterLoopDigit(int digit)
    {
        var order = _song!.ChannelOrders[_cursorChannel];
        if (order.Tokens.Count == 0)
        {
            return;
        }

        bool combine = _pendingLoopDigit >= 0 &&
                       (DateTime.UtcNow - _lastLoopDigitTime) < TimeSpan.FromMilliseconds(800);
        int newValue = combine ? _pendingLoopDigit * 10 + digit : digit;
        newValue = Math.Clamp(newValue, 1, order.Tokens.Count);
        _pendingLoopDigit = combine ? -1 : digit;
        _lastLoopDigitTime = DateTime.UtcNow;

        order.RestartPosition = newValue;
        Edited?.Invoke();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

        g.FillRectangle(BrushBackground, ClientRectangle);

        DrawHeader(g);

        if (_song is null)
        {
            return;
        }

        int visibleRows = VisibleRows;

        if (_isPlaybackActive)
        {
            DrawPlaybackFollowView(g, visibleRows);
            return;
        }


        // Per-channel pattern-boundary tracking, keyed on pattern *reference* rather than
        // LocalRow continuity: skipped marker rows (LS/LE no longer occupy a display row - see
        // RealRowCount/RealRowOriginalIndex) mean LocalRow can legitimately jump by more than 1
        // within the *same* pattern, so "not exactly +1" is no longer a valid boundary signal.
        // A boundary is: the pattern reference changed (new pattern), or it's the same pattern
        // but LocalRow dropped back to/below where it was (RestartPosition looped it back to
        // its own start).
        var lastPatternPerChannel = new Pattern?[3];
        var lastNthPerChannel = new int[3] { -1, -1, -1 };
        var resolvedByChannel = new (Pattern Pattern, int LocalRow, int OrderIndex, int Nth)?[3];
        var isBoundaryRow = new bool[3];
        var markerTextByChannel = new string?[3];

        // The row where each channel's order-list runs out and RestartPosition takes over -
        // the order-track cell there shows/edits "LoopNN" instead of the usual "NN→PNN" (see
        // DrawOrderTrackCell/EnterOrderDigit). A channel-level fact, computed once rather than
        // per row.
        var loopRowPerChannel = new int[3];
        for (int ch = 0; ch < 3; ch++)
        {
            int total = SinglePassRowCount(ch);
            loopRowPerChannel[ch] = total > 0 ? total - 1 : -1;
        }

        for (int i = 0; i < DrawnRows; i++)
        {
            int row = TopRow + i;
            int y = HeaderHeight + i * RowHeight;
            bool isCursorRow = row == _cursorRow;

            // Centring the cursor puts the top of the song part-way down the window, so the rows
            // above it are before row 0. They have no number and no content - skipping them here
            // keeps "-0001" out of the gutter.
            if (row < 0)
            {
                continue;
            }

            if (isCursorRow)
            {
                g.FillRectangle(BrushCurrentRow, 0, y, GridContentWidth, RowHeight);
            }

            g.DrawString(row.ToString("D4"), GridFont, BrushRowNumber, 2, y + 1);

            for (int ch = 0; ch < 3; ch++)
            {
                var resolved = ResolveDisplayRow(ch, row);
                var pattern = resolved?.Pattern;

                bool isBoundary = pattern is not null && (
                    resolved!.Value.Nth == 0 ||
                    (lastPatternPerChannel[ch] is not null &&
                     (!ReferenceEquals(pattern, lastPatternPerChannel[ch]) ||
                      resolved.Value.Nth <= lastNthPerChannel[ch])));
                isBoundaryRow[ch] = isBoundary;

                if (pattern is not null)
                {
                    lastPatternPerChannel[ch] = pattern;
                    lastNthPerChannel[ch] = resolved!.Value.Nth;
                }

                markerTextByChannel[ch] = resolved is { } r
                    ? isBoundary ? $"P{r.Pattern.Number:D2}" : MarkerTextFor(r)
                    : null;
                resolvedByChannel[ch] = resolved;
            }

            g.DrawLine(PenGridLine, RowNumWidth, y, GridContentWidth, y);
            for (int ch = 0; ch < 3; ch++)
            {
                if (isBoundaryRow[ch])
                {
                    int colX = RowNumWidth + ch * ChannelColWidth;
                    g.DrawLine(PenPatternBoundary, colX, y, colX + ChannelColWidth, y);
                }
            }

            for (int ch = 0; ch < 3; ch++)
            {
                int colX = RowNumWidth + ch * ChannelColWidth;
                DrawOrderTrackCell(g, ch, row, colX, y, isBoundaryRow[ch], row == loopRowPerChannel[ch], resolvedByChannel[ch]);
                DrawCell(g, ch, row, colX + OrderColWidth, y, resolvedByChannel[ch], markerTextByChannel[ch]);
            }
        }

        // Vertical separators between channel columns, plus a lighter one splitting each
        // channel's order-track cell from its editable marker/note/instrument fields.
        for (int ch = 0; ch <= 3; ch++)
        {
            int x = RowNumWidth + ch * ChannelColWidth;
            g.DrawLine(PenGridLine, x, HeaderHeight, x, Height);
            if (ch < 3)
            {
                g.DrawLine(PenGridLine, x + OrderColWidth, HeaderHeight, x + OrderColWidth, Height);
            }
        }
    }

    /// <summary>Draws a channel's order-track cell: "OrderIndex→PNN" on a pattern-transition
    /// row (the same rows already flagged for the "P0N" prefix in the marker column), or
    /// "LoopNN" on the channel's *last* row - where the order-list runs out and
    /// RestartPosition takes over - taking priority on the rare row that is both (a
    /// single-row last pattern). Blank otherwise - sparse by design, so a glance down the
    /// column shows only where the song order actually advances or loops. Also draws the
    /// cursor box here when the Order field is selected on this exact cell, whether or not
    /// there's anything to show - matches the Marker field, whose cursor likewise sits on
    /// rows with nothing currently attached.</summary>
    private void DrawOrderTrackCell(
        Graphics g, int channel, int row, int x, int y, bool isBoundary, bool isLoopRow,
        (Pattern Pattern, int LocalRow, int OrderIndex, int Nth)? resolved)
    {
        // "RST" rather than "Loop": it matches ChannelOrder.RestartPosition and the "restart"
        // keyword the Patterns.s grammar uses, and is a character narrower. Shown exactly as
        // RestartPosition is stored (1-based) - that is also exactly how "restart N" appears in
        // the .s file itself (PatternsFileWriter writes RestartPosition with no conversion), so
        // this is the one number in the column that matches what a hand-edited file would say.
        string? text = isLoopRow
            ? $"RST{_song!.ChannelOrders[channel].RestartPosition:D2}"
            // 1-based to match RST's own numbering in this same column, even though the index
            // behind it (OrderIndex, into ChannelOrder.Tokens) is 0-based everywhere else it's
            // used - this is its only on-screen appearance, so nothing else needs the +1.
            : isBoundary && resolved is { } r ? $"{r.OrderIndex + 1:D2}→" : null;

        if (text is not null)
        {
            g.DrawString(text, GridFont, BrushOrderTrack,
                new RectangleF(x, y + 1, OrderColWidth - 4, RowHeight), OrderTrackFormat);
        }

        if (!_isPlaybackActive && Focused && channel == _cursorChannel && row == _cursorRow &&
            _cursorField == CursorField.Order)
        {
            g.DrawRectangle(PenCursorBorder, x, y, OrderColWidth - 1, RowHeight - 1);
        }
    }

    private void DrawCell(
        Graphics g, int channel, int row, int x, int y,
        (Pattern Pattern, int LocalRow, int OrderIndex, int Nth)? resolved, string? markerText)
    {
        if (markerText is not null)
        {
            g.DrawString(markerText, GridFont, BrushLoopMarker, x + 2, y + 1);
        }

        if (resolved is null)
        {
            if (PendingSlideText(channel, row) is { } typing)
                g.DrawString(typing, GridFont, BrushSlide, x + MarkerColWidth + 2, y + 1);
            DrawCursorIfHere(g, channel, row, x, y, hasInstrument: false);
            return;
        }

        var (pattern, localRow, _, nth) = resolved.Value;
        var prow = pattern.Rows[localRow];

        // A later pass of an unrolled loop: the same rows again, tinted, with a rail down the
        // left - edits here write to the pattern itself, so every pass changes with them.
        if (DisplayRowAt(pattern, nth) is { Linked: true })
        {
            g.FillRectangle(BrushLinkedPass, x, y, MarkerColWidth + NoteColWidth + InstColWidth, RowHeight);
            g.FillRectangle(BrushLinkedRail, x, y, 2, RowHeight);
        }
        else if (IsEchoOfCursor(channel, row, pattern, localRow))
        {
            g.DrawRectangle(PenLinkedEcho, x + MarkerColWidth + 1, y + 1, NoteColWidth - 3, RowHeight - 3);
        }
        string noteText;
        string? instText = null;
        Brush noteBrush;

        switch (prow)
        {
            case NoteRow(var note, var inst):
                noteText = note.ToString();
                // Decimal, as the instrument list beside the grid numbers them: 14, not 0E.
                instText = inst.ToString("D2");
                noteBrush = BrushNote;
                break;
            case HoldRow:
                noteText = "...";
                noteBrush = BrushHold;
                break;
            case NoteOffRow:
                noteText = "---";
                noteBrush = BrushNoteOff;
                break;
            case SlideRow slide:
                noteText = slide.Text;
                noteBrush = BrushSlide;
                break;
            default:
                noteText = "?";
                noteBrush = BrushHold;
                break;
        }

        // The slide being typed shows in the cursor's cell as it builds up: "1", "12_", ...
        if (PendingSlideText(channel, row) is { } pending)
        {
            noteText = pending;
            noteBrush = BrushSlide;
            instText = null;
        }

        g.DrawString(noteText, GridFont, noteBrush, x + MarkerColWidth + 2, y + 1);

        if (instText is not null)
        {
            g.DrawString(instText, GridFont, BrushInstrument, x + MarkerColWidth + NoteColWidth + 2, y + 1);
        }

        DrawCursorIfHere(g, channel, row, x, y, hasInstrument: instText is not null);
    }

    /// <summary>The slide being typed, padded out with underscores, for the cursor's cell only.</summary>
    private string? PendingSlideText(int channel, int row) =>
        _slideEntry is not null && channel == _cursorChannel && row == _cursorRow
            ? _slideEntry.PadRight(3, '_')
            : null;

    private void DrawCursorIfHere(Graphics g, int channel, int row, int x, int y, bool hasInstrument)
    {
        // CursorField.Order is drawn by DrawOrderTrackCell, which owns that column - without
        // this the switch's catch-all below would draw a second box on the Instrument field
        // at the same time, so the cursor appeared to be in two places at once.
        if (_isPlaybackActive || !Focused || channel != _cursorChannel || row != _cursorRow ||
            _cursorField == CursorField.Order)
        {
            return;
        }

        var rect = _cursorField switch
        {
            CursorField.Marker => new Rectangle(x, y, MarkerColWidth - 1, RowHeight - 1),
            CursorField.Note => new Rectangle(x + MarkerColWidth, y, NoteColWidth - 1, RowHeight - 1),
            _ => new Rectangle(x + MarkerColWidth + NoteColWidth, y, InstColWidth - 1, RowHeight - 1),
        };
        g.DrawRectangle(PenCursorBorder, rect);
    }

    /// <summary>Renders the live playback-follow view: each channel's column shows the exact
    /// same content as the static edit view (holds as "...", LS/LE/P0N prefixes and all - see
    /// ResolveDisplayRow/DrawCell) but scrolled independently, centered so that channel's live
    /// center row (from whichever PlaybackAnchor the live sample clock currently selects - see
    /// the <see cref="_playbackHistory"/> doc comment) always lands on the same horizontal "now"
    /// line shared by all three columns.
    ///
    /// The "now" line itself (BrushCurrentRow) is drawn at a FIXED pixel position - it's the
    /// playhead. Row *content* is what moves: each channel's rows are drawn shifted upward by
    /// a sub-row pixel amount that grows from 0 to RowHeight as the audio clock advances through
    /// that row's own tick count (one authored row line is one tick - see Code\musicplayer.s /
    /// SongSequencer - but a row can set its own duration to more than one, and different rows
    /// and different channels routinely disagree, e.g. Lightforce's ported patterns mix
    /// duration-3 and duration-7 rows on the same channel). Continuous per-channel motion
    /// instead of one RowHeight-sized snap per tick.
    ///
    /// The row-number gutter (normally the static view's leftmost column) has no single value
    /// that means the same thing for all three channels here, since they scroll at independent
    /// rates - so it's drawn once, as a shared ruler rather than a per-channel value; leaving it
    /// blank read as "the numbers disappeared" rather than as a deliberate simplification. The
    /// ruler is the channel with the longest order, the song's timeline - the one stopping parks
    /// the cursor by (see <see cref="JumpEditCursorToLivePlaybackPosition"/>). It used to be
    /// channel 1, and in Magnificent, whose channel 1 is one 384-row pattern going round, F2 at
    /// row 390 numbered the rows from 006.</summary>
    private void DrawPlaybackFollowView(Graphics g, int visibleRows)
    {
        // Row 0's sub-pixel offset can still reach up to one RowHeight above HeaderHeight even
        // bounded to [0, RowHeight) - the header's own text isn't confined to its very top pixel
        // (DrawChannelHeadings sits at HeaderHeight-16), so that peek was reaching the CH/PAT
        // heading row underneath "Playing", not just the boundary line. Clipping the whole
        // scrolling region to below the header is the actual fix - it can't be reintroduced by
        // some future change to the offset math the way bounding the offset alone already was.
        var priorClipState = g.Save();
        g.SetClip(new Rectangle(0, HeaderHeight, Width, Height - HeaderHeight));
        try
        {
            DrawPlaybackFollowRows(g, visibleRows);
        }
        finally
        {
            g.Restore(priorClipState);
        }
    }

    private void DrawPlaybackFollowRows(Graphics g, int visibleRows)
    {

        int centerIndex = visibleRows / 2;
        int centerY = HeaderHeight + centerIndex * RowHeight;
        long currentSamples = AudioClockSamples?.Invoke() ?? 0;

        // The ruler's channel, or the first that has played anything when that one has not yet.
        int longest = Enumerable.Range(0, 3).OrderByDescending(SinglePassRowCount).First();
        int ruler = new[] { longest, 0, 1, 2 }.FirstOrDefault(c => GetCurrentAnchor(c, currentSamples) is not null, longest);

        for (int ch = 0; ch < 3; ch++)
        {
            int colX = RowNumWidth + ch * ChannelColWidth;

            if (GetCurrentAnchor(ch, currentSamples) is not { } anchor)
            {
                continue; // nothing triggered yet for this channel since Play was pressed
            }

            g.FillRectangle(BrushCurrentRow, colX, centerY, ChannelColWidth, RowHeight);

            // A multi-tick row (a NoteRow followed by HoldRows - "...") occupies that many real
            // rows in the pattern, and CenterRow jumps by the same count at the *next* trigger
            // (see SetChannelPlaybackAnchor/RealRowCount - holds are real rows too). So the
            // glide has to cover anchor.Ticks worth of RowHeight over the row's full duration,
            // not one RowHeight regardless of tick count: capping it at a single RowHeight left
            // the holds essentially motionless for the whole duration and then jumped the view
            // forward by the remaining rows in a single frame the instant the next real note
            // triggered - which is what read as the holds being skipped over entirely.
            //
            // That total offset is decomposed into whole rows (absorbed into channelTopRow, so
            // it changes *which* rows get resolved) plus a sub-row remainder bounded to
            // [0, RowHeight) (the only part actually applied as a pixel shift). Applying the
            // full, unbounded total directly as a pixel offset - the first cut of this fix - let
            // it grow past one RowHeight on any row longer than a single tick, which scrolled
            // this column's content up past the header's own row entirely and drew pattern data
            // on top of "Playing" and the CH/PAT column headings.
            double totalOffsetRows = RowsInto(anchor, currentSamples);
            int wholeRows = (int)totalOffsetRows;
            int subPixelOffset = (int)Math.Round((totalOffsetRows - wholeRows) * RowHeight);

            int channelTopRow = anchor.CenterRow + wholeRows - centerIndex;
            Pattern? lastPattern = null;
            int lastNth = -1;

            // One row past the bottom: the glide lifts every row by up to a row, and the next one
            // has to be there to fill the space it leaves.
            for (int i = 0; i <= DrawnRows; i++)
            {
                int row = channelTopRow + i;
                int y = HeaderHeight + i * RowHeight - subPixelOffset;
                var resolved = ResolveDisplayRow(ch, row);
                var pattern = resolved?.Pattern;

                bool isBoundary = pattern is not null && (
                    resolved!.Value.Nth == 0 ||
                    (lastPattern is not null &&
                     (!ReferenceEquals(pattern, lastPattern) || resolved.Value.Nth <= lastNth)));
                if (pattern is not null)
                {
                    lastPattern = pattern;
                    lastNth = resolved!.Value.Nth;
                }

                g.DrawLine(isBoundary ? PenPatternBoundary : PenGridLine, colX, y, colX + ChannelColWidth, y);

                if (ch == ruler)
                {
                    g.DrawString(row.ToString("D4"), GridFont, BrushRowNumber, 2, y + 1);
                }

                string? markerText = resolved is { } r
                    ? isBoundary ? $"P{r.Pattern.Number:D2}" : MarkerTextFor(r)
                    : null;
                // The follow view scrolls on through the loop, so the RST row comes round once
                // per pass - it is whichever row ends the order-list, not one fixed row number.
                DrawOrderTrackCell(g, ch, row, colX, y, isBoundary, IsOrderEndRow(ch, resolved), resolved);
                DrawCell(g, ch, row, colX + OrderColWidth, y, resolved, markerText);
            }
        }

        for (int ch = 0; ch <= 3; ch++)
        {
            int x = RowNumWidth + ch * ChannelColWidth;
            g.DrawLine(PenGridLine, x, HeaderHeight, x, Height);
            if (ch < 3)
            {
                g.DrawLine(PenGridLine, x + OrderColWidth, HeaderHeight, x + OrderColWidth, Height);
            }
        }
    }

    private void DrawHeader(Graphics g)
    {
        g.FillRectangle(BrushHeaderBackground, 0, 0, Width, HeaderHeight);


        if (_isPlaybackActive)
        {
            g.DrawString("▶ Playing", HeaderFont, BrushHeaderText, 2, 2);
            for (int ch = 0; ch < 3; ch++)
            {
                var history = _playbackHistory[ch];
                string patternLabel = history.Count > 0 ? $"PAT{history[^1].Pattern.Number:D2}" : "—";
                DrawChannelHeadings(g, BrushHeaderText, ch, patternLabel);
            }
            return;
        }

        g.DrawString($"Song Pos {_songPosition:D2}   Oct {_octave}", HeaderFont, BrushHeaderText, 2, 2);

        for (int ch = 0; ch < 3; ch++)
        {
            // Resolved at the cursor's row (not fixed to _songPosition) so the label tracks
            // wherever the cursor's chain has actually scrolled to, e.g. flips from PAT01 to
            // PAT02 once the cursor moves past PAT01's last row.
            string patternLabel = _song is not null && ResolveDisplayRow(ch, _cursorRow) is { Pattern.Number: var n }
                ? $"PAT{n:D2}"
                : "—";
            DrawChannelHeadings(g, BrushHeaderText, ch, patternLabel);
        }
    }

    /// <summary>Draws one channel's four column headings, each sitting over the field it names:
    /// the channel number over the order track, the pattern the cursor (or playhead) is
    /// currently inside over the Marker column that draws "PNN", then NOT and INS over the note
    /// and instrument cells.
    ///
    /// Drawn for every channel whether or not there's data beneath - on a song with no patterns
    /// yet these headings are the only thing showing that the note and instrument columns exist
    /// at all, and where a pattern number is supposed to go.
    ///
    /// The "CH1"/"CH2"/"CH3" label doubles as a mute toggle (see TryToggleChannelMute) and reads
    /// "Mute" in BrushNoteOff's own red - the same colour a note-off cell already uses for "this
    /// channel has gone quiet here" - rather than a new colour invented just for this.</summary>
    private void DrawChannelHeadings(Graphics g, Brush brush, int channel, string patternLabel)
    {
        int x = RowNumWidth + channel * ChannelColWidth;
        int y = HeaderHeight - 16;

        bool muted = _channelMuted[channel];
        g.DrawString(muted ? "Mute" : $"CH{channel + 1}", HeaderFont, muted ? BrushNoteOff : brush, x + 2, y);
        g.DrawString(patternLabel, HeaderFont, brush, x + OrderColWidth + 2, y);
        g.DrawString("NOT", HeaderFont, brush, x + OrderColWidth + MarkerColWidth + 2, y);
        g.DrawString("INS", HeaderFont, brush, x + OrderColWidth + MarkerColWidth + NoteColWidth + 2, y);
    }
}
