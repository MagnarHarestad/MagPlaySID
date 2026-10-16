using MagSid.Core.Model;
using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Playback;

/// <summary>
/// Live, polyphonic jamming: a key held down sounds a note with its instrument's full automation
/// running - wavetable, arpeggio, pulse sweep, filter program - for as long as the key stays
/// down, releasing on key-up. Up to three notes at once, one per SID voice, exactly as many as
/// the chip actually has; a fourth steals the oldest-held voice rather than being dropped
/// silently. Reuses <see cref="VoiceEngine"/> and <see cref="FilterEngine"/> - the same code
/// <see cref="SongSequencer"/> drives from pattern rows - so a jammed instrument sounds exactly
/// as it would in the song, not an approximation of it.
///
/// The output stays open once started rather than being opened per note, the same reasoning
/// <see cref="NotePreviewPlayer"/> documents: opening a device takes long enough to be audible as
/// lag, and it simply feeds silence between chords.
/// </summary>
public sealed class JamEngine : IWaveProvider, IDisposable
{
    private const double ClockFrequency = 985248.0; // PAL C64
    private const double FrameRate = 50.0; // matches Voice_Automation/Filter_Play - one frame, always
    private const int SampleRate = 44100;
    private const int VoiceCount = 3;

    // NoteOn/NoteOff arrive from the UI thread (key events); Read() clocks the chip and steps
    // automation from NAudio's audio callback thread. Both go through this lock, same as
    // NotePreviewPlayer - the critical sections are a handful of register writes and one
    // ClockAndSample call, so contention is not a concern.
    private readonly object _chip = new();
    private readonly SidChip _sid = new();
    private readonly Action<int, int> _write;
    private readonly VoiceEngine[] _voices = [new(), new(), new()];
    private readonly FilterEngine _filter = new();

    // Which note currently owns each voice slot (null = free), and when it was last (re)claimed -
    // an ever-incrementing counter, not a timestamp, so "oldest" is a plain comparison with no
    // clock involved. Both arrays are read and written only under _chip.
    private readonly NoteName?[] _slotNotes = new NoteName?[VoiceCount];
    private readonly int[] _slotAge = new int[VoiceCount];
    private int _nextAge;

    // Every held note ignores an early NoteOff (see NoteOff below) until its own WAVE program has
    // played through once, in both Edit and Jam mode - a quick tap or a short jam always hears
    // the same deterministic program instead of being cut off at whatever frame the key happened
    // to come up on. Records that a NoteOff already arrived early and is waiting for Frame() to
    // notice completion and finish the release. Cleared on every fresh TriggerNote and by
    // SilenceAll, same as _slotNotes/_slotAge.
    private readonly bool[] _slotPendingRelease = new bool[VoiceCount];

    private readonly double _cyclesPerFrame = ClockFrequency / FrameRate;
    private double _totalCyclesClocked;
    private double _nextFrameBoundaryCycles;

    private IAudioOutput? _output;
    private bool _disposed;

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    /// <param name="writeRegister">Where register writes go - defaults to this engine's own
    /// chip, which is what actually produces sound. Tests pass their own capturing delegate
    /// instead, the same way SongSequencerTests drives SongSequencer, to check voice allocation
    /// and triggering without needing an audio device or reading register state back out of a
    /// chip that (like real SID hardware) mostly can't be read.</param>
    public JamEngine(ChipModel chipModel = ChipModel.Mos6581, Action<int, int>? writeRegister = null)
    {
        _sid.SetChipModel(chipModel);
        _sid.SetSamplingParameters(ClockFrequency, SamplingMethod.Interpolate, SampleRate);
        _write = writeRegister ?? _sid.Write;

        // Matches music.init's "lda #$0f / sta $d418", and SongSequencer's own constructor for
        // the identical reason - without it the filter's Vol register stays 0 and every voice is
        // silent for any instrument that never claims the filter for itself.
        _write(0x18, 0x0f);
        _sid.SettleStartupTransient(ClockFrequency);

        Array.Fill(_slotNotes, null);
    }

    /// <summary>Opens the output device if it isn't already running. Not called automatically
    /// from <see cref="NoteOn"/> - separated out, the same way NotePreviewPlayer.Trigger is split
    /// from Play, so voice allocation and automation can be tested without an audio device.
    /// Idempotent; the caller (MainForm) calls it once when jamming becomes possible rather than
    /// on every note.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        EnsureOutputRunning();
    }

    /// <summary>Sounds a note with the given instrument's full program - not just its first
    /// step, the whole wavetable/arpeggio/filter running frame by frame for as long as the note
    /// stays held. Retriggers in place if this exact note is already sounding (fast repeats of
    /// the same key), otherwise takes a free voice, or steals whichever held note is oldest if
    /// all three are already busy - the chip only has three, the same limit real hardware
    /// jamming a chord runs into.</summary>
    public void NoteOn(NoteName note, Instrument instrument)
    {
        if (_disposed)
        {
            return;
        }

        lock (_chip)
        {
            int slot = Array.IndexOf(_slotNotes, note);
            if (slot < 0)
            {
                slot = Array.IndexOf(_slotNotes, null);
            }
            if (slot < 0)
            {
                slot = OldestSlot();
            }

            _voices[slot].TriggerNote(RegisterBase(slot), note, instrument.Number, instrument, _write);
            _slotNotes[slot] = note;
            _slotAge[slot] = _nextAge++;
            _slotPendingRelease[slot] = false;

            if (instrument.Filter.Count > 0)
            {
                _filter.Claim(instrument.Filter);
            }
        }
    }

    /// <summary>
    /// Takes the filter back to how the engine starts: no voice through it, no resonance, volume
    /// 15, no program or sweep running. A song leaves the filter with whichever instrument claimed
    /// it last, and so does jamming - but sounds auditioned one after another are each to be heard
    /// as they are, not through the filter the one before set up.
    /// </summary>
    public void ResetFilter()
    {
        if (_disposed)
            return;

        lock (_chip)
        {
            _filter.Reset();
            _write(0x17, 0x00);
            _write(0x18, 0x0f);
        }
    }

    /// <summary>Releases the voice currently sounding this note, if any - a key-up for a note
    /// that was stolen by a later chord note, or that was never sounding (e.g. two keys mapped
    /// to the same note), is simply ignored rather than releasing whatever else happens to be
    /// playing. If that voice's own programs haven't played through once yet (see
    /// VoiceEngine.HaveProgramsCompleted), the release itself is deferred instead - in both Edit
    /// and Jam mode, a note is guaranteed to at least finish its own programs once, however
    /// briefly the key was actually held, rather than being cut wherever the key happened to come
    /// up. Both programs, not just the wavetable: they are not the same length, and a drum whose
    /// pitch sweep is longer than its waveform list would otherwise lose its root note on exactly
    /// the short taps a drum gets played with. The slot stays marked busy while deferred (so
    /// NoteOn can't steal it out from under the still-sounding note) and Frame() finishes the
    /// release the moment both programs actually complete.</summary>
    public void NoteOff(NoteName note)
    {
        lock (_chip)
        {
            int slot = Array.IndexOf(_slotNotes, note);
            if (slot < 0)
            {
                return;
            }

            if (!_voices[slot].HaveProgramsCompleted)
            {
                _slotPendingRelease[slot] = true;
                return;
            }

            _voices[slot].ReleaseNote(RegisterBase(slot), _write);
            _slotNotes[slot] = null;
            _slotPendingRelease[slot] = false;
        }
    }

    /// <summary>The panic button: forces every voice silent right now - see
    /// <see cref="VoiceEngine.Silence"/> for why this is a hard reset rather than three
    /// <see cref="NoteOff"/> calls. Used when switching Jam/Edit mode, where an instant, complete
    /// silence matters more than letting whatever was playing ring out its own release.</summary>
    public void SilenceAll()
    {
        lock (_chip)
        {
            for (int i = 0; i < VoiceCount; i++)
            {
                _voices[i].Silence(RegisterBase(i), _write);
                _slotNotes[i] = null;
                _slotPendingRelease[i] = false;
            }
        }
    }

    private int OldestSlot()
    {
        int oldest = 0;
        for (int i = 1; i < VoiceCount; i++)
        {
            if (_slotAge[i] < _slotAge[oldest])
            {
                oldest = i;
            }
        }
        return oldest;
    }

    private static int RegisterBase(int slot) => slot * 7;

    private void EnsureOutputRunning()
    {
        if (_output is not null)
        {
            return;
        }

        _output = AudioOutput.Start(this, latencyMs: 100);
    }

    /// <summary>One frame of automation for every voice, then the filter - same order
    /// SongSequencer.Frame uses. Public, and takes no lock of its own, so tests can drive voice
    /// allocation and automation directly without an audio device; <see cref="Read"/> is what
    /// calls it in real use, already holding <see cref="_chip"/> for the whole buffer.</summary>
    public void Frame()
    {
        for (int i = 0; i < VoiceCount; i++)
        {
            _voices[i].Automate(RegisterBase(i), _write);

            // Finishes a release NoteOff had to defer (see NoteOff's own comment) the moment both
            // of this voice's programs actually complete - checked after Automate so a completion
            // that happens on THIS frame is caught immediately rather than one frame late.
            if (_slotPendingRelease[i] && _voices[i].HaveProgramsCompleted)
            {
                _voices[i].ReleaseNote(RegisterBase(i), _write);
                _slotNotes[i] = null;
                _slotPendingRelease[i] = false;
            }
        }
        _filter.StepFrame(_write);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesRequested = count / 2;
        var samples = new short[samplesRequested];
        int samplesWritten = 0;

        lock (_chip)
        {
            while (samplesWritten < samplesRequested)
            {
                double cyclesToNextFrame = _nextFrameBoundaryCycles - _totalCyclesClocked;
                if (cyclesToNextFrame < 1.0)
                {
                    Frame();
                    _nextFrameBoundaryCycles += _cyclesPerFrame;
                    continue;
                }

                int deltaT = (int)cyclesToNextFrame;
                int deltaTRemaining = deltaT;
                int produced = _sid.ClockAndSample(
                    ref deltaTRemaining, samples, samplesWritten, samplesRequested - samplesWritten);

                if (produced == 0)
                {
                    break; // shouldn't happen, but never spin the audio thread
                }

                _totalCyclesClocked += deltaT - deltaTRemaining;
                samplesWritten += produced;
            }
        }

        Buffer.BlockCopy(samples, 0, buffer, offset, samplesWritten * 2);
        return samplesWritten * 2;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _output?.Dispose();
        _output = null;
    }
}
