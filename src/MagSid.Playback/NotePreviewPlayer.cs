using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Playback;

/// <summary>
/// Auditions a single note through the SID emulation while editing, so entering one is audible
/// straight away instead of only after starting playback.
///
/// Deliberately not the song engine: this keeps its own <see cref="SidChip"/> and its own output
/// device, running one voice with no sequencer attached. Note entry is blocked while the song
/// plays (see TrackerGridControl.OnKeyDown), so the two never fight over the sound card.
///
/// The output stays open once started rather than being opened per note - opening a device takes
/// long enough to be audible as lag, and closing one mid-tail cuts the note off with a click. It
/// simply feeds silence between notes, which costs one idle SID voice.
/// </summary>
public sealed class NotePreviewPlayer : IWaveProvider, IDisposable
{
    private const double ClockFrequency = 985248.0; // PAL C64
    private const int SampleRate = 44100;

    /// <summary>How long the gate is held before release takes over. Short on purpose: this is
    /// a pitch/instrument check while typing, not a performance - the note should confirm what
    /// was entered and get out of the way.</summary>
    private static readonly TimeSpan GateHold = TimeSpan.FromMilliseconds(120);

    /// <summary>Release nibble forced into the instrument's sustain/release byte. 8 is ~300ms on
    /// a real 6581, so the tail is audible but done before the next keystroke - whatever release
    /// the instrument itself carries could be 24 seconds, which would pile notes on top of each
    /// other while editing.</summary>
    private const int PreviewRelease = 8;

    // The audio thread clocks the chip inside Read() while the UI thread pokes registers from
    // Play(). Both go through this lock; the critical sections are a handful of register writes
    // and one ClockAndSample call, so contention is not a concern.
    private readonly object _chip = new();
    private readonly SidChip _sid = new();

    private IAudioOutput? _output;
    private int _samplesUntilGateOff = -1; // -1 = nothing sounding
    private int _gateOffRegister;
    private bool _disposed;

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    public NotePreviewPlayer(ChipModel chipModel = ChipModel.Mos6581)
    {
        _sid.SetChipModel(chipModel);
        _sid.SetSamplingParameters(ClockFrequency, SamplingMethod.Interpolate, SampleRate);
        _sid.Write(0x18, 0x0f); // master volume - without it every voice is silent
        _sid.SettleStartupTransient(ClockFrequency);
    }

    /// <summary>Sounds one note with the given instrument's waveform and attack/decay. Call from
    /// the UI thread; retriggering while a previous note is still ringing simply takes it over,
    /// which is what fast typing sounds like on real hardware too.</summary>
    public void Play(NoteName note, Instrument instrument)
    {
        if (_disposed)
        {
            return;
        }

        EnsureOutputRunning();
        Trigger(note, instrument);
    }

    /// <summary>Writes the note to the chip without touching the output device. <see cref="Play"/>
    /// is the audible path; this is split out so the envelope can be rendered and inspected
    /// without a sound card in the way.</summary>
    public void Trigger(NoteName note, Instrument instrument)
    {
        var (lo, hi) = NotePalTable.GetFreq(note);
        int sustain = instrument.SustainRelease & 0xF0;
        // An instrument with a wavetable starts on its first step, not on its base waveform - a
        // drum whose program opens with noise should audition as noise. Only the first step:
        // there is no frame clock here, so the rest of the program cannot run.
        int control = (instrument.Wavetable.Count > 0 ? instrument.Wavetable[0] : instrument.Waveform)
                      | 0x01; // gate on

        lock (_chip)
        {
            _sid.Write(0x00, lo);
            _sid.Write(0x01, hi);

            // The instrument's starting pulse width. Without it $D402/$D403 stay at 0, and a
            // pulse instrument auditions as silence - which reads as a broken preview rather
            // than as a missing setting. The sweep is deliberately not applied: this has no
            // frame clock, and a 120ms audition is about pitch and timbre, not movement.
            _sid.Write(0x02, 0x00);
            _sid.Write(0x03, instrument.PulseWidth & 0x0F);

            _sid.Write(0x05, instrument.AttackDecay);
            _sid.Write(0x06, sustain | PreviewRelease);
            _sid.Write(0x04, control);

            _gateOffRegister = control & ~0x01;
            _samplesUntilGateOff = (int)(GateHold.TotalSeconds * SampleRate);
        }
    }

    private void EnsureOutputRunning()
    {
        if (_output is not null)
        {
            return;
        }

        // Small buffers: this is a keystroke response, so latency matters more than the safety
        // margin a music player would want.
        _output = AudioOutput.Start(this, latencyMs: 100);
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
                // Stop exactly on the gate-off boundary so the release always starts after the
                // same hold, whatever size buffer the driver happens to ask for.
                int samplesThisPass = samplesRequested - samplesWritten;
                if (_samplesUntilGateOff >= 0 && _samplesUntilGateOff < samplesThisPass)
                {
                    samplesThisPass = Math.Max(1, _samplesUntilGateOff);
                }

                int deltaT = (int)(ClockFrequency / SampleRate * samplesThisPass) + 16;
                int deltaTRemaining = deltaT;
                int produced = _sid.ClockAndSample(ref deltaTRemaining, samples, samplesWritten, samplesThisPass);

                if (produced == 0)
                {
                    break; // shouldn't happen, but never spin the audio thread
                }

                samplesWritten += produced;

                if (_samplesUntilGateOff >= 0)
                {
                    _samplesUntilGateOff -= produced;
                    if (_samplesUntilGateOff <= 0)
                    {
                        // Clear only the gate bit - clearing the whole register would drop the
                        // waveform select too and cut the note dead instead of letting it
                        // release, the same trap the assembly player's Voice_NoteOff hit.
                        _sid.Write(0x04, _gateOffRegister);
                        _samplesUntilGateOff = -1;
                    }
                }
            }
        }

        Buffer.BlockCopy(samples, 0, buffer, offset, samplesRequested * 2);
        return samplesRequested * 2;
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
