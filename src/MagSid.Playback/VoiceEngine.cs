using MagSid.Core;
using MagSid.Core.Model;

namespace MagSid.Playback;

/// <summary>
/// One SID voice's per-frame automation - wavetable, ADSR, pulse sweep, arpeggio - plus the
/// note-trigger hard-restart sequence and note-off. Extracted out of <see cref="SongSequencer"/>
/// so the exact same, carefully-tuned behavior (see <see cref="TriggerNote"/>'s hard-restart
/// comment) drives a voice whether it is advancing through a Pattern's rows (SongSequencer) or
/// answering a live key press (<see cref="JamEngine"/>). Owns no notion of *when* a note starts
/// or stops, or of the SID's shared filter - only what one voice already told to sound (or
/// release) does, frame by frame; the caller decides when to call which method.
/// </summary>
public sealed class VoiceEngine
{
    /// <summary>0 = nothing running on this voice; otherwise the 1-based step the instrument's
    /// wavetable has reached. Doubles as the master switch for all of the per-frame automation,
    /// exactly as voice_wt_pos does in the assembly.</summary>
    public bool IsActive => _wavetablePosition != 0;

    /// <summary>True once the WAVE program has stepped past its own last frame and is now just
    /// holding the final control byte - i.e. it has "played through once" - false while nothing
    /// has ever been triggered and false while still mid-program. `_wavetablePosition` stops
    /// incrementing the moment the program runs out (see <see cref="Automate"/>'s own `step <
    /// wavetable.Count` guard), so this is monotonic: once true for a given trigger, it stays
    /// true until the next <see cref="TriggerNote"/>. Real song playback (SongSequencer) never
    /// needs this; it exists for JamEngine, which has to know whether an early key-up may
    /// release the note yet or must wait for at least one full playthrough of a short percussive
    /// program first - see JamEngine.NoteOff's own comment.</summary>
    public bool HasWavetableCompleted =>
        _instrument is not null && _wavetablePosition - 1 >= WavetableOf(_instrument).Count;

    /// <summary>
    /// True once the note program has run past its last step - which is where ROOT takes over, a
    /// HOLD settles, and a loop turns back.
    ///
    /// Separate from the wavetable because the two are not the same length. Magnificent's KickBass
    /// has three wavetable steps and four arpeggio steps, so waiting only for the wavetable let a
    /// short key-up cut the pitch sweep before it ever reached its root note - the drum's whole
    /// character, gone, on exactly the short taps a drum gets played with.
    /// </summary>
    public bool HasArpeggioCompleted =>
        _instrument is null || !_instrument.HasArpeggio || _arpeggioCompleted;

    /// <summary>Both programs have had their say. What a key-up waits for.</summary>
    public bool HaveProgramsCompleted => HasWavetableCompleted && HasArpeggioCompleted;

    /// <summary>The 0-based step each program last actually played on this voice, or -1 when it
    /// has not played one since the last trigger. Reported rather than derived from the position
    /// counters: the wavetable's stops advancing once the program runs out and the arpeggio's
    /// wraps on a loop, so neither counter alone says what was last written.</summary>
    public int LastWavetableStep { get; private set; } = -1;
    public int LastArpeggioStep { get; private set; } = -1;

    /// <summary>The instrument last triggered on this voice - kept for callers that need to
    /// name it (SongSequencer's resume/capture, in numbers rather than the object itself).
    /// -1 before anything has ever triggered.</summary>
    public int InstrumentNumber { get; private set; } = -1;

    private Instrument? _instrument;
    private int _wavetablePosition;
    private int _arpeggioIndex = -1;
    private bool _arpeggioCompleted;
    private NoteName _rootNote;
    private int _pulseLow;
    private int _pulseHigh;
    private int _lastControlValue;

    // Slide/vibrato, mirroring Voice_Automation's .freqmod section: the frequency the note or arpeggio
    // step last asked for (the SID's own registers cannot be read back), the running 16-bit
    // offset added to it, and the counters that decide when the offset turns round.
    private int _baseFreq;
    private int _freqOffset;
    private int _vibDelay;
    private int _vibCount;
    private bool _vibDown;

    /// <summary>
    /// Hard restart, this frame: zero AD and drop the previous note's envelope at a fast release
    /// rate, gate off - the waveform kept or cut with TEST, as MagSid.Core.HardRestart says -
    /// unless the outgoing instrument opted out
    /// via NoHardRestart, in which case none of that happens: no AD/SR write, no gate pulse, no
    /// oscillator reset. The frequency change above still lands, so a NoHardRestart retrigger is
    /// a genuine legato - the envelope simply continues wherever it already was (still decaying,
    /// already in sustain, whatever), on the new pitch, with no re-attack at all. Arming the
    /// wavetable is what makes the *next* frame open the gate on the real waveform when a hard
    /// restart does run.
    ///
    /// Release 7, not 0 - see Voice_PlayCurrent's own matching comment for the full mechanism:
    /// dropping the release rate's period below wherever the envelope's rate counter already
    /// sits (which release 0 does to any note that had been sustaining at a slower decay rate)
    /// triggers a real SID hardware quirk where the counter has to wrap its full 15-bit range
    /// before the envelope can move at all - longer than the one frame before the next attack,
    /// so the old note is still at full value when attack starts, and attack's own
    /// (envelope+1)&amp;0xff then freezes it at zero instead of rolling over into decay/sustain.
    /// Every instrument this song uses tops out at decay 7, so release 7's own period is always
    /// at least as large as whatever the counter was already counting towards - the release
    /// genuinely runs instead of stalling.
    ///
    /// This engine is shared by every voice, so a naive "pick the biggest release" doesn't
    /// generalize: a too-large release (verified against real rendered audio, not register
    /// writes alone) starves this frame's own reset entirely - period 15's 31251 cycles exceeds
    /// one whole frame (19705 cycles at 50Hz), so rate_counter never wraps back down during
    /// frame A, and the *next* frame's real attack (a small, fast period) then stalls instead.
    /// The safe range is bounded on both sides: release's period must be &gt;= the old note's own
    /// decay/attack period (or frame A stalls) and small enough to actually complete resets
    /// within one frame (or frame B stalls). Instruments with a slower decay than 7 are not
    /// covered by this constant - see Instrument.NoHardRestart's own comment for how those opt
    /// out instead of needing a universally-safe constant that does not actually exist.
    /// </summary>
    public void TriggerNote(int baseReg, NoteName note, int instrumentNumber, Instrument instrument,
        Action<int, int> write)
    {
        var (lo, hi) = NotePalTable.GetFreq(note);
        write(baseReg + 0, lo);
        write(baseReg + 1, hi);
        _baseFreq = (hi << 8) | lo;

        // The flag belongs to the OUTGOING instrument, not this new one: it is whichever
        // instrument's own decay/attack rate this voice's envelope was actually running under a
        // moment ago that the reset pair's period risks colliding with. IsActive false (nothing
        // was playing) means there is no outgoing instrument to consult and the reset proceeds
        // as normal.
        bool skipHardReset = IsActive && _instrument is { NoHardRestart: true };
        if (!skipHardReset)
        {
            write(baseReg + 5, 0x00);
            write(baseReg + 6, 0x07);

            // See MagSid.Core.HardRestart: gate off with the waveform kept, or $08.
            write(baseReg + 4, HardRestart.KeepsWaveform ? _lastControlValue & 0xFE : 0x08);
        }

        InstrumentNumber = instrumentNumber;
        _instrument = instrument;
        _wavetablePosition = 1;
        _arpeggioIndex = instrument.HasArpeggio ? 0 : -1;
        _arpeggioCompleted = false;
        LastWavetableStep = -1;
        LastArpeggioStep = -1;

        // Kept separately from the trigger call because a note program's Root step can be
        // reached many frames after the trigger - a held bass note reaches it on frame two and
        // then stays there for the rest of the note.
        _rootNote = note;

        _freqOffset = 0;
        _vibDown = false;
        _vibDelay = (instrument.VibratoDelaySpeed & 0xF0) >> 2;
        _vibCount = (instrument.VibratoDelaySpeed & 0x0F) >> 1;

        // Continuous: the sweep belongs to the voice, so a new note leaves it running instead of
        // restarting it here - matching Voice_PlayCurrent's bit-6 check.
        if (!instrument.PulseContinuous)
        {
            _pulseHigh = instrument.PulseWidth;
            _pulseLow = 0;
        }
    }

    /// <summary>Clears only the gate bit - clearing the whole register would also wipe the
    /// waveform-select bits, silencing the oscillator instantly and masking whatever release
    /// curve the envelope generator is still gracefully doing. Stopping the wavetable matters
    /// just as much: it writes the control register every frame and would put the gate straight
    /// back on. It also stops the ADSR, pulse and arpeggio writes, since all of them hang off
    /// this one position.</summary>
    public void ReleaseNote(int baseReg, Action<int, int> write)
    {
        write(baseReg + 4, _lastControlValue & ~0x01);
        _wavetablePosition = 0;
    }

    /// <summary>A "panic button" reset rather than a release: forces this voice silent right now,
    /// regardless of whatever release curve it was already in the middle of. Reuses
    /// <see cref="TriggerNote"/>'s own hard-restart reset pair (gate off, TEST on, AD 0, release
    /// 7 - see that method's long comment for exactly why release 7 and not 0) rather than
    /// zeroing release directly: release 0 is the SID's *fastest* rate, and dropping straight to
    /// it from whatever slower rate this voice's envelope was already counting under is the same
    /// hardware quirk TriggerNote's comment describes - the envelope can stall for seconds
    /// instead of going silent. The same documented gap applies here too: an instrument with a
    /// slower decay than 7 is not perfectly covered by this constant.</summary>
    public void Silence(int baseReg, Action<int, int> write)
    {
        if (!IsActive)
        {
            return;
        }

        write(baseReg + 5, 0x00);
        write(baseReg + 6, 0x07);
        write(baseReg + 4, 0x08); // gate off, TEST on - no waveform bits, so this stays silent
        _wavetablePosition = 0;
    }

    /// <summary>One frame of automation: a wavetable step, the instrument's ADSR, the pulse
    /// width, and an arpeggio step - every frame, not every tick. Does nothing while
    /// <see cref="IsActive"/> is false, which is how <see cref="ReleaseNote"/> silences all four
    /// at once by clearing a single position.</summary>
    public void Automate(int baseReg, Action<int, int> write)
    {
        if (!IsActive)
        {
            return;
        }

        var instrument = _instrument!;

        // One control-register byte per frame. An instrument with no WAVE section compiles to
        // the single step { waveform | GATE }, so it opens the gate on the frame after the
        // trigger and then holds - which is what every instrument did before wavetables. Once
        // the program runs out the register is simply left alone, and that hold is what lets a
        // drum settle on its final waveform without needing a loop.
        var wavetable = WavetableOf(instrument);
        int step = _wavetablePosition - 1;
        if (step < wavetable.Count)
        {
            int control = wavetable[step];
            LastWavetableStep = step;
            write(baseReg + 4, control);
            _lastControlValue = control;
            _wavetablePosition++;
        }

        // Written every frame rather than once, because the trigger frame deliberately zeroes
        // ADSR for the hard restart and the real envelope has to arrive on the frame after.
        // Rewriting the same two bytes afterwards has no effect on the chip.
        write(baseReg + 5, instrument.AttackDecay);
        write(baseReg + 6, instrument.SustainRelease);

        StepPulseWidth(instrument, baseReg, write);
        StepArpeggio(instrument, baseReg, write);
        StepFrequencyModulation(instrument, baseReg, write);
    }

    /// <summary>
    /// Slide and vibrato, mirroring Voice_Automation's .freqmod section. Both only move one signed
    /// 16-bit offset from the bare note, and the sum is written once at the end. Runs after the
    /// arpeggio step and rewrites the frequency, because the arpeggio writes the bare note every
    /// frame it is active.
    ///
    /// Slide is GoatTracker's 1xx/2xx as an instrument property: <see cref="Instrument.SlideRate"/>
    /// is added every frame the note sounds. Vibrato keeps to a 16-bit add too, so the C64 player
    /// needs no multiply: after the delay (top nibble, units of four frames) the offset moves by
    /// <see cref="Instrument.VibratoDepth"/> each frame, one way for speed+1 frames (low nibble)
    /// and then the other, so the pitch traces a triangle. The first run is only half as long,
    /// which centres the wave on the note instead of hanging it off one side.
    /// </summary>
    private void StepFrequencyModulation(Instrument instrument, int baseReg, Action<int, int> write)
    {
        if (instrument.SlideRate == 0 && instrument.VibratoDepth == 0)
        {
            return;
        }

        if (instrument.SlideRate != 0)
        {
            _freqOffset = (_freqOffset + instrument.SlideRate) & 0xFFFF;
        }

        if (instrument.VibratoDepth != 0)
        {
            if (_vibDelay > 0)
            {
                _vibDelay--;
            }
            else
            {
                _freqOffset = (_freqOffset + (_vibDown ? -instrument.VibratoDepth : instrument.VibratoDepth)) & 0xFFFF;
                if (--_vibCount < 0)
                {
                    _vibDown = !_vibDown;
                    _vibCount = instrument.VibratoDelaySpeed & 0x0F;
                }
            }
        }

        int frequency = (_baseFreq + _freqOffset) & 0xFFFF;
        write(baseReg + 0, frequency & 0xFF);
        write(baseReg + 1, frequency >> 8);
    }

    /// <summary>
    /// The 12-bit pulse width kept in a shadow, because $D402/$D403 cannot be read back. The
    /// sweep is a signed byte added with sign extension, and the result is masked back to 12
    /// bits - so a sweep that runs off either end wraps round rather than sticking, matching the
    /// assembly's closing "and #$0f" exactly.
    /// </summary>
    private void StepPulseWidth(Instrument instrument, int baseReg, Action<int, int> write)
    {
        int low = _pulseLow + (instrument.PulseSweep & 0xFF);
        int carry = low >> 8;
        _pulseLow = low & 0xFF;
        write(baseReg + 2, _pulseLow);

        int signExtension = instrument.PulseSweep < 0 ? 0xFF : 0x00;
        _pulseHigh = (_pulseHigh + carry + signExtension) & 0x0F;

        write(baseReg + 3, _pulseHigh & 0x0F);
    }

    /// <summary>
    /// One note per frame, running in step with the wavetable. The notes are absolute - they
    /// override the triggering note rather than transposing it - which is why a step costs one
    /// byte in the assembled song and adds nothing to the frequency table.
    ///
    /// What happens after the last step is the whole difference between an arpeggio and a drum:
    /// Loop cycles, Hold stays on the last step, and Root settles onto the note that actually
    /// triggered. Root is why a bass can open with a fixed high pitch on noise and still end up
    /// playing the song.
    /// </summary>
    private void StepArpeggio(Instrument instrument, int baseReg, Action<int, int> write)
    {
        if (_arpeggioIndex < 0)
        {
            return;
        }

        var steps = instrument.Arpeggio;

        if (_arpeggioIndex >= steps.Count)
        {
            // Recorded before the switch, because two of the three endings move the index back and
            // a caller looking at it afterwards would never see the program reach its end.
            _arpeggioCompleted = true;

            switch (instrument.ArpeggioEnding)
            {
                case ArpeggioEnding.Root:
                    WriteFrequency(baseReg, _rootNote, write);
                    return;              // stays here for good, exactly like the assembly's $80

                case ArpeggioEnding.Hold:
                    _arpeggioIndex = steps.Count - 1;
                    break;

                default:
                    // Back to the loop point, which is only 0 for a plain arpeggio. A drum loops
                    // into the middle of its own list so it rings out on its tail.
                    _arpeggioIndex = instrument.ArpeggioLoopPoint;
                    break;
            }
        }

        LastArpeggioStep = _arpeggioIndex;
        WriteFrequency(baseReg, steps[_arpeggioIndex], write);
        _arpeggioIndex++;
    }

    private void WriteFrequency(int baseReg, NoteName note, Action<int, int> write)
    {
        var (lo, hi) = NotePalTable.GetFreq(note);
        write(baseReg + 0, lo);
        write(baseReg + 1, hi);
        _baseFreq = (hi << 8) | lo;
    }

    /// <summary>The instrument's WAVE program, or the implicit single step an instrument without
    /// one gets.</summary>
    private static IReadOnlyList<byte> WavetableOf(Instrument instrument) =>
        instrument.Wavetable.Count > 0
            ? instrument.Wavetable
            : [(byte)(instrument.Waveform | 0x01)];
}
