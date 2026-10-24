using MagSid.Core;
using MagSid.Core.Model;
using MagSid.Playback;
using MagSid.Sid;

namespace MagSid.Editor.App;

/// <summary>
/// Converts a .wav into a modulator instrument plus a small library of short carrier instruments
/// that approximate it using ring modulation - not PCM sample playback, which neither the SID
/// nor this player's frame-rate architecture can do. The technique is the long-established one
/// behind spoken-word intros in GoatTracker tunes: a "modulator" voice whose frequency tracks
/// the source audio's detected pitch, ring-modulating a "carrier" voice whose formant (which
/// resonant frequency band its filter is sweeping toward) tracks the source's own dominant
/// resonance.
///
/// The shape of the encoding was established by decoding real GoatTracker instrument tables
/// rather than guessed at: instruments doing this are short (3-6 step) wavetable/filter
/// programs, and the pattern retriggers a fresh note - usually switching instrument - every
/// couple of rows, never running one program continuously for a whole clip. So this encoder
/// segments the clip (see Segmentize) into short retriggered spans and picks, per span,
/// whichever of a handful of formant-shaped carrier instruments (see BuildFormantInstrument)
/// is the closest match - a small reusable library, the same way such tunes reuse a handful of
/// instruments across many notes - rather than generating one giant continuous per-frame
/// program. GoatTracker also needs a tempo-alternation trick to fake frame resolution out of
/// its tick-rate automation between those retriggers; this player doesn't, since
/// Wavetable/Arpeggio/Filter already step every video frame regardless of song Speed (see
/// SongSequencer.Frame's own comment).
///
/// This is a DSP/perceptual encoding problem, not a mechanical conversion - it will still need
/// listening-based iteration (Segment ms and Cutoff min/max above all trade off differently per
/// source). The UI is built around an audition loop (adjust, listen, repeat) rather than a
/// single-shot "convert" button, the same way the filter-timing question earlier needed real
/// listening, not just a register-write trace, to settle.
/// </summary>
public static class DigiEncoder
{
    public const int FrameRate = 50; // matches Voice_Automation/Filter_Play - one frame, always
    public const int MaxFrames = 500; // 10s cap, keeps analysis and the generated program sane

    /// <summary>Hold rows padded onto the audition preview song after its content. Its channel
    /// orders are each a single pattern, which loops - Audition has no stop timer of its own (the
    /// user presses Stop, or the form closes), so without this the note retriggers indefinitely
    /// once the analyzed clip finishes playing. Matches SignalPathTrace's LoopGuardFrames, same
    /// underlying issue: see project_magsid_editor_timing / project_magsid_channel_positions.</summary>
    private const int LoopGuardFrames = 250;

    // SID ring-mod/sync wiring is fixed in hardware (see SidChip's constructor:
    // voice1<-voice3, voice2<-voice1, voice3<-voice2): the carrier's ring bit needs its
    // *modulator* to be specifically the voice that feeds it, not just any other voice.
    // Channel 2 modulated by channel 1 is the pairing the real tunes doing this trick use.
    public const int ModulatorChannel = 0; // channel 1
    public const int CarrierChannel = 1;   // channel 2

    private static readonly (NoteName Note, double Hz)[] NoteFrequencies = BuildNoteFrequencies();

    /// <summary>The Carrier waveform choices, in the order <see cref="BuildInstruments"/>'s
    /// forcedIndex counts them.</summary>
    public static readonly string[] CarrierWaveforms =
    [
        "Auto (search all)",
        "Pulse only ($41)",
        "Triangle+Pulse only ($51)",
        "Ring+Triangle only ($15)",
        "Ring+Triangle+Pulse only ($55)",
        "Ring+Sync+Triangle+Pulse only ($57)",
    ];

    /// <summary>The lowest and highest note frequency in the table - the pitch graph's range.</summary>
    public static double LowestNoteHz => NoteFrequencies[0].Hz;
    public static double HighestNoteHz => NoteFrequencies[^1].Hz;

    // ---------------------------------------------------------------- analysis

    /// <summary>Pitch, voicing, loudness and formant for every 20ms frame of the clip, up to
    /// <see cref="MaxFrames"/>. Null for an empty clip.</summary>
    /// <param name="threshold">How sure pitch detection must be, 0-1, for a frame to be voiced.</param>
    public static AnalysisResult? Analyze(float[] rawSamples, int sampleRate, float threshold)
    {
        if (rawSamples.Length == 0)
        {
            return null;
        }

        int samplesPerFrame = sampleRate / FrameRate;
        int frameCount = Math.Min(rawSamples.Length / samplesPerFrame, MaxFrames);

        var amplitude = new float[frameCount];
        var pitchHz = new float[frameCount];
        var voiced = new bool[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, rawSamples.Length - offset);

            double sumSq = 0;
            for (int i = 0; i < count; i++)
            {
                sumSq += rawSamples[offset + i] * (double)rawSamples[offset + i];
            }
            amplitude[f] = (float)Math.Sqrt(sumSq / Math.Max(1, count));

            float hz = DetectPitchHz(rawSamples, offset, count, sampleRate, out float confidence);
            pitchHz[f] = hz;
            voiced[f] = hz > 0 && confidence >= threshold;
        }

        SmoothPitch(pitchHz, voiced);

        var formantHz = new float[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, rawSamples.Length - offset);
            formantHz[f] = DetectFormantHz(rawSamples, offset, count, sampleRate);
        }

        // Median-of-3 for the same reason SmoothPitch runs on the pitch track: a resonance
        // estimate that jumps a whole band between adjacent 20ms frames reads as a warble, not a
        // vowel - real formants glide, they don't teleport.
        MedianSmooth3(formantHz);

        return new AnalysisResult
        {
            Amplitude = amplitude,
            PitchHz = pitchHz,
            Voiced = voiced,
            FormantHz = formantHz,
            FrameCount = frameCount,
        };
    }

    /// <summary>Median-of-3 pass over the voiced frames only - autocorrelation pitch trackers are
    /// prone to single-frame octave jumps (locking onto a harmonic for one window), which read as
    /// jarring pitch skips rather than a smooth track. Unvoiced frames are left alone; there is no
    /// meaningful pitch to smooth there.</summary>
    private static void SmoothPitch(float[] pitchHz, bool[] voiced)
    {
        var original = (float[])pitchHz.Clone();
        for (int f = 1; f < pitchHz.Length - 1; f++)
        {
            if (!voiced[f] || !voiced[f - 1] || !voiced[f + 1])
            {
                continue;
            }
            float a = original[f - 1], b = original[f], c = original[f + 1];
            pitchHz[f] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c)); // median of 3
        }
    }

    /// <summary>Normalized autocorrelation pitch estimate over one analysis frame. Searches lags
    /// corresponding to a plausible speech/singing range (70-400Hz); the correlation strength at
    /// the winning lag doubles as the voiced/unvoiced confidence the caller thresholds against.
    /// No existing pitch-tracking utility in this codebase to reuse - this is self-contained.</summary>
    private static float DetectPitchHz(float[] samples, int offset, int count, int sampleRate, out float confidence)
    {
        confidence = 0;
        if (count < 8)
        {
            return 0;
        }

        int minLag = Math.Max(1, sampleRate / 400);
        int maxLag = Math.Min(count - 1, sampleRate / 70);
        if (maxLag <= minLag)
        {
            return 0;
        }

        double energy = 0;
        for (int i = 0; i < count; i++)
        {
            energy += samples[offset + i] * (double)samples[offset + i];
        }
        if (energy < 1e-6)
        {
            return 0;
        }

        int bestLag = -1;
        double bestCorr = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double corr = 0;
            int n = count - lag;
            for (int i = 0; i < n; i++)
            {
                corr += samples[offset + i] * samples[offset + i + lag];
            }
            corr /= n;
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        if (bestLag < 0)
        {
            return 0;
        }

        confidence = (float)(bestCorr / (energy / count));
        return sampleRate / (float)bestLag;
    }

    /// <summary>Log-spaced Goertzel target frequencies covering the range vowel formants
    /// actually live in - not a full spectrum, just enough bins to find which one dominates.</summary>
    private static readonly double[] FormantBinsHz = BuildLogSpacedBins(250.0, 3200.0, 24);

    private static double[] BuildLogSpacedBins(double lo, double hi, int count)
    {
        var bins = new double[count];
        double logLo = Math.Log(lo), logHi = Math.Log(hi);
        for (int i = 0; i < count; i++)
        {
            bins[i] = Math.Exp(logLo + (logHi - logLo) * i / (count - 1));
        }
        return bins;
    }

    /// <summary>Goertzel magnitude at one target frequency - a single-bin DFT, far cheaper than a
    /// full FFT when only a couple dozen frequencies actually matter.</summary>
    private static double GoertzelMagnitude(float[] samples, int offset, int count, double targetHz, int sampleRate)
    {
        double k = Math.Round(count * targetHz / sampleRate);
        double w = 2.0 * Math.PI * k / count;
        double cosine = Math.Cos(w);
        double coeff = 2.0 * cosine;
        double q0, q1 = 0, q2 = 0;
        for (int i = 0; i < count; i++)
        {
            q0 = coeff * q1 - q2 + samples[offset + i];
            q2 = q1;
            q1 = q0;
        }
        double real = q1 - q2 * cosine;
        double imag = q2 * Math.Sin(w);
        return Math.Sqrt(real * real + imag * imag);
    }

    /// <summary>The dominant resonance in one frame: whichever <see cref="FormantBinsHz"/> bin
    /// carries the most energy. This is what a source-filter model calls a formant - the
    /// frequency band a vowel's own vocal-tract shape happens to be resonating - and it is the
    /// signal <see cref="CutoffFor"/> needs, not loudness (see that method's own comment for why
    /// the previous amplitude-driven cutoff could never sound like a vowel).</summary>
    private static float DetectFormantHz(float[] samples, int offset, int count, int sampleRate)
    {
        if (count < 8)
        {
            return 0f;
        }
        double best = -1;
        double bestHz = FormantBinsHz[0];
        foreach (double hz in FormantBinsHz)
        {
            double mag = GoertzelMagnitude(samples, offset, count, hz, sampleRate);
            if (mag > best)
            {
                best = mag;
                bestHz = hz;
            }
        }
        return (float)bestHz;
    }

    /// <summary>Same median-of-3 idea as <see cref="SmoothPitch"/>, without the voiced-gating -
    /// used on formant estimates, which are meaningful on every frame, voiced or not.</summary>
    private static void MedianSmooth3(float[] values)
    {
        var original = (float[])values.Clone();
        for (int f = 1; f < values.Length - 1; f++)
        {
            float a = original[f - 1], b = original[f], c = original[f + 1];
            values[f] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }
    }

    /// <summary>The line under the settings: how long the clip is and how much of it is voiced.</summary>
    public static string Summary(AnalysisResult? analysis) => analysis is null
        ? "No .wav loaded."
        : $"{analysis.FrameCount} frames ({analysis.FrameCount / (double)FrameRate:0.00}s), " +
          $"{analysis.Voiced.Count(v => v)} voiced / {analysis.Voiced.Count(v => !v)} unvoiced";

    // ---------------------------------------------------------------- note lookup

    private static (NoteName, double)[] BuildNoteFrequencies()
    {
        const double palClock = 985248.0;
        var list = new List<(NoteName, double)>();
        foreach (var note in NoteName.AllNotes)
        {
            var (lo, hi) = NotePalTable.GetFreq(note);
            int reg = lo | (hi << 8);
            list.Add((note, reg * palClock / 16777216.0));
        }
        return [.. list];
    }

    private static NoteName NearestNote(double hz)
    {
        NoteName best = NoteFrequencies[0].Note;
        double bestDiff = double.MaxValue;
        foreach (var (note, noteHz) in NoteFrequencies)
        {
            double diff = Math.Abs(Math.Log(noteHz / hz));
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = note;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- encoding
    //
    // Rebuilt after the first version - one carrier instrument holding a single wavetable/filter
    // program running continuously for the whole clip - turned out to sound like nothing anyone
    // would call speech. The reason isn't a tuning problem: the real SID ring-modulated speech
    // tricks don't run one continuous program either. Their instrument tables (read directly out
    // of real GoatTracker song files) are each only 3-6 steps long, and the *pattern* retriggers a fresh
    // note - and usually a different instrument - every couple of rows. The melody is doing the
    // work a single long automation program can't: each retrigger is a fresh attack transient
    // (a consonant-like onset) landing on a short, distinct formant-shaped instrument, the same
    // way a real voice re-articulates for every syllable. So this version segments the clip into
    // short spans, builds a small reusable library of short carrier instruments - the same shape
    // as those 3-6 step originals - and writes one NoteRow per segment referencing whichever
    // instrument's formant is the closest match, instead of one giant per-frame table.

    /// <summary>One retriggered span of the clip: a fresh carrier note, using whichever library
    /// instrument's formant is nearest this span's own median.</summary>
    /// <summary><paramref name="Amplitude"/> is already normalized 0..1 against the clip's own
    /// loudest frame, so the search can compare it directly against a rendered candidate's own
    /// RMS without needing the whole clip's data in scope again.</summary>
    public readonly record struct Segment(
        int StartFrame, int FrameCount, bool Voiced, double PitchHz, double FormantHz, double Amplitude);

    /// <summary>Splits the analysis into spans that each become one retrigger: a new span starts
    /// whenever voicing flips (a genuine acoustic event worth a fresh attack) or the configured
    /// segment length is reached (so a long steady vowel still gets re-articulated periodically,
    /// matching how a real voice - and the patterns of tunes doing this - never holds one note
    /// for seconds on end).</summary>
    private static List<Segment> Segmentize(AnalysisResult a, int maxSegmentFrames)
    {
        float maxAmp = a.Amplitude.Length > 0 ? a.Amplitude.Max() : 1f;
        if (maxAmp <= 0f)
        {
            maxAmp = 1f;
        }

        var segments = new List<Segment>();
        int start = 0;
        while (start < a.FrameCount)
        {
            bool voiced = a.Voiced[start];
            int end = start + 1;
            while (end < a.FrameCount && a.Voiced[end] == voiced && end - start < maxSegmentFrames)
            {
                end++;
            }

            var pitches = new List<double>();
            var formants = new List<double>();
            var amplitudes = new List<double>();
            for (int f = start; f < end; f++)
            {
                if (a.PitchHz[f] > 0) pitches.Add(a.PitchHz[f]);
                if (a.FormantHz[f] > 0) formants.Add(a.FormantHz[f]);
                amplitudes.Add(a.Amplitude[f] / maxAmp);
            }
            pitches.Sort();
            formants.Sort();
            amplitudes.Sort();
            double medianPitch = pitches.Count > 0 ? pitches[pitches.Count / 2] : 220.0;
            double medianFormant = formants.Count > 0 ? formants[formants.Count / 2] : FormantBinsHz[FormantBinsHz.Length / 2];
            double medianAmplitude = amplitudes.Count > 0 ? amplitudes[amplitudes.Count / 2] : 0.0;

            segments.Add(new Segment(start, end - start, voiced, medianPitch, medianFormant, medianAmplitude));
            start = end;
        }
        return segments;
    }

    /// <summary>0 (lowest formant bin) .. 1 (highest) on a log scale, matching how pitch itself
    /// is perceived - the same reason <see cref="NearestNote"/> compares logs, not raw Hz.</summary>
    public static double NormalizedFormant(double hz)
    {
        double lo = FormantBinsHz[0], hi = FormantBinsHz[^1];
        double clamped = Math.Clamp(hz, lo, hi);
        return (Math.Log(clamped) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo));
    }

    /// <summary>One short, single-waveform carrier candidate - a plain SID control byte held for
    /// the whole segment (the shape several instruments in the decoded reference tunes use: one
    /// waveform plus a filter/portamento program, no wavetable ramp at all) - optionally with a
    /// two-step BANDPASS filter program sweeping from
    /// <paramref name="cutoffMinV"/> to <paramref name="cutoffTarget"/> and holding there.
    ///
    /// BANDPASS with resonance pushed toward maximum, not the LOWPASS/low-resonance the first
    /// version of this tool used: a lowpass just gets duller as its cutoff falls, which has no
    /// vowel identity of its own - a resonant bandpass carves out a genuine narrow peak, which is
    /// what a vocal tract's own formant resonance actually is.</summary>
    private static Instrument BuildCandidate(
        string name, byte waveform, byte cutoffMinV, byte cutoffTarget, byte resonanceNibble,
        byte sustainNibble, bool withFilter)
    {
        var instrument = new Instrument
        {
            Name = name,
            Waveform = waveform,
            AttackDecay = 0x02, // fast attack, short decay - each retrigger is its own "hit"
            // Attack always rises to full volume regardless of what follows - it's the sustain
            // level here that actually decides how loud the note settles to and stays, which is
            // the search's only real lever on loudness (see ScoreCandidate's amplitude term).
            // Release fixed at a moderate rate; only sustain varies.
            SustainRelease = (byte)((sustainNibble << 4) | 0x8),
        };
        instrument.Wavetable.AddRange([waveform]);
        if (withFilter)
        {
            byte resRoute = (byte)((resonanceNibble << 4) | (1 << CarrierChannel));
            instrument.Filter.Add(FilterStep.Setup(0x2F, resRoute, cutoffMinV)); // $2F: BANDPASS, full volume
            instrument.Filter.Add(FilterStep.Sweep(cutoffTarget));
        }
        return instrument;
    }

    /// <summary>
    /// The candidate pool the brute-force search below picks from - built once per encode, then
    /// rendered and scored per segment rather than assumed correct.
    ///
    /// The three waveform bytes below are not a guess - they are the actual ring-modulated bytes
    /// found by decoding real GoatTracker instrument WAVE tables (against a gplay.c-verified
    /// reading of the format): $57 and $55 (both RINGMOD+TRIANGLE+PULSE, differing only in
    /// whether SYNC also rides along) dominate, $15 (RINGMOD+TRIANGLE, no PULSE) is rarer.
    /// $17 and $37 - RINGMOD+TRIANGLE+SYNC and RINGMOD+TRIANGLE+SYNC+SAWTOOTH, both missing the
    /// PULSE bit - never occur there at all and were dropped after the search kept leaning on
    /// them and clearly not matching what the real tunes are built from. All three carry
    /// TRIANGLE+RINGMOD, since ring modulation on real SID hardware only ever modifies the
    /// triangle generator's output (confirmed by reading WaveformGenerator's own port of reSID's
    /// wave.cc) - SYNC/PULSE riding along just add harmonic color.
    /// Cutoff steps span the Cutoff min/max sliders at four fixed positions crossed with two
    /// resonance levels, giving each voiced waveform its own small spread of candidate formant
    /// targets to be scored against, instead of guessing one target per waveform up front.
    /// </summary>
    private static (List<Instrument> Voiced, List<Instrument> Unvoiced) BuildCandidatePool(byte cutoffMinV, byte cutoffMaxV)
    {
        // $41 (plain PULSE) and $51 (TRIANGLE+PULSE) carry no RINGMOD bit at all - and per the
        // same decoded reference data, $41 is by a wide margin the single most-used waveform
        // byte in those files. Ring modulation there is closer to an occasional
        // accent than the default carrier texture, which the first version of this search got
        // backwards by only ever offering RINGMOD candidates - there was no way for it to pick a
        // plain tone even when one would have scored better. IsRingModulated below is what lets
        // these two share a candidate list with the RINGMOD ones despite needing a different
        // carrier note (see RenderSegment/BuildCarrierRows: only a RINGMOD candidate's own pitch
        // is irrelevant to what's heard).
        byte[] voicedWaveforms = [0x41, 0x51, 0x15, 0x55, 0x57];
        byte[] resonances = [0x8, 0xF];
        // Sustain level - quiet/mid/loud - is the search's lever on loudness (see BuildCandidate's
        // own comment): without it every candidate settles to the same volume regardless of how
        // loud or quiet the source actually was at that moment, which is why the first version of
        // this search rendered at a nearly constant level throughout instead of tracking the
        // source's own dynamics at all.
        byte[] sustainLevels = [0x2, 0x9, 0xF];
        const int cutoffSteps = 4;

        var voiced = new List<Instrument>();
        foreach (byte wave in voicedWaveforms)
        {
            for (int c = 0; c < cutoffSteps; c++)
            {
                double norm = (c + 0.5) / cutoffSteps;
                byte target = (byte)Math.Clamp(
                    cutoffMinV + (int)Math.Round(norm * (cutoffMaxV - cutoffMinV)), cutoffMinV, cutoffMaxV);
                foreach (byte res in resonances)
                {
                    foreach (byte sustain in sustainLevels)
                    {
                        voiced.Add(BuildCandidate(
                            $"Cand V ${wave:x2} cut${target:x2} res{res:x1} sus{sustain:x1}",
                            wave, cutoffMinV, target, res, sustain, withFilter: true));
                    }
                }
            }
        }

        // Unvoiced (fricatives, plosive bursts) has no fundamental for a filter to resonate
        // against, so these skip the filter entirely - leaving whichever voiced instrument
        // played most recently in control of the shared filter, matching Filter_Play's own
        // single-filter design.
        byte[] unvoicedWaveforms = [0x81, 0x91, 0x21];
        var unvoiced = new List<Instrument>();
        foreach (byte wave in unvoicedWaveforms)
        {
            foreach (byte sustain in sustainLevels)
            {
                unvoiced.Add(BuildCandidate(
                    $"Cand U ${wave:x2} sus{sustain:x1}", wave, 0, 0, 0, sustain, withFilter: false));
            }
        }

        return (voiced, unvoiced);
    }

    /// <summary>Bit 2 - true for every candidate this pool builds where the carrier's own pitch
    /// is irrelevant to what gets heard (the audible pitch comes from ring-modulating against the
    /// modulator instead); false for a plain tone, where the carrier has to be triggered on the
    /// segment's own pitch like any other voice.</summary>
    private static bool IsRingModulated(byte waveform) => (waveform & 0x04) != 0;

    /// <summary>The note a carrier candidate should actually be triggered on for this segment:
    /// a fixed high note for a RINGMOD candidate, whose own pitch never reaches the ear, or the
    /// segment's own tracked pitch for a plain tone, which has to carry it directly like any
    /// other voice.</summary>
    public static NoteName CarrierNoteFor(Instrument carrier, Segment segment) =>
        IsRingModulated(carrier.Waveform)
            ? NoteName.AllNotes[^1]
            : NearestNote(segment.PitchHz > 0 ? segment.PitchHz : 220.0);

    /// <summary>
    /// Renders <paramref name="carrier"/> ring-modulated by <paramref name="modulator"/> for
    /// exactly this segment's own frames, through the same reSID port (<see
    /// cref="SongPlaybackEngine"/>) Audition uses - not a formula, the actual chip - and returns
    /// the resulting PCM as floats, skipping the trigger frame's own attack transient so the
    /// score reflects the settled sound.
    /// </summary>
    private static float[] RenderSegment(Instrument carrier, Segment segment, AnalysisResult a)
    {
        var modInstr = new Instrument
        {
            Number = 1,
            Waveform = 0x11,
            AttackDecay = 0x00,
            SustainRelease = 0xF0,
            NoHardRestart = true,
            ArpeggioEnding = ArpeggioEnding.Hold,
        };
        for (int f = segment.StartFrame; f < segment.StartFrame + segment.FrameCount; f++)
        {
            float hz = a.PitchHz[f];
            modInstr.Arpeggio.Add(NearestNote(hz > 0 ? hz : 220.0));
        }

        carrier.Number = 2;
        var song = new Song { Speed = 0 };
        song.Instruments.Add(modInstr);
        song.Instruments.Add(carrier);

        var modPattern = new Pattern { Number = 1 };
        modPattern.Rows.Add(new NoteRow(modInstr.Arpeggio[0], 1));
        AddHolds(modPattern, segment.FrameCount + 3);
        song.Patterns.Add(modPattern);

        var carPattern = new Pattern { Number = 2 };
        carPattern.Rows.Add(new NoteRow(CarrierNoteFor(carrier, segment), 2));
        AddHolds(carPattern, segment.FrameCount + 3);
        song.Patterns.Add(carPattern);

        var silent = new Pattern { Number = 3 };
        AddHolds(silent, segment.FrameCount + 3);
        song.Patterns.Add(silent);

        var patternForChannel = new int[3];
        patternForChannel[ModulatorChannel] = 1;
        patternForChannel[CarrierChannel] = 2;
        patternForChannel[3 - ModulatorChannel - CarrierChannel] = 3;
        for (int ch = 0; ch < 3; ch++)
        {
            song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(patternForChannel[ch]));
        }

        var engine = new SongPlaybackEngine(song, ChipModel.Mos8580);
        int samplesNeeded = (int)((segment.FrameCount + 2) / (double)FrameRate * SongPlaybackEngine.SampleRate);
        var buffer = new byte[samplesNeeded * 2];
        engine.Read(buffer, 0, buffer.Length);

        var samples = new float[samplesNeeded];
        for (int i = 0; i < samplesNeeded; i++)
        {
            samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }
        return samples;
    }

    /// <summary>
    /// How well one rendered candidate matches this segment's own source formant - silence
    /// (a candidate whose filter/waveform combination happens to null the signal out) always
    /// loses outright, otherwise it's how close the candidate's own dominant resonance (measured
    /// the same way <see cref="DetectFormantHz"/> measures the source) lands to the segment's
    /// target, on the same log scale <see cref="NormalizedFormant"/> uses for pitch perception.
    /// </summary>
    private static float ScoreCandidate(Instrument candidate, Segment segment, AnalysisResult a)
    {
        var rendered = RenderSegment(candidate, segment, a);

        int skip = Math.Min(rendered.Length - 1, SongPlaybackEngine.SampleRate / FrameRate);
        int count = Math.Max(1, rendered.Length - skip);

        double sumSq = 0;
        for (int i = skip; i < rendered.Length; i++)
        {
            sumSq += rendered[i] * (double)rendered[i];
        }
        double rms = Math.Sqrt(sumSq / count);
        if (rms < 0.002)
        {
            return 0f; // effectively silent - never a usable candidate
        }

        float renderedFormant = DetectFormantHz(rendered, skip, count, SongPlaybackEngine.SampleRate);
        double formantMatch = 1.0 - Math.Abs(NormalizedFormant(renderedFormant) - NormalizedFormant(segment.FormantHz));

        // The SID's own ADSR sustain plateau scales its output amplitude linearly with the
        // sustain nibble (0..15) - see BuildCandidate's own comment - so the candidate's
        // SustainRelease byte already says how loud it settles to, with no need to calibrate a
        // rendered-RMS reference level to compare against. This is the piece that was missing
        // entirely before: every candidate used to share one fixed envelope, so the search had
        // no way to make a quiet source frame render any quieter than a loud one.
        double achievedLoudness = (candidate.SustainRelease >> 4) / 15.0;
        double amplitudeMatch = 1.0 - Math.Abs(achievedLoudness - segment.Amplitude);

        double match = 0.6 * formantMatch + 0.4 * amplitudeMatch;
        return (float)Math.Clamp(match, 0.0, 1.0);
    }

    private static Instrument PickBestCandidate(
        IReadOnlyList<Instrument> candidates, Segment segment, AnalysisResult a)
    {
        // Scoring a candidate means rendering it through its own SidChip, and those renders are
        // completely independent of each other: every bit of state a render touches is created
        // inside ScoreCandidate/RenderSegment, and the only things shared across them in
        // MagSid.Sid are static *readonly* lookup tables. So the expensive part parallelises
        // with no locking at all - and since the whole search is dozens of these per segment,
        // this is where the wall-clock time of a search actually goes.
        //
        // Only the scoring is parallel; the winner is still picked by a plain sequential scan
        // afterwards. That is deliberate rather than an oversight: picking inside the parallel
        // loop would make which candidate wins a tie depend on thread timing, and this search
        // has to give the same answer every run for the cache in BuildInstrumentsAsync (and for
        // the user comparing two settings) to mean anything.
        //
        // Candidate objects are mutated during a render (RenderSegment sets Number), so it is
        // the candidate loop that is parallel and the segment loop that stays serial - that way
        // no two threads ever hold the same Instrument.
        var scores = new float[candidates.Count];
        Parallel.For(0, candidates.Count, i => scores[i] = ScoreCandidate(candidates[i], segment, a));

        Instrument best = candidates[0];
        float bestScore = -1f;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (scores[i] > bestScore)
            {
                bestScore = scores[i];
                best = candidates[i];
            }
        }
        return best;
    }

    public sealed class EncodeResult
    {
        public required Instrument Modulator;
        public required List<Instrument> CarrierLibrary;
        public required List<(Segment Segment, Instrument Carrier)> SegmentAssignments;
    }

    /// <summary>
    /// Builds the modulator, then brute-forces the carrier: renders every candidate in the pool
    /// against every segment through the real chip and keeps whichever wins, instead of assuming
    /// one fixed instrument per formant bucket is close enough. CarrierLibrary ends up holding
    /// only the candidates that actually won at least one segment - typically a handful out of
    /// the ~36 tried, the same "small reused library" shape the real tunes' instruments have.
    /// </summary>
    /// <remarks>Static, touching only its own parameters and the (immutable-in-practice)
    /// AnalysisResult, so it is safe to run on a background thread; every setting it needs
    /// arrives already read off the window.</remarks>
    /// <param name="forcedIndex">0 searches every carrier waveform; 1-5 keep one family - the
    /// order of <see cref="CarrierWaveforms"/>.</param>
    public static EncodeResult BuildInstruments(
        AnalysisResult a, byte cutoffMinV, byte cutoffMaxV, int segmentMs, int forcedIndex,
        IProgress<int> progress, CancellationToken ct)
    {
        var modulator = new Instrument
        {
            Name = "Digi Modulator",
            Waveform = 0x11, // plain triangle - only its frequency matters to the ring mod
            AttackDecay = 0x00,
            SustainRelease = 0xF0,
            NoHardRestart = true,
            ArpeggioEnding = ArpeggioEnding.Hold,
        };
        foreach (float hz in a.PitchHz)
        {
            modulator.Arpeggio.Add(NearestNote(hz > 0 ? hz : 220.0));
        }

        var (voicedPool, unvoicedPool) = BuildCandidatePool(cutoffMinV, cutoffMaxV);

        if (forcedIndex is >= 1 and <= 5) // 0 = Auto (search), 1..5 = force a waveform family
        {
            byte[] forced = [0x41, 0x51, 0x15, 0x55, 0x57];
            voicedPool = voicedPool.Where(i => i.Waveform == forced[forcedIndex - 1]).ToList();
        }

        int maxSegmentFrames = Math.Max(2, (int)Math.Round(segmentMs / 1000.0 * FrameRate));
        var segments = Segmentize(a, maxSegmentFrames);

        var used = new List<Instrument>();
        var assignments = new List<(Segment, Instrument)>();
        for (int i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var segment = segments[i];
            var pool = segment.Voiced ? voicedPool : unvoicedPool;
            var winner = PickBestCandidate(pool, segment, a);
            if (!used.Contains(winner))
            {
                used.Add(winner);
            }
            assignments.Add((segment, winner));

            progress.Report((i + 1) * 100 / segments.Count);
        }

        return new EncodeResult
        {
            Modulator = modulator,
            CarrierLibrary = used,
            SegmentAssignments = assignments,
        };
    }

    public static void NumberForPreview(EncodeResult result)
    {
        result.Modulator.Number = 1;
        for (int i = 0; i < result.CarrierLibrary.Count; i++)
        {
            result.CarrierLibrary[i].Number = 2 + i;
        }
    }

    /// <summary>The whole clip through the real reSID port - a throwaway engine, so scrubbing the
    /// graph never needs the sound device to be playing - so the graph can show what the chip
    /// will actually output next to the source waveform.</summary>
    public static float[] RenderClip(EncodeResult result, int frameCount)
    {
        NumberForPreview(result);
        var song = BuildPreviewSong(result, frameCount);

        var engine = new SongPlaybackEngine(song, ChipModel.Mos8580);
        int samplesNeeded = (int)((frameCount + 8) / (double)FrameRate * SongPlaybackEngine.SampleRate);
        var buffer = new byte[samplesNeeded * 2];
        engine.Read(buffer, 0, buffer.Length);

        var samples = new float[samplesNeeded];
        for (int i = 0; i < samplesNeeded; i++)
        {
            samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }
        return samples;
    }

    /// <summary>A throwaway Song for auditioning: the modulator and the whole carrier library
    /// triggered together on the two channels the SID's own ring-mod wiring requires, the third
    /// channel silent. Never touches the user's actual song.</summary>
    public static Song BuildPreviewSong(EncodeResult result, int frameCount)
    {
        var song = new Song { Speed = 0 };
        song.Instruments.Add(result.Modulator);
        song.Instruments.AddRange(result.CarrierLibrary);

        var modPattern = new Pattern { Number = 1 };
        modPattern.Rows.Add(new NoteRow(
            result.Modulator.Arpeggio.Count > 0 ? result.Modulator.Arpeggio[0] : NoteName.Parse("C-3"),
            result.Modulator.Number));
        AddHolds(modPattern, frameCount + 8 + LoopGuardFrames);
        song.Patterns.Add(modPattern);

        var carPattern = new Pattern { Number = 2 };
        BuildCarrierRows(carPattern, result);
        song.Patterns.Add(carPattern);

        var silent = new Pattern { Number = 3 };
        AddHolds(silent, frameCount + 8 + LoopGuardFrames);
        song.Patterns.Add(silent);

        var patternForChannel = new int[3];
        patternForChannel[ModulatorChannel] = 1;
        patternForChannel[CarrierChannel] = 2;
        patternForChannel[3 - ModulatorChannel - CarrierChannel] = 3;

        for (int ch = 0; ch < 3; ch++)
        {
            song.ChannelOrders[ch].Tokens.Add(new PatternRefToken(patternForChannel[ch]));
        }

        return song;
    }

    /// <summary>One NoteRow per segment - a fresh retrigger on whichever library instrument's
    /// formant matches that segment - followed by enough HoldRows to cover the rest of its
    /// frames. The note itself is <see cref="CarrierNoteFor"/>: a fixed high pitch for a RINGMOD
    /// instrument (whose own note is irrelevant - the audible pitch comes from the modulator),
    /// or the segment's own tracked pitch for a plain tone, which has to carry it directly.</summary>
    private static void BuildCarrierRows(Pattern carPattern, EncodeResult result)
    {
        foreach (var (segment, carrier) in result.SegmentAssignments)
        {
            carPattern.Rows.Add(new NoteRow(CarrierNoteFor(carrier, segment), carrier.Number));
            for (int i = 1; i < segment.FrameCount; i++)
            {
                carPattern.Rows.Add(new HoldRow());
            }
        }
        AddHolds(carPattern, 8 + LoopGuardFrames); // trailing pad, matching the modulator/silent patterns' own +8
    }

    private static void AddHolds(Pattern pattern, int count)
    {
        for (int i = 0; i < count; i++)
        {
            pattern.Rows.Add(new HoldRow());
        }
    }

    /// <summary>What Add to Song put where.</summary>
    public sealed record Added(IReadOnlyList<int> Instruments, int LibraryCount, int ModulatorPattern, int CarrierPattern, int Segments)
    {
        public string Message =>
            $"Added instruments {string.Join(", ", Instruments)} (modulator + a {LibraryCount}-instrument " +
            $"carrier library) and pattern {ModulatorPattern} (modulator) and {CarrierPattern} (carrier, " +
            $"{Segments} segments).\n\n" +
            $"Place pattern {ModulatorPattern} on channel {ModulatorChannel + 1} and pattern " +
            $"{CarrierPattern} on channel {CarrierChannel + 1}, at the same song position, so they trigger " +
            "together - the SID's ring modulation only works between those two specific adjacent voices.";
    }

    /// <summary>The modulator, the carrier library and their two patterns, added to the song
    /// after what it has. The caller tells the editor the song's structure changed.</summary>
    public static Added AddToSong(Song song, EncodeResult result, int frameCount)
    {
        // The window's search may hand back a cached result also in use for Audition/the
        // reSID-output graph, both of which renumber and reuse those same Instrument objects on
        // every call - fine for a throwaway preview, not fine here, where the objects actually
        // get added to the user's own song permanently. Cloning first means a later Audition
        // renumbering those shared instances back to 1/2/3 can never reach into (and silently
        // corrupt) what just got saved.
        int nextInstr = song.Instruments.Count == 0 ? 1 : song.Instruments.Max(i => i.Number) + 1;
        var clonedModulator = CloneInstrument(result.Modulator, nextInstr);
        var cloneMap = new Dictionary<Instrument, Instrument>();
        var clonedLibrary = new List<Instrument>();
        for (int i = 0; i < result.CarrierLibrary.Count; i++)
        {
            var clone = CloneInstrument(result.CarrierLibrary[i], nextInstr + 1 + i);
            cloneMap[result.CarrierLibrary[i]] = clone;
            clonedLibrary.Add(clone);
        }
        var clonedResult = new EncodeResult
        {
            Modulator = clonedModulator,
            CarrierLibrary = clonedLibrary,
            SegmentAssignments = result.SegmentAssignments
                .Select(sa => (sa.Segment, Carrier: cloneMap[sa.Carrier]))
                .ToList(),
        };

        song.Instruments.Add(clonedModulator);
        song.Instruments.AddRange(clonedLibrary);

        int nextPat = song.Patterns.Count == 0 ? 1 : song.Patterns.Max(p => p.Number) + 1;

        var modPattern = new Pattern { Number = nextPat };
        modPattern.Rows.Add(new NoteRow(
            clonedModulator.Arpeggio.Count > 0 ? clonedModulator.Arpeggio[0] : NoteName.Parse("C-3"),
            clonedModulator.Number));
        AddHolds(modPattern, frameCount + 8);
        song.Patterns.Add(modPattern);

        var carPattern = new Pattern { Number = nextPat + 1 };
        BuildCarrierRows(carPattern, clonedResult);
        song.Patterns.Add(carPattern);

        return new Added(
            [.. new[] { clonedModulator }.Concat(clonedLibrary).Select(i => i.Number)],
            clonedLibrary.Count, modPattern.Number, carPattern.Number, clonedResult.SegmentAssignments.Count);
    }

    private static Instrument CloneInstrument(Instrument source, int number)
    {
        var clone = new Instrument
        {
            Number = number,
            Name = source.Name,
            Waveform = source.Waveform,
            AttackDecay = source.AttackDecay,
            SustainRelease = source.SustainRelease,
            ArpeggioEnding = source.ArpeggioEnding,
            ArpeggioLoopPoint = source.ArpeggioLoopPoint,
            PulseWidth = source.PulseWidth,
            PulseSweep = source.PulseSweep,
            PulseContinuous = source.PulseContinuous,
            NoHardRestart = source.NoHardRestart,
            SoftHardRestart = source.SoftHardRestart,
        };
        clone.Wavetable.AddRange(source.Wavetable);
        clone.Arpeggio.AddRange(source.Arpeggio);
        clone.Filter.AddRange(source.Filter);
        return clone;
    }

    public sealed class AnalysisResult
    {
        /// <summary>RMS per frame - not drawn any more (see the reSID-output band that replaced
        /// the old amplitude graph), but still what <see cref="Segment.Amplitude"/> and the
        /// candidate search's sustain-level dimension are driven from.</summary>
        public float[] Amplitude = [];

        public float[] PitchHz = [];
        public bool[] Voiced = [];

        /// <summary>The dominant resonance frequency in each frame (see <see cref="DetectFormantHz"/>),
        /// in Hz - what the carrier's filter cutoff tracks. This is the piece that was missing
        /// before: loudness alone has no vowel identity, but sweeping a highly resonant bandpass
        /// filter's peak across the frequency where the source's own energy is concentrated is
        /// exactly what turns a voice's supply of harmonics into the specific vowel shape a
        /// listener recognizes (the same source-filter idea behind every formant vocoder, not
        /// something invented for this tool).</summary>
        public float[] FormantHz = [];

        public int FrameCount;
    }
}
