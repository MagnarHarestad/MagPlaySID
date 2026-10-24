using MagSid.Core.Model;
using MagSid.Core.Validation;

namespace MagSid.Playback;

/// <summary>
/// Direct port of MAGSIDv7_LowMemory\Code\musicplayer.s's music.play / Voice_Automation /
/// Voice_Tick / Voice_AdvanceOrder / Voice_PlayCurrent / Voice_NoteOff, adapted to run against
/// MagSid.Core's already-structured <see cref="Song"/> model instead of assembled byte tables.
/// The command bytes the assembly dispatches on don't exist here - a pattern's row-list simply
/// ends, and LoopStartRow/LoopEndRow are already distinct types - but every observable
/// *behavior* is reproduced exactly, frame for frame, so a Song plays identically here and in
/// the verified assembly player.
///
/// The frame structure matters as much as the individual writes, and mirrors music.play: every
/// channel's automation runs first, then the filter steps once per frame (the SID has one
/// filter, not one per voice), and only then does the speed countdown decide whether this frame
/// is also a tick. Both automation and the filter running <em>before</em> the row fetch is
/// load-bearing: a note trigger arms the wavetable and claims the filter, and because both have
/// already gone past for this frame, the wavetable's first real step and the filter's first
/// Setup land on the <em>next</em> frame together - the wavetable half of that is precisely the
/// second half of the old two-frame hard restart, now with no flag to remember it by.
/// </summary>
public sealed class SongSequencer
{
    private readonly Song _song;
    private readonly Action<int, int> _write;
    private Dictionary<int, Pattern> _patternsByNumber;
    private Dictionary<int, Instrument> _instrumentsByNumber;

    /// <summary>Instrument index 0 in the player's tables - what a release before a voice's
    /// first note runs on.</summary>
    private readonly Instrument? _firstInstrument;
    private readonly ChannelState[] _channels = [new(), new(), new()];

    private int _speedCounter;

    /// <summary>Song_Shuffle: with funk speed, flipped by the two speeds' difference every tick -
    /// the tick lengths it gives are what ONbeat.PlaySong's lda/eor/sta/eor/sta gives.</summary>
    private int _shuffle;

    /// <summary>Frames played since the top of the song - carried through a resume, so it counts
    /// the music rather than this sequencer's own lifetime.</summary>
    private int _frame;

    // The filter is a single global resource, exactly as in the player: whichever instrument
    // last triggered with a FLT program owns it, across all three channels, until another one
    // takes it over. Shared code with JamEngine - see FilterEngine's own doc comment.
    private readonly FilterEngine _filter = new();

    /// <summary>Fires exactly when a real event (NoteRow/NoteOffRow) is triggered on a
    /// channel - not for holds, not for LS/LE markers. Carries the pattern number and the
    /// row's original Pattern.Rows index (not the post-lookahead RowIndex, which has already
    /// moved past any consumed holds) so a UI can locate exactly which authored row this was,
    /// e.g. to center a live-follow view on it. Also carries OrderEntrySequence - an
    /// ever-incrementing counter bumped only by AdvanceOrder (never by an in-pattern LS/LE
    /// loop-back) - so a UI can tell "this channel genuinely moved on to a new pattern
    /// occurrence" apart from "this is the same occurrence looping back via LE": both can
    /// otherwise look identical (same pattern, an earlier row) from the trigger data alone.
    /// Raised synchronously from within <see cref="Frame"/>, so on whatever thread the caller
    /// drives playback from (typically an audio callback thread, not the UI thread).</summary>
    public event Action<int, int, int, int, PatternRow>? RowTriggered;

    /// <param name="resumeFrom">A position captured from an earlier sequencer (see
    /// <see cref="Capture"/>), to carry on from instead of starting the song at its beginning.
    ///
    /// A whole captured instant, not a per-channel row number, because that is the only thing
    /// that reliably keeps the parts together. The three channels have independent song orders
    /// whose patterns are different lengths, so they are never level with each other once a song
    /// has been running - and a channel stopped inside an LS/LE section also carries a repeat
    /// count and a loop-back anchor that nothing outside the sequencer can reconstruct. Anything
    /// less than the full instant puts some channel back in the wrong place.
    ///
    /// A position that no longer fits the song - a pattern that has since been deleted, a row
    /// past the end of a shortened one - is ignored for that channel, which simply starts at its
    /// own first order entry.</param>
    public SongSequencer(
        Song song,
        Action<int, int> writeRegister,
        SequencerPosition? resumeFrom = null)
    {
        _song = song;
        _write = writeRegister;
        _patternsByNumber = ByNumber(song.Patterns, p => p.Number);
        _instrumentsByNumber = ByNumber(song.Instruments, i => i.Number);
        _firstInstrument = song.Instruments.MinBy(i => i.Number);

        bool slides = MagSid.Core.SongSizeCalculator.UsesSlide(song);
        foreach (var channel in _channels)
            channel.Voice.SongHasSlides = slides;

        // Matches music.init's "lda #$0f / sta $d418" - max volume, no filter routing.
        // Without this the filter's Vol register stays 0 and every voice is silent.
        _write(0x18, 0x0f);

        // Trigger the song on the first frame, matching music.init priming each channel's
        // order before the first tick (mirrors ONbeat.PlaySong's "trigger on first frame"
        // idiom via Song_Speed starting the countdown already expired).
        _speedCounter = resumeFrom?.SpeedCounter ?? -1;
        _shuffle = resumeFrom?.Shuffle ?? 0;
        _frame = resumeFrom?.Frame ?? 0;

        for (int ch = 0; ch < 3; ch++)
        {
            if (resumeFrom is not null && TryRestore(ch, resumeFrom.Channels[ch]))
            {
                _announceRestored = true;
                continue;
            }

            AdvanceOrder(ch);
        }
    }

    /// <summary>Set by a resume: the first frame tells <see cref="RowTriggered"/> which row each
    /// restored channel is already sounding.</summary>
    private bool _announceRestored;

    /// <summary>Which channels raised <see cref="RowTriggered"/> in the frame being played.</summary>
    private readonly bool[] _triggeredThisFrame = new bool[3];

    /// <summary>
    /// A channel restored part-way through a long note triggers nothing until its next row, and
    /// whatever follows playback from RowTriggered - the retro view's scrolling columns - had
    /// nothing to show for it until then. So a resumed song names, once, the row each channel is
    /// on: the hold being heard, raised as a trigger of that row. The voice is not touched.
    /// </summary>
    private void AnnounceRestoredRows()
    {
        for (int ch = 0; ch < 3; ch++)
        {
            var state = _channels[ch];
            if (_triggeredThisFrame[ch] || PatternFor(state.CurrentPatternNumber) is not { } pattern)
            {
                continue;
            }

            // At the end of the frame: the last tick played the row before the remaining holds.
            int row = state.RowIndex - state.Duration - 1;
            if (row >= 0 && row < pattern.Rows.Count && pattern.Rows[row] is not (LoopStartRow or LoopEndRow))
            {
                RowTriggered?.Invoke(ch, state.CurrentPatternNumber, row, state.OrderEntrySequence, pattern.Rows[row]);
            }
        }
    }

    /// <summary>
    /// The song's state at the moment <paramref name="channel"/> is about to play the given
    /// order entry and row - what "start playing from this row" needs.
    ///
    /// Found by running the song silently from its beginning rather than calculated. The three
    /// channels' orders are independent and need not span the same amount of time: in Lightforce
    /// channel 3's order is 1024 ticks against 2048 on channels 1 and 2, so it runs through its
    /// whole order twice per pass of theirs and its entry 4 is a different moment in the music
    /// than their entry 4. There is no arithmetic on song positions that lines the channels up;
    /// the only thing that knows where they all are at a given moment is the sequencer, which
    /// also already handles per-row durations and LS/LE repeat counts.
    ///
    /// Returns null if the target is never reached - a row no order entry leads to.
    /// </summary>
    public static SequencerPosition? FindPosition(Song song, int channel, int orderIndex, int rowIndex) =>
        FindTrigger(song, channel, orderIndex, rowIndex, out _);

    /// <summary>
    /// The song's state at exactly that row - not at the note sounding there, as
    /// <see cref="FindPosition"/> gives, but that many rows further into it: what a play-from
    /// marker means. A note started before the row is under way there and does not sound until
    /// the next one, the same as starting a DAW from a locator.
    /// </summary>
    public static SequencerPosition? FindExactPosition(Song song, int channel, int orderIndex, int rowIndex)
    {
        var trigger = FindTrigger(song, channel, orderIndex, rowIndex, out int triggeredRow);
        if (trigger is null || triggeredRow >= rowIndex)
            return trigger;

        // Each tick from the trigger plays the next row, so the row wanted is that many ticks on;
        // the boundary just before that tick is where it starts.
        int ticksOn = rowIndex - triggeredRow;
        var probe = new SongSequencer(song, (_, _) => { }, trigger);
        for (int ticks = 0, frame = 0; frame < 1_000_000; frame++)
        {
            var before = probe.Capture();
            if (probe.Frame())
            {
                if (ticks == ticksOn)
                    return before;
                ticks++;
            }
        }

        return trigger;
    }

    /// <summary>
    /// The song's state <paramref name="ticksInto"/> ticks after the channel starts the given
    /// order entry - a row of a loop's later pass is a moment of its own, which a pattern row
    /// cannot name. What the retro view's play-from-cursor needs once loops are drawn unrolled.
    /// </summary>
    public static SequencerPosition? FindPositionInEntry(Song song, int channel, int orderIndex, int ticksInto)
    {
        if (orderIndex < 0 || orderIndex >= song.ChannelOrders[channel].Tokens.Count
            || song.ChannelOrders[channel].Tokens[orderIndex] is not PatternRefToken token
            || song.Patterns.FirstOrDefault(p => p.Number == token.PatternNumber) is not { } pattern
            || PatternLoops.Compact(pattern) is not [var first, ..])
        {
            return null;
        }

        var start = FindEntryStart(song, channel, orderIndex);
        if (start is null || ticksInto <= 0)
            return start;

        var probe = new SongSequencer(song, (_, _) => { }, start);
        for (int ticks = 0, frame = 0; frame < 1_000_000; frame++)
        {
            var before = probe.Capture();
            if (probe.Frame())
            {
                if (ticks == ticksInto)
                    return before;
                ticks++;
            }
        }

        return start;
    }

    /// <summary>The song's state after <paramref name="frames"/> frames from the top - where a
    /// pre-roll a bar before a punch-in starts.</summary>
    public static SequencerPosition PositionAtFrame(Song song, int frames)
    {
        var probe = new SongSequencer(song, (_, _) => { });
        for (int frame = 0; frame < frames; frame++)
            probe.Frame();
        return probe.Capture();
    }

    /// <summary>
    /// How many ticks each channel has played of the order entry it is in at
    /// <paramref name="position"/> - found by playing the song silently up to it. A loop plays
    /// its rows more than once, so a pattern row alone does not say which pass a resumed channel
    /// is on; the ticks do.
    /// </summary>
    public static int[] TicksIntoEntries(Song song, SequencerPosition position)
    {
        var ticks = new int[3];
        var sequence = new int?[3];
        var started = new bool[3];
        var probe = new SongSequencer(song, (_, _) => { });
        probe.RowTriggered += (ch, _, _, occurrence, _) =>
        {
            if (sequence[ch] != occurrence)
            {
                sequence[ch] = occurrence;
                started[ch] = true;
            }
        };

        for (int frame = 0; frame < position.Frame; frame++)
        {
            Array.Clear(started);
            if (!probe.Frame())
                continue;

            for (int ch = 0; ch < 3; ch++)
                ticks[ch] = started[ch] ? 1 : ticks[ch] + 1;
        }

        return ticks;
    }

    /// <summary>
    /// The frame boundary before the channel first enters <paramref name="orderIndex"/>. Not the
    /// pattern's first row by <see cref="FindTrigger"/>: that keeps the latest trigger of a row,
    /// and a pattern opening with a loop plays its first row once a pass - Magnificent's PAT03
    /// starts with a two-row LE32, and F2 into it began 31 passes, 62 ticks, late.
    /// </summary>
    private static SequencerPosition? FindEntryStart(Song song, int channel, int orderIndex)
    {
        const int MaxFrames = 2_000_000;
        var probe = new SongSequencer(song, (_, _) => { });
        bool triggered = false;
        probe.RowTriggered += (ch, _, _, _, _) => triggered |= ch == channel;

        for (int frame = 0; frame < MaxFrames; frame++)
        {
            var before = probe.Capture();
            triggered = false;
            probe.Frame();
            if (triggered && probe.Capture().Channels[channel].PlayingEntry == orderIndex)
                return before;
        }

        return null;
    }

    private static SequencerPosition? FindTrigger(Song song, int channel, int orderIndex, int rowIndex, out int bestRow)
    {
        bestRow = -1;
        // Enough for any song this format can hold - a 255-entry order of 255-row patterns at
        // the slowest speed - while still refusing to spin forever on an unreachable target.
        const int MaxFrames = 2_000_000;

        var probe = new SongSequencer(song, (_, _) => { });

        // What gets returned is the frame boundary *before* the wanted row is triggered, not the
        // state at the row itself: AdvanceOrder and the row fetch both happen inside one Frame,
        // so "entry N, row 0, about to play" is never observable from outside. Restoring the
        // preceding boundary and letting the song run reproduces that row as the next thing that
        // happens, which is what a resume needs.
        int triggeredRow = -1;
        probe.RowTriggered += (ch, _, rowIdx, _, _) =>
        {
            if (ch == channel)
            {
                triggeredRow = rowIdx;
            }
        };

        SequencerPosition? best = null;
        for (int frame = 0; frame < MaxFrames; frame++)
        {
            var before = probe.Capture();
            triggeredRow = -1;
            probe.Frame();

            // OrderPos points past the entry being played, so after the frame the entry that
            // just triggered is OrderPos - 1. Read through Capture rather than the field, so
            // this stays honest about only using what the type already publishes.
            int entry = probe.Capture().Channels[channel].PlayingEntry;

            if (triggeredRow >= 0 && entry == orderIndex)
            {
                // Keep the latest trigger at or before the target rather than demanding the row
                // exactly: a note swallows the holds that follow it, so a hold row never
                // triggers at all. Landing on the note still sounding there is what pointing at
                // a hold row means.
                if (triggeredRow <= rowIndex)
                {
                    best = before;
                    bestRow = triggeredRow;
                }
                else if (best is not null)
                {
                    return best;
                }
            }
            else if (best is not null && entry != orderIndex)
            {
                return best; // the channel has moved on past the entry we were collecting from
            }
        }

        return best;
    }

    /// <summary>Puts one channel back exactly as it was, without going through AdvanceOrder -
    /// which would reset the row and the loop bookkeeping that is the whole point of restoring.
    /// Returns false if the captured position does not fit this song any more, leaving the
    /// caller to start that channel normally.</summary>
    private bool TryRestore(int ch, ChannelPlaybackState captured)
    {
        if (PatternFor(captured.CurrentPatternNumber) is not { } pattern ||
            pattern.Rows.Count == 0)
        {
            return false;
        }

        var state = _channels[ch];
        state.OrderPos = captured.OrderPos;
        state.CurrentPatternNumber = captured.CurrentPatternNumber;
        // Up to Rows.Count, not Rows.Count - 1: a channel that has just played its pattern's last
        // row sits one past it, waiting to move to its next entry. Pulled back onto that row, it
        // played it a second time and came out a row behind the other channels.
        state.RowIndex = Math.Clamp(captured.RowIndex, 0, pattern.Rows.Count);
        state.LoopStartRowIndex = Math.Clamp(captured.LoopStartRowIndex, 0, pattern.Rows.Count - 1);
        state.LoopRepeatCounter = captured.LoopRepeatCounter;
        state.Duration = captured.Duration;
        state.TicksIntoEntry = captured.TicksIntoEntry;
        return true;
    }

    /// <summary>Advances one 50Hz frame - mirrors music.play's "dec Song_Speed / bmi
    /// ONbeat.PlaySong" tick-speed countdown. Returns true if this frame was a tick (all
    /// three channels' m_voice_tick ran).</summary>
    /// <summary>Which program steps each instrument has actually played since playback started.
    /// Indexed by instrument number, so entry 0 is unused; see <see cref="ProgramCoverage"/> for
    /// why this is gathered here, on the audio thread, rather than sampled by the UI.</summary>
    public IReadOnlyList<ProgramCoverage> AllCoverage => _coverage;

    /// <summary>How many order entries this channel has started since playback began - it counts
    /// up by one on every AdvanceOrder and never resets, so it measures progress through the
    /// order list in a way OrderPos cannot (that one only moves when a pattern ends, and wraps).</summary>
    public int OrderEntriesStarted(int channel) => _channels[channel].OrderEntrySequence;

    public ProgramCoverage CoverageFor(int instrumentNumber) =>
        _coverage[Math.Clamp(instrumentNumber, 0, _coverage.Length - 1)];

    private readonly ProgramCoverage[] _coverage =
        Enumerable.Range(0, SongValidator.MaxInstrumentNumber + 1)
                  .Select(_ => new ProgramCoverage())
                  .ToArray();

    /// <summary>What voice `channel` is playing right now: its instrument, and the step each of
    /// its two programs last actually played. -1 for a program that has not played a step.</summary>
    public (int Instrument, int WavetableStep, int ArpeggioStep) VoicePlayhead(int channel)
    {
        var voice = _channels[channel].Voice;
        return voice.IsActive && !voice.IsReleased
            ? (voice.InstrumentNumber, voice.LastWavetableStep, voice.LastArpeggioStep)
            : (-1, -1, -1);
    }

    /// <summary>The instrument a voice last triggered, or -1. Unlike <see cref="VoicePlayhead"/>
    /// this outlasts the programs: a released note is still that instrument's envelope.</summary>
    public int VoiceInstrument(int channel) => _channels[channel].Voice.InstrumentNumber;

    /// <summary>The shared filter's owner, the frames since it claimed the filter, and the cutoff
    /// last written - see FilterEngine.</summary>
    public (int Instrument, int Frames, int Cutoff) FilterState() =>
        (_filter.OwnerInstrumentNumber, _filter.FramesSinceClaim, _filter.Cutoff);

    /// <summary>The shared filter's owner and its last played step.</summary>
    public (int Instrument, int Step) FilterPlayhead() =>
        (_filter.OwnerInstrumentNumber, _filter.LastStep);

    private void RecordCoverage()
    {
        for (int ch = 0; ch < 3; ch++)
        {
            var voice = _channels[ch].Voice;
            if (voice.InstrumentNumber < 0 || voice.InstrumentNumber >= _coverage.Length)
            {
                continue;
            }

            var coverage = _coverage[voice.InstrumentNumber];
            coverage.Record(coverage.Wavetable, voice.LastWavetableStep);
            coverage.Record(coverage.Arpeggio, voice.LastArpeggioStep);

            // Only move the playhead on a step that actually played: -1 is the one-frame gap
            // between a trigger and its first automation frame, and following it there would
            // blink the highlight off on every note.
            if (voice.LastWavetableStep >= 0)
                coverage.CurrentWavetableStep = voice.LastWavetableStep;
            if (voice.LastArpeggioStep >= 0)
                coverage.CurrentArpeggioStep = voice.LastArpeggioStep;
        }

        // The filter is one shared program, owned by whichever instrument claimed it last.
        if (_filter.OwnerInstrumentNumber >= 0 && _filter.OwnerInstrumentNumber < _coverage.Length)
        {
            var coverage = _coverage[_filter.OwnerInstrumentNumber];
            coverage.Record(coverage.Filter, _filter.LastStep);
            if (_filter.LastStep >= 0)
                coverage.CurrentFilterStep = _filter.LastStep;
        }
    }

    /// <summary>Frames played since the top of the song - a resumed sequencer counts on from the
    /// position it was given.</summary>
    public int FramesPlayed => _frame;

    public bool Frame()
    {
        _frame++;
        bool announce = _announceRestored;
        _announceRestored = false;
        Array.Clear(_triggeredThisFrame);

        for (int ch = 0; ch < 3; ch++)
        {
            RunAutomation(ch);
        }

        // Before the row fetch below, same as the automation loop above - not after, as this
        // used to run. A trigger claims the filter in TickChannel below; stepping it here first
        // means that claim isn't read back until next frame, matching the one-frame lag the
        // wavetable/arpeggio automation above already has relative to its own trigger. Stepping
        // it after TickChannel instead lands a fresh Setup on the trigger frame itself - one
        // frame ahead of the wavetable's own first real step rather than in sync with it, which
        // is audible on an instrument like Magnificent's kick whose filter program is its attack
        // transient. Verified against MAGSIDv7_LowMemory\Code\musicplayer.s, which orders
        // Filter_Play the same way, right after the Voice_Automation loop.
        _filter.StepFrame(_write);
        RecordCoverage();

        bool ticked = false;
        _speedCounter--;
        if (_speedCounter < 0)
        {
            if (_song.HasFunk)
            {
                _shuffle ^= _song.Speed ^ _song.FunkSpeed!.Value;
                _speedCounter = _shuffle ^ _song.FunkSpeed.Value;
            }
            else
            {
                _speedCounter = _song.Speed;
            }

            for (int ch = 0; ch < 3; ch++)
            {
                TickChannel(ch);
            }

            ticked = true;
        }

        // After the tick, so a channel that played a hold this frame is named on that hold, and
        // one that triggered a row of its own already has.
        if (announce)
            AnnounceRestoredRows();

        return ticked;
    }

    /// <summary>
    /// Voice_Automation for one channel: a wavetable step, the instrument's ADSR, the pulse
    /// width, and an arpeggio step - every frame, not every tick. Delegated to
    /// <see cref="VoiceEngine.Automate"/>, shared with <see cref="JamEngine"/>, which does
    /// nothing while the voice isn't active - the same way Voice_NoteOff silences all four at
    /// once by clearing a single position.
    /// </summary>
    private void RunAutomation(int ch) => _channels[ch].Voice.Automate(RegisterBase(ch), _write);

    private void TickChannel(int ch)
    {
        var state = _channels[ch];

        if (state.Duration > 0)
        {
            state.Duration--;
        }
        else
        {
            FetchEvent(ch);
        }

        // After the fetch, which starts a new entry at 0 when the last one has run out: this
        // tick is then its first.
        state.TicksIntoEntry++;
    }

    private void FetchEvent(int ch)
    {
        var state = _channels[ch];

        // An order entry whose pattern is gone is passed over; a whole order of them would
        // otherwise go round for ever, so the channel rests a tick instead.
        for (int missing = 0; ; )
        {
            if (PatternFor(state.CurrentPatternNumber) is not { } pattern)
            {
                if (++missing > 256)
                    return;
                AdvanceOrder(ch);
                continue;
            }

            if (state.RowIndex >= pattern.Rows.Count)
            {
                AdvanceOrder(ch);
                continue;
            }

            var row = pattern.Rows[state.RowIndex];

            if (row is LoopStartRow)
            {
                // LS anchors the loop-back point at the *next* real event, and doesn't
                // itself consume a tick. Also resets the finite-repeat counter below, so a
                // fresh pass through this LS always starts counting from zero.
                state.LoopStartRowIndex = state.RowIndex + 1;
                state.LoopRepeatCounter = 0;
                state.RowIndex++;
                continue;
            }

            if (row is LoopEndRow(var repeatCount))
            {
                if (repeatCount is null)
                {
                    // Bare LE: loops back to LS forever, matching today's behavior exactly.
                    state.RowIndex = state.LoopStartRowIndex;
                    continue;
                }

                // Finite LE04 etc: RepeatCount is the *total* number of times the marked
                // section plays, including this completed pass. Loop back while more passes
                // are still owed; once the count is met, fall through past LE and reset the
                // counter so a later independent LS/LE pair (or a fresh pass if this whole
                // pattern loops back around via AdvanceOrder) starts counting from zero again.
                state.LoopRepeatCounter++;
                if (state.LoopRepeatCounter < repeatCount.Value)
                {
                    state.RowIndex = state.LoopStartRowIndex;
                    continue;
                }

                state.LoopRepeatCounter = 0;
                state.RowIndex++;
                continue;
            }

            // A timed event: NoteRow, NoteOffRow, or a HoldRow that no preceding note could
            // absorb. That last case happens when the hold sits on the far side of an LS/LE
            // marker from its note - most often right after an LE row, reached only once the
            // loop finally falls through - and the lookahead below stopped short of it. The
            // grid draws such a hold as a row of its own, so it has to cost time rather than
            // be skipped: it falls past both dispatch arms below, spending its ticks without
            // touching the voice. That mirrors the assembly's $FD "wait" row exactly.
            //
            // Capture where the row actually lives before RowIndex moves past it (and any
            // consumed holds) below.
            int triggeredPatternNumber = state.CurrentPatternNumber;
            int triggeredRowIndex = state.RowIndex;

            // Count how many HoldRow entries immediately follow it - this is the row's
            // duration in ticks, matching Startup\Tables.s's run-length collapsing of "..."
            // rows exactly, just done at playback time instead of build time.
            int duration = 1;
            int look = state.RowIndex + 1;
            while (look < pattern.Rows.Count && pattern.Rows[look] is HoldRow)
            {
                duration++;
                look++;
            }
            state.RowIndex = look;

            // One authored row line is one tick, so a run of `duration` lines occupies
            // `duration` ticks: the fetch tick plays the note and the countdown spends the
            // remaining `duration - 1`. That matches the assembly's "sbc #CMD_DUR", which
            // stores the ticks *after* the one that plays the row.
            state.Duration = duration - 1;

            if (row is NoteOffRow)
            {
                state.Voice.ReleaseNote(RegisterBase(ch), _write, _firstInstrument);
            }
            else if (row is SlideRow slide)
            {
                // A wait row that also sets the slide: the sounding note carries on and glides.
                state.Voice.SetSlide(slide.Rate);
            }
            else if (row is NoteRow noteRow)
            {
                // An instrument that is not there plays nothing new; the voice carries on.
                if (InstrumentFor(noteRow.InstrumentNumber) is { } instrument)
                {
                    state.Voice.TriggerNote(
                        RegisterBase(ch), noteRow.Note, noteRow.InstrumentNumber, instrument, _write);

                    if (instrument.Filter.Count > 0)
                    {
                        _filter.Claim(instrument.Filter, noteRow.InstrumentNumber);
                    }
                }
            }

            _triggeredThisFrame[ch] = true;
            RowTriggered?.Invoke(ch, triggeredPatternNumber, triggeredRowIndex, state.OrderEntrySequence, row);
            return;
        }
    }

    /// <summary>
    /// The song is edited while it plays - an instrument loaded, a pattern drawn - so the tables
    /// built at the start can fall behind it. A number they do not have, have under an object since
    /// renumbered, or have under an object no longer in the song re-reads it. Before, the lookup
    /// threw on the audio thread and the output stopped; and a deleted instrument keeps its number,
    /// so one deleted and replaced while the song played went on sounding in its replacement's place.
    /// </summary>
    private Instrument? InstrumentFor(int number)
    {
        if (_instrumentsByNumber.TryGetValue(number, out var instrument) && instrument.Number == number
            && _song.Instruments.Contains(instrument))
            return instrument;

        _instrumentsByNumber = ByNumber(_song.Instruments, i => i.Number);
        return _instrumentsByNumber.GetValueOrDefault(number);
    }

    private Pattern? PatternFor(int number)
    {
        if (_patternsByNumber.TryGetValue(number, out var pattern) && pattern.Number == number
            && _song.Patterns.Contains(pattern))
            return pattern;

        _patternsByNumber = ByNumber(_song.Patterns, p => p.Number);
        return _patternsByNumber.GetValueOrDefault(number);
    }

    /// <summary>By number, from a list the editor may be changing on another thread - read by
    /// index, and the first of a number kept, so a half-made change cannot throw here.</summary>
    private static Dictionary<int, T> ByNumber<T>(List<T> items, Func<T, int> number)
    {
        var byNumber = new Dictionary<int, T>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items.ElementAtOrDefault(i) is { } item)
                byNumber.TryAdd(number(item), item);
        }

        return byNumber;
    }

    private void AdvanceOrder(int ch)
    {
        var state = _channels[ch];
        var order = _song.ChannelOrders[ch];

        if (state.OrderPos >= order.Tokens.Count)
        {
            // End of song-order: restart at the 1-based RestartPosition (default 1, the
            // start). The order-list always loops - a channel never truly stops from normal
            // playback, matching the assembly's $00-terminator-plus-restart-byte encoding
            // (Voice_AdvanceOrder reads the byte after the $00 straight into voice_orderpos,
            // so the assembly's restart position is 0-based and absolute into the shared
            // tbl_songorder array, where this one is 1-based and per channel).
            state.OrderPos = order.RestartPosition - 1;
        }

        var token = (PatternRefToken)order.Tokens[state.OrderPos];
        state.CurrentPatternNumber = token.PatternNumber;
        state.OrderPos++;
        state.RowIndex = 0;
        state.LoopStartRowIndex = 0;
        state.LoopRepeatCounter = 0;
        state.OrderEntrySequence++;
        state.TicksIntoEntry = 0;
    }

    private static int RegisterBase(int ch) => ch * 7;

    /// <summary>Everything about one channel's place in the song that a resume has to put back.
    /// Not just the order entry and row: a channel stopped inside an LS/LE section is part-way
    /// through a repeat count, and its loop-back target is the LS anchor rather than the top of
    /// the pattern. Restoring only the row leaves that channel starting its loop over, so it
    /// plays the section more times than it had left and drifts away from the other two.</summary>
    public readonly record struct ChannelPlaybackState(
        int OrderPos,
        int CurrentPatternNumber,
        int RowIndex,
        int LoopStartRowIndex,
        int LoopRepeatCounter,
        int Duration,
        int TicksIntoEntry = 0)
    {
        /// <summary>The order entry being played, or -1 before the first. OrderPos is where the
        /// next pattern will be fetched from, one past this - not the entry itself.</summary>
        public int PlayingEntry => OrderPos - 1;
    }

    /// <summary>A whole-song instant: every channel plus the shared tick countdown, captured
    /// together so the parts stay in the same relation to each other when they are put back.</summary>
    /// <param name="Frame">How many frames the song had played to get here, from its top - what
    /// anything that has to replay the song in step from the beginning, such as the 6502 player
    /// behind the live rastertime, needs to know.</param>
    /// <param name="Shuffle">Song_Shuffle at that instant - which of a funk song's two speeds the
    /// tick in progress has.</param>
    public sealed record SequencerPosition(int SpeedCounter, IReadOnlyList<ChannelPlaybackState> Channels, int Frame = 0, int Shuffle = 0)
    {
        /// <summary>The speed of the tick in progress: the song's own, or with funk speed the one
        /// this tick was reloaded with.</summary>
        public int TickSpeed(MagSid.Core.Model.Song song) =>
            song.HasFunk ? Shuffle ^ song.FunkSpeed!.Value : song.Speed;
    }

    /// <summary>The exact instant this sequencer is at. Taken by the playback engine once per
    /// frame so a resume can be lined up with what the listener had actually heard.</summary>
    public SequencerPosition Capture()
    {
        var channels = new ChannelPlaybackState[3];
        for (int ch = 0; ch < 3; ch++)
        {
            var s = _channels[ch];
            channels[ch] = new ChannelPlaybackState(
                s.OrderPos, s.CurrentPatternNumber, s.RowIndex,
                s.LoopStartRowIndex, s.LoopRepeatCounter, s.Duration, s.TicksIntoEntry);
        }

        return new SequencerPosition(_speedCounter, channels, _frame, _shuffle);
    }

    private sealed class ChannelState
    {
        public int OrderPos;
        public int CurrentPatternNumber;
        public int RowIndex;
        public int LoopStartRowIndex;
        public int LoopRepeatCounter;
        public int Duration;
        public int OrderEntrySequence;

        /// <summary>Ticks played of the order entry this channel is in, the current one included -
        /// how far along its clip it is, loops' repeats counted. 0 before its first tick.</summary>
        public int TicksIntoEntry;

        /// <summary>The wavetable/ADSR/pulse/arpeggio automation and note-trigger sequence for
        /// this channel's voice - shared code with JamEngine, see VoiceEngine's own doc comment.
        /// Everything about *this* type is pattern/order navigation; everything about what the
        /// SID voice itself is doing lives there instead.</summary>
        public readonly VoiceEngine Voice = new();
    }
}
