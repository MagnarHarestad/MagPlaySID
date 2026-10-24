using MagSid.Core.Model;
using MagSid.Playback;
using MagSid.Sid;

namespace MagSid.Editor.App;

/// <summary>
/// Frame-by-frame SID signal-path trace for one instrument: register writes, gate, envelope,
/// oscillator and the final mixed output, all read straight out of a live <see cref="SidChip"/>
/// driven by the real <see cref="SongSequencer"/> - not a re-derivation from source, the actual
/// emulation this editor uses for playback and preview.
///
/// The instrument is triggered in a small synthetic one-note song built just for this trace
/// (channel 1 gets the real note; channels 2/3 get a silent hold-only pattern, since
/// SongSequencer always needs all three channels' order lists populated) rather than reading
/// from whatever is live in the main window - that keeps the trace reproducible and free of
/// interference from other channels fighting over the one shared filter.
/// </summary>
public static class SignalPathTrace
{
    private const double ClockFrequency = 985248.0; // PAL C64
    public const double FrameRate = 50.0;
    private const int SamplesPerFrame = 24; // sub-frame resolution for the osc/env/out sparklines

    /// <summary>Hold rows padded onto the audition song after the traced span. A channel order
    /// with a single pattern in it loops, which would retrigger the note and make one press of
    /// Trace sound like two; these push the loop point far beyond any time the audition is
    /// stopped, so the retrigger is unreachable rather than merely unlikely.</summary>
    private const int LoopGuardFrames = 250;

    public sealed record WriteEvent(int Register, int Value);

    public sealed class FrameData
    {
        public List<WriteEvent> Writes = [];
        public bool GateOn;
        public bool TestBit;
        public int ControlRegister;
        public int EnvelopeValue;
        public EnvelopeGenerator.EnvelopeState EnvelopeState;
        public int[] OscSamples = [];
        public int[] EnvelopeSamples = [];
        public int[] OutSamples = [];

        /// <summary>$D416 - the running cutoff, held between steps like the real one-shot filter
        /// program does (see Filter_Play's own comment in Code\musicplayer.s).</summary>
        public int FilterCutoff;

        /// <summary>$D417 - resonance (high nibble) + which voices are routed through the
        /// filter (low nibble).</summary>
        public int FilterResRoute;

        /// <summary>$D418 - filter mode (high nibble) + master volume (low nibble).</summary>
        public int FilterModeVol;

        public bool FilterResRouteChanged;
    }

    public sealed class TraceResult
    {
        public List<FrameData> Frames = [];
        public int TriggerFrame = -1;
        public int MasterVolume;
        public string InstrumentLabel = "";
        public string NoteLabel = "";

        /// <summary>The one-note song the trace was taken from - what Trace plays.</summary>
        public Song Song = new();

        /// <summary>The line under the toolbar: what was traced, and when the note started.</summary>
        public string Summary => TriggerFrame < 0
            ? $"{InstrumentLabel}  note {NoteLabel}  - never triggered (check the instrument's own data)"
            : $"{InstrumentLabel}  note {NoteLabel}  triggered on frame {TriggerFrame}  master vol $0f";
    }

    /// <summary>How an instrument is named in the picker: its number, and its name when it has one.</summary>
    public static string Label(Instrument instrument) =>
        $"{instrument.Number:d2}{(string.IsNullOrWhiteSpace(instrument.Name) ? "" : $" : {instrument.Name}")}";

    public static TraceResult Capture(Instrument instrument, NoteName note, int frameCount)
    {
        var song = BuildSingleNoteSong(instrument, note, frameCount);

        var sid = new SidChip();
        sid.SetChipModel(ChipModel.Mos8580);
        sid.SetSamplingParameters(ClockFrequency, SamplingMethod.Interpolate, 44100);

        var result = new TraceResult { Frames = new List<FrameData>(frameCount) };
        List<WriteEvent> pendingWrites = [];
        int controlRegister = 0;

        // The filter is one shared chip resource, not per-voice, and it only ever moves once
        // per frame (StepFilter/StepFilterSweep), so - unlike osc/env/out below - there is
        // nothing to sub-sample within a frame: one running value each is enough.
        int filterCutoff = 0;
        int filterResRoute = 0;
        int filterModeVol = 0;

        void CaptureWrite(int reg, int value)
        {
            pendingWrites.Add(new WriteEvent(reg, value));
            switch (reg)
            {
                case 4: controlRegister = value; break;
                case 0x16: filterCutoff = value; break;
                case 0x17: filterResRoute = value; break;
                case 0x18: filterModeVol = value; break;
            }
            sid.Write(reg, value);
        }

        var sequencer = new SongSequencer(song, CaptureWrite);
        pendingWrites.Clear(); // the constructor's own master-volume write - not a traced frame

        int triggerFrame = -1;
        sequencer.RowTriggered += (channel, _, _, _, row) =>
        {
            if (channel == 0 && row is NoteRow && triggerFrame < 0)
            {
                triggerFrame = result.Frames.Count;
            }
        };

        double cyclesTarget = 0;
        int cyclesClocked = 0;

        var voice = sid.Voice(0);

        for (int frame = 0; frame < frameCount; frame++)
        {
            pendingWrites.Clear();
            sequencer.Frame();

            var frameData = new FrameData
            {
                Writes = new List<WriteEvent>(pendingWrites),
                ControlRegister = controlRegister,
                FilterCutoff = filterCutoff,
                FilterResRoute = filterResRoute,
                FilterModeVol = filterModeVol,
                FilterResRouteChanged = pendingWrites.Any(w => w.Register == 0x17),
            };

            cyclesTarget += ClockFrequency / FrameRate;
            int cyclesThisFrame = (int)cyclesTarget - cyclesClocked;
            int sampleEvery = Math.Max(1, cyclesThisFrame / SamplesPerFrame);

            var osc = new List<int>(SamplesPerFrame + 1);
            var env = new List<int>(SamplesPerFrame + 1);
            var outp = new List<int>(SamplesPerFrame + 1);

            for (int c = 0; c < cyclesThisFrame; c++)
            {
                sid.Clock();
                cyclesClocked++;
                if (c % sampleEvery == 0)
                {
                    osc.Add(voice.Wave.Output());
                    env.Add(voice.Envelope.Output());
                    outp.Add(sid.Output());
                }
            }

            frameData.OscSamples = osc.ToArray();
            frameData.EnvelopeSamples = env.ToArray();
            frameData.OutSamples = outp.ToArray();
            frameData.EnvelopeValue = voice.Envelope.Output();
            frameData.EnvelopeState = voice.Envelope.State;
            frameData.GateOn = (controlRegister & 0x01) != 0;
            frameData.TestBit = (controlRegister & 0x08) != 0;

            result.Frames.Add(frameData);
        }

        result.TriggerFrame = triggerFrame;
        result.MasterVolume = 0x0f;
        result.InstrumentLabel = Label(instrument);
        result.NoteLabel = note.ToString();
        result.Song = song;
        return result;
    }

    /// <summary>How long an audition of a trace plays: every traced frame plus a release tail.
    /// It does not have to beat the song's loop point to a deadline - BuildSingleNoteSong pads
    /// the pattern with LoopGuardFrames of holds so the note cannot retrigger anywhere near here,
    /// which is what stops Trace from playing the instrument twice.</summary>
    public static int AuditionMilliseconds(int frameCount) => Math.Max(50, (int)((frameCount + 12) * 1000.0 / FrameRate));

    /// <summary>One note on channel 1, held via HoldRows for the whole capture window so it
    /// never re-triggers or runs off the end of the pattern (which would advance the order and
    /// loop back to a second trigger). Channels 2/3 get their own silent hold-only pattern -
    /// SongSequencer always primes all three channels' order lists in its constructor, so they
    /// cannot be left empty.</summary>
    private static Song BuildSingleNoteSong(Instrument instrument, NoteName note, int frameCount)
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(instrument);

        int rows = frameCount + 8 + LoopGuardFrames;

        var lead = new Pattern { Number = 1 };
        lead.Rows.Add(new NoteRow(note, instrument.Number));
        for (int i = 0; i < rows; i++)
        {
            lead.Rows.Add(new HoldRow());
        }
        song.Patterns.Add(lead);

        var silent = new Pattern { Number = 2 };
        for (int i = 0; i < rows; i++)
        {
            silent.Rows.Add(new HoldRow());
        }
        song.Patterns.Add(silent);

        song.ChannelOrders[0].Tokens.Add(new PatternRefToken(1));
        song.ChannelOrders[1].Tokens.Add(new PatternRefToken(2));
        song.ChannelOrders[2].Tokens.Add(new PatternRefToken(2));

        return song;
    }
}
