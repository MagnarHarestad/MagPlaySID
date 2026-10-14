using System.Collections.Concurrent;
using System.Threading;
using MagSid.Core.Model;
using MagSid.Sid;
using NAudio.Wave;

namespace MagSid.Playback;

/// <summary>
/// Weaves the 50Hz tick clock (one <see cref="SongSequencer"/> frame - a "row", every
/// Song.Speed ticks) together with the PAL SID clock (985248 Hz) and 44.1kHz audio output,
/// exactly as reSID's own sid.cc documents for a driver loop: "while (delta_t) { bufindex +=
/// sid.clock(delta_t, buf+bufindex, ...); ... }" - except here the register writes the
/// sequencer makes are interleaved at precise frame boundaries rather than all issued up
/// front, so a note always lands on the correct SID cycle relative to the audio it produces.
/// </summary>
public sealed class SongPlaybackEngine : IWaveProvider
{
    private const double ClockFrequency = 985248.0; // PAL C64
    private const double FrameRate = 50.0; // PAL frames/second

    /// <summary>Public so the UI can convert its own animation math into this same sample
    /// clock - see <see cref="SamplesProduced"/>.</summary>
    public const int SampleRate = 44100;

    private readonly SidChip _sid = new();
    private SongSequencer _sequencer;
    private readonly Song _song;

    /// <summary>A Cycle range: when the song reaches <c>EndFrame</c> it goes on from <c>Start</c>
    /// (null: the top) instead - a fresh sequencer at that position, the sound card untouched, so
    /// the loop has no gap. Swapped whole, so the audio thread never sees half of one.</summary>
    private sealed record CycleRange(SongSequencer.SequencerPosition? Start, int EndFrame);

    private volatile CycleRange? _cycle;

    /// <summary>
    /// Loops playback between two places: on reaching <paramref name="endFrame"/> (frames from the
    /// top), it carries on from <paramref name="start"/>. Null clears it. Safe while playing: the
    /// audio thread picks it up at its next frame. Started past the end, it never comes round.
    /// </summary>
    public void SetCycle(SongSequencer.SequencerPosition? start, int endFrame) =>
        _cycle = new CycleRange(start, endFrame);

    public void ClearCycle() => _cycle = null;

    // The metronome, for a count-in: a short beep mixed in after the SID, so it takes no voice
    // from the song. Clicks fall on song frames [_clickFrom, _clickTo), one a beat, the first of
    // each bar higher. _countInLeft frames of clicks alone come before the song starts at all.
    private int _clickFrom = -1;
    private int _clickTo = -1;
    private int _clickFramesPerBeat = 1;
    private int _countInLeft;
    private int _countInDone;
    private int _clickSamplesLeft;
    private double _clickPhase;
    private double _clickStep;

    private const int ClickSamples = SampleRate / 40;   // 25ms

    /// <summary>Clicks over the song's own frames <paramref name="fromFrame"/> up to
    /// <paramref name="toFrame"/> - a pre-roll counted in.</summary>
    public void SetClick(int fromFrame, int toFrame, int framesPerBeat)
    {
        _clickFrom = fromFrame;
        _clickTo = toFrame;
        _clickFramesPerBeat = Math.Max(1, framesPerBeat);
    }

    /// <summary>A count-in before the song starts: <paramref name="frames"/> frames of clicks
    /// alone, then the song from where it was given.</summary>
    public void SetCountIn(int frames, int framesPerBeat)
    {
        _countInLeft = Math.Max(0, frames);
        _countInDone = 0;
        _clickFramesPerBeat = Math.Max(1, framesPerBeat);
    }

    private void StartClick(bool accent)
    {
        _clickSamplesLeft = ClickSamples;
        _clickPhase = 0;
        _clickStep = 2 * Math.PI * (accent ? 1760.0 : 1320.0) / SampleRate;
    }

    private void MixClick(short[] samples, int from, int count)
    {
        for (int i = from; i < from + count && _clickSamplesLeft > 0; i++, _clickSamplesLeft--)
        {
            double envelope = _clickSamplesLeft / (double)ClickSamples;
            int value = samples[i] + (int)(Math.Sin(_clickPhase) * 9000 * envelope);
            samples[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            _clickPhase += _clickStep;
        }
    }
    private readonly double _cyclesPerFrame = ClockFrequency / FrameRate;

    // SongSequencer.RowTriggered fires from Frame(), which this class's Read() calls on
    // whatever thread NAudio drives audio callbacks from - never the UI thread. The UI polls
    // TryDequeueRowEvent from a WinForms Timer instead of subscribing directly. Each event
    // carries the SamplesProduced count *at the instant the row triggered*, not just which row -
    // see SamplesProduced's own doc comment for why that number, not a wall-clock timestamp, is
    // what the UI needs to animate smoothly.
    private readonly ConcurrentQueue<(int Channel, int PatternNumber, int RowIndex, int OrderEntrySequence, PatternRow Row, long SampleIndex)> _rowEvents = new();

    // One captured sequencer instant per frame, with the sample index that frame starts at.
    // Kept so a resume can be taken at the position the listener had actually *heard*: the
    // sequencer itself always sits at the produced frontier, up to a whole output buffer (300ms
    // by WaveOutEvent's default) ahead of the speakers. Written on the audio thread by Read and
    // read on the UI thread when playback stops, hence the lock; bounded to a few seconds, since
    // nothing ever asks for more than one buffer's worth of history.
    /// <summary>
    /// Where each voice's programs stood on each rendered frame, tagged with the sample it was
    /// rendered at. Read back through <see cref="PlayheadAt"/> against the device's *play*
    /// position, because Read() runs as far ahead as the output buffer is deep - WaveOutEvent
    /// defaults to around 300ms - and a highlight driven straight off the sequencer would lead
    /// the sound by that much, and disagree with the tracker grid, which already aligns this way.
    ///
    /// A preallocated ring of structs: this is written from the audio thread, which must not
    /// allocate or block.
    /// </summary>
    private readonly PlayheadFrame[] _playheads = new PlayheadFrame[PositionHistoryFrames];
    private int _playheadCount;
    private int _playheadNext;
    private readonly object _playheadLock = new();

    private struct PlayheadFrame
    {
        public long Samples;
        public int Instrument0, Wave0, Arp0;
        public int Instrument1, Wave1, Arp1;
        public int Instrument2, Wave2, Arp2;
        public int FilterInstrument, FilterStep;
        public int FilterFrames, FilterCutoff;

        // Each voice's envelope as the frame starts: whose it is, its stage and its level.
        public int EnvInstrument0, EnvState0, EnvLevel0;
        public int EnvInstrument1, EnvState1, EnvLevel1;
        public int EnvInstrument2, EnvState2, EnvLevel2;
    }

    /// <summary>
    /// Where this instrument's filter program was at that point in the audio: the frame of it,
    /// counted from its first step as 0, and the cutoff actually written then. False when the
    /// filter belonged to another instrument, or this one had claimed it but not yet stepped.
    /// </summary>
    public bool FilterAt(long sample, int instrumentNumber, out int frame, out int cutoff)
    {
        frame = -1;
        cutoff = -1;

        lock (_playheadLock)
        {
            int newest = -1;
            for (int i = 0; i < _playheadCount; i++)
            {
                int index = (_playheadNext - _playheadCount + i + _playheads.Length) % _playheads.Length;
                if (_playheads[index].Samples > sample)
                {
                    break;
                }

                newest = index;
            }

            if (newest < 0)
            {
                return false;
            }

            ref var at = ref _playheads[newest];
            if (at.FilterInstrument != instrumentNumber || at.FilterFrames < 1 || at.FilterCutoff < 0)
            {
                return false;
            }

            frame = at.FilterFrames - 1;
            cutoff = at.FilterCutoff;
            return true;
        }
    }

    /// <summary>
    /// Where this instrument's envelope was at that point in the audio: the stage (0 attack,
    /// 1 decay/sustain, 2 release) and the level, 0-255. False when no voice playing it was
    /// making any sound. With the instrument on more than one voice, the loudest one.
    /// </summary>
    public bool EnvelopeAt(long sample, int instrumentNumber, out int stage, out int level)
    {
        stage = -1;
        level = 0;

        lock (_playheadLock)
        {
            int newest = -1;
            for (int i = 0; i < _playheadCount; i++)
            {
                int index = (_playheadNext - _playheadCount + i + _playheads.Length) % _playheads.Length;
                if (_playheads[index].Samples > sample)
                {
                    break;
                }

                newest = index;
            }

            if (newest < 0)
            {
                return false;
            }

            ref var frame = ref _playheads[newest];
            Consider(frame.EnvInstrument0, frame.EnvState0, frame.EnvLevel0, ref stage, ref level);
            Consider(frame.EnvInstrument1, frame.EnvState1, frame.EnvLevel1, ref stage, ref level);
            Consider(frame.EnvInstrument2, frame.EnvState2, frame.EnvLevel2, ref stage, ref level);
        }

        return level > 0;

        void Consider(int instrument, int voiceStage, int voiceLevel, ref int bestStage, ref int bestLevel)
        {
            if (instrument == instrumentNumber && voiceLevel > bestLevel)
            {
                bestStage = voiceStage;
                bestLevel = voiceLevel;
            }
        }
    }

    /// <summary>The program steps this instrument was on at that point in the audio, or nothing
    /// if it was not sounding then. Steps are -1 when that program had not played yet.</summary>
    public bool PlayheadAt(long sample, int instrumentNumber,
        out int wavetableStep, out int arpeggioStep, out int filterStep)
    {
        wavetableStep = arpeggioStep = filterStep = -1;
        bool found = false;

        lock (_playheadLock)
        {
            for (int i = 0; i < _playheadCount; i++)
            {
                int index = (_playheadNext - _playheadCount + i + _playheads.Length) % _playheads.Length;
                ref var frame = ref _playheads[index];
                if (frame.Samples > sample)
                {
                    break;
                }

                // Only the newest frame at or before the instant asked for describes what is
                // sounding now, so each frame starts from nothing. Carrying a step forward from an
                // older frame left an instrument that had already stopped lit up for as long as its
                // last note stayed in the ring.
                wavetableStep = arpeggioStep = filterStep = -1;
                found = false;

                int w = -1, a = -1;
                if (frame.Instrument0 == instrumentNumber) { w = frame.Wave0; a = frame.Arp0; }
                if (frame.Instrument1 == instrumentNumber) { w = frame.Wave1; a = frame.Arp1; }
                if (frame.Instrument2 == instrumentNumber) { w = frame.Wave2; a = frame.Arp2; }

                if (w >= 0 || a >= 0)
                {
                    wavetableStep = w;
                    arpeggioStep = a;
                    found = true;
                }

                if (frame.FilterInstrument == instrumentNumber && frame.FilterStep >= 0)
                {
                    filterStep = frame.FilterStep;
                    found = true;
                }
            }
        }

        return found;
    }

    private void RecordPlayhead()
    {
        lock (_playheadLock)
        {
            ref var frame = ref _playheads[_playheadNext];
            frame.Samples = _samplesProduced;
            (frame.Instrument0, frame.Wave0, frame.Arp0) = _sequencer.VoicePlayhead(0);
            (frame.Instrument1, frame.Wave1, frame.Arp1) = _sequencer.VoicePlayhead(1);
            (frame.Instrument2, frame.Wave2, frame.Arp2) = _sequencer.VoicePlayhead(2);
            (frame.FilterInstrument, frame.FilterStep) = _sequencer.FilterPlayhead();
            (_, frame.FilterFrames, frame.FilterCutoff) = _sequencer.FilterState();
            (frame.EnvInstrument0, frame.EnvState0, frame.EnvLevel0) = Envelope(0);
            (frame.EnvInstrument1, frame.EnvState1, frame.EnvLevel1) = Envelope(1);
            (frame.EnvInstrument2, frame.EnvState2, frame.EnvLevel2) = Envelope(2);

            _playheadNext = (_playheadNext + 1) % _playheads.Length;
            if (_playheadCount < _playheads.Length)
            {
                _playheadCount++;
            }
        }
    }

    private (int Instrument, int State, int Level) Envelope(int voice)
    {
        var envelope = _sid.Voice(voice).Envelope;
        return (_sequencer.VoiceInstrument(voice), (int)envelope.State, envelope.Output());
    }

    private readonly object _historyLock = new();
    private readonly Queue<(long Samples, SongSequencer.SequencerPosition Position)> _positionHistory = new();
    private const int PositionHistoryFrames = 256;

    private double _totalCyclesClocked;
    private double _nextFrameBoundaryCycles;
    private long _samplesProduced;

    /// <summary>How many audio samples this engine has generated since construction - a
    /// monotonic clock driven by the same Read() calls that produce the actual sound, so it
    /// never drifts from what's really playing. The UI's smooth-scroll animation reads this
    /// fresh on every repaint (60fps) rather than relying on a wall-clock timestamp captured
    /// only when a WinForms poll timer happens to drain the row-trigger queue - at
    /// Song.Speed=0 a row is a single 50Hz tick, 20ms, which is *shorter* than a 40ms poll
    /// interval was, so that timestamp could already be a full row behind before the animation
    /// even started. Sample counts don't have that problem: they're read from a value that keeps
    /// advancing every millisecond regardless of when anyone last looked at it.</summary>
    public long SamplesProduced => Interlocked.Read(ref _samplesProduced);

    public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

    /// <param name="resumeFrom">A position captured from an earlier engine via
    /// <see cref="PositionAt"/>, to carry on from. Null starts at the top of the song.</param>
    public SongPlaybackEngine(
        Song song,
        ChipModel chipModel = ChipModel.Mos6581,
        SongSequencer.SequencerPosition? resumeFrom = null)
    {
        _sid.SetChipModel(chipModel);
        _sid.SetSamplingParameters(ClockFrequency, SamplingMethod.Interpolate, SampleRate);
        _song = song;
        _sequencer = CreateSequencer(resumeFrom);

        // After the sequencer's constructor, since that is what writes the volume register the
        // transient comes from. It only advances the chip's own history - the frame counters
        // below are this class's, so the first tick still lands where it should.
        _sid.SettleStartupTransient(ClockFrequency);

        // 0, not _cyclesPerFrame: Read()'s loop only calls Frame() once cyclesToNextFrame drops
        // below 1, so leaving this at a full frame's worth of cycles meant the first ~20ms of
        // real audio played with every voice still untriggered - row 0's own note only started
        // once that silent frame had already gone out the door. A short percussive first note
        // (a two-row kick, ~80ms total) can lose most of its audible attack to that alone; a
        // long-held one survives it without anyone noticing. Starting here at 0 makes Frame()
        // fire immediately, before Read() has produced a single sample.
        _nextFrameBoundaryCycles = 0;
    }

    private SongSequencer CreateSequencer(SongSequencer.SequencerPosition? from)
    {
        var sequencer = new SongSequencer(_song, _sid.Write, from);
        sequencer.RowTriggered += (channel, patternNumber, rowIndex, orderEntrySequence, row) =>
            _rowEvents.Enqueue((channel, patternNumber, rowIndex, orderEntrySequence, row, _samplesProduced));
        return sequencer;
    }

    /// <summary>Mutes or unmutes one of the three SID voices in this engine's own output - an
    /// editor-side listening aid (see SidChip.SetVoiceMuted's own comment), not something that
    /// changes the song or ever reaches the assembled player. Safe to call while playing: it's a
    /// pure output-mix toggle with no register state involved, so it takes effect on the very
    /// next sample and leaves the voice's own oscillator/envelope running untouched underneath.</summary>
    public void SetVoiceMuted(int voice, bool muted) => _sid.SetVoiceMuted(voice, muted);

    /// <summary>Which steps of that instrument's programs this playback has reached so far -
    /// the editor lights them so an unreachable tail of a wavetable is visible rather than
    /// guessed at. Accumulating, and safe to read from the UI thread while audio runs; see
    /// ProgramCoverage.</summary>
    public ProgramCoverage CoverageFor(int instrumentNumber) => _sequencer.CoverageFor(instrumentNumber);

    /// <summary>Every instrument's coverage, indexed by instrument number. Handed out so the
    /// editor can hold on to it after this engine is gone - what a play-through reached is the
    /// evidence Optimize uses to trim program tails.</summary>
    public IReadOnlyList<ProgramCoverage> AllCoverage => _sequencer.AllCoverage;

    /// <summary>Drains one buffered row-trigger event, if any are pending. Safe to call from
    /// the UI thread on a poll timer.</summary>
    public bool TryDequeueRowEvent(
        out (int Channel, int PatternNumber, int RowIndex, int OrderEntrySequence, PatternRow Row, long SampleIndex) rowEvent) =>
        _rowEvents.TryDequeue(out rowEvent);

    /// <summary>
    /// The sequencer's position as of <paramref name="sample"/> - pass the device's own play
    /// position (see WaveOutEvent.GetPosition) to get where the song was when the listener last
    /// heard it, rather than where generation had run ahead to.
    ///
    /// Null before the first frame has been produced, or once the wanted instant has aged out of
    /// the bounded history.
    /// </summary>
    public SongSequencer.SequencerPosition? PositionAt(long sample) => PositionAt(sample, out _);

    /// <summary>The same, and the sample the returned frame was captured at - so a caller that
    /// wants to know how far into that frame <paramref name="sample"/> is can work it out, rather
    /// than seeing the song stand still for a fiftieth of a second at a time.</summary>
    public SongSequencer.SequencerPosition? PositionAt(long sample, out long capturedAt)
    {
        lock (_historyLock)
        {
            SongSequencer.SequencerPosition? best = null;
            capturedAt = 0;

            foreach (var (samples, position) in _positionHistory)
            {
                if (samples > sample)
                {
                    break;
                }

                best = position;
                capturedAt = samples;
            }

            if (best is not null)
            {
                return best;
            }

            // Asking for an instant older than the history reaches is not an error - the caller
            // gets the oldest frame still held, which is the closest thing to it that exists.
            if (_positionHistory.Count == 0)
            {
                return null;
            }

            var oldest = _positionHistory.Peek();
            capturedAt = oldest.Samples;
            return oldest.Position;
        }
    }

    /// <summary>Audio samples in one player frame - what a sub-frame offset is measured
    /// against.</summary>
    public const double SamplesPerFrame = SampleRate / FrameRate;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesRequested = count / 2;
        var samples = new short[samplesRequested];
        int samplesWritten = 0;

        while (samplesWritten < samplesRequested)
        {
            double cyclesToNextFrame = _nextFrameBoundaryCycles - _totalCyclesClocked;
            if (cyclesToNextFrame < 1.0)
            {
                // A count-in: clicks alone, the song held until it is over.
                if (_countInLeft > 0)
                {
                    if (_countInDone % _clickFramesPerBeat == 0)
                        StartClick(_countInDone % (_clickFramesPerBeat * 4) == 0);
                    _countInDone++;
                    _countInLeft--;
                    _nextFrameBoundaryCycles += _cyclesPerFrame;
                    continue;
                }

                // The end of a Cycle range: on from its start, as if the song had been there.
                if (_cycle is { } cycle && _sequencer.FramesPlayed == cycle.EndFrame
                    && cycle.EndFrame > (cycle.Start?.Frame ?? 0))
                {
                    _sequencer = CreateSequencer(cycle.Start);
                }

                int songFrame = _sequencer.FramesPlayed;
                if (songFrame >= _clickFrom && songFrame < _clickTo && (songFrame - _clickFrom) % _clickFramesPerBeat == 0)
                    StartClick((songFrame - _clickFrom) % (_clickFramesPerBeat * 4) == 0);

                _sequencer.Frame();
                RecordPlayhead();
                _nextFrameBoundaryCycles += _cyclesPerFrame;
                lock (_historyLock)
                {
                    _positionHistory.Enqueue((_samplesProduced, _sequencer.Capture()));
                    while (_positionHistory.Count > PositionHistoryFrames)
                    {
                        _positionHistory.Dequeue();
                    }
                }
                continue;
            }

            int deltaT = (int)cyclesToNextFrame;
            int deltaTRemaining = deltaT;
            int produced = _sid.ClockAndSample(ref deltaTRemaining, samples, samplesWritten, samplesRequested - samplesWritten);

            _totalCyclesClocked += deltaT - deltaTRemaining;
            if (_clickSamplesLeft > 0)
                MixClick(samples, samplesWritten, produced);
            samplesWritten += produced;
            Interlocked.Add(ref _samplesProduced, produced);
        }

        Buffer.BlockCopy(samples, 0, buffer, offset, samplesRequested * 2);
        return samplesRequested * 2;
    }
}
