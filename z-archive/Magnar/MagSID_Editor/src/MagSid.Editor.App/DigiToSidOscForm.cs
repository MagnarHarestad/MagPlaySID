using MagSid.Core.Model;
using MagSid.Playback;
using MagSid.Sid;
using NAudio.Wave;

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
public sealed class DigiToSidOscForm : Form
{
    private const int FrameRate = 50; // matches Voice_Automation/Filter_Play - one frame, always
    private const int MaxFrames = 500; // 10s cap, keeps analysis and the generated program sane

    /// <summary>Hold rows padded onto the audition preview song after its content. Its channel
    /// orders are each a single pattern, which loops - Audition has no stop timer of its own (the
    /// user presses Stop, or the form closes), so without this the note retriggers indefinitely
    /// once the analyzed clip finishes playing. Matches SignalPathForm's LoopGuardFrames, same
    /// underlying issue: see project_magsid_editor_timing / project_magsid_channel_positions.</summary>
    private const int LoopGuardFrames = 250;

    // SID ring-mod/sync wiring is fixed in hardware (see SidChip's constructor:
    // voice1<-voice3, voice2<-voice1, voice3<-voice2): the carrier's ring bit needs its
    // *modulator* to be specifically the voice that feeds it, not just any other voice.
    // Channel 2 modulated by channel 1 is the pairing the real tunes doing this trick use.
    private const int ModulatorChannel = 0; // channel 1
    private const int CarrierChannel = 1;   // channel 2

    private static readonly (NoteName Note, double Hz)[] NoteFrequencies = BuildNoteFrequencies();

    private readonly EditorSession _session;

    private readonly Button _loadButton = new()
    {
        Text = "Load .wav...",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 110,
        Height = 24,
    };

    private readonly Label _fileLabel = new()
    {
        AutoSize = true,
        ForeColor = EditorPalette.Subtitle,
        Margin = new Padding(8, 22, 0, 0),
    };

    private readonly NumericUpDown _voicedThreshold = new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = 30,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly NumericUpDown _cutoffMin = new()
    {
        Minimum = FilterStep.MinCutoff,
        Maximum = FilterStep.MaxCutoff,
        Value = 0x20,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly NumericUpDown _cutoffMax = new()
    {
        Minimum = FilterStep.MinCutoff,
        Maximum = FilterStep.MaxCutoff,
        Value = 0xE0,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    /// <summary>How often the carrier retriggers with a fresh instrument, in milliseconds - the
    /// real GoatTracker speech tricks this tool is modeled on retrigger every couple of rows
    /// (roughly 100-150ms at their tempo), not once per whole clip: each retrigger is what gives
    /// a "syllable" its own attack transient and lets a different formant-shaped instrument take
    /// over. Too short and it buzzes as pure noise; too long and formant changes within one
    /// segment get averaged away - a slider because the right value depends on the source and is
    /// only found by listening.</summary>
    private readonly NumericUpDown _segmentMs = new()
    {
        Minimum = 40,
        Maximum = 400,
        Value = 120,
        Width = 60,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
    };

    private readonly ComboBox _carrierWaveform = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 230,
        BackColor = EditorPalette.FieldBackground,
        ForeColor = EditorPalette.Body,
        FlatStyle = FlatStyle.Flat,
    };

    private readonly Button _reanalyzeButton = new()
    {
        Text = "Re-analyze",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 90,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Button _auditionButton = new()
    {
        Text = "Audition",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 90,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Button _stopButton = new()
    {
        Text = "Stop",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 70,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Button _addToSongButton = new()
    {
        Text = "Add to Song",
        FlatStyle = FlatStyle.Flat,
        BackColor = EditorPalette.ButtonBackground,
        ForeColor = EditorPalette.Body,
        Width = 100,
        Height = 24,
        Margin = new Padding(0, 19, 0, 0),
    };

    private readonly Label _summary = new()
    {
        AutoSize = true,
        ForeColor = EditorPalette.Subtitle,
        Padding = new Padding(0, 8, 0, 0),
    };

    /// <summary>Only visible while a search is actually running (see BuildInstrumentsAsync) -
    /// rendering hundreds to thousands of candidates through the real chip is real work, and this
    /// is what tells the user how far through it the app is, rather than just a spinning
    /// wait cursor with no sense of whether it's stuck.</summary>
    private readonly ProgressBar _searchProgress = new()
    {
        Minimum = 0,
        Maximum = 100,
        Width = 220,
        Height = 16,
        Visible = false,
        Margin = new Padding(12, 10, 0, 0),
    };

    private readonly ZoomCanvas _canvas = new()
    {
        Dock = DockStyle.Fill,
        BackColor = EditorPalette.Background,
    };

    /// <summary>Pixels per analysis frame on the graph - mouse-wheel adjustable (see
    /// ZoomCanvas). Reset to a fit-the-window value each time a new analysis runs.</summary>
    private float _pxPerFrame = 10f;

    private AnalysisResult? _analysis;
    private WaveOutEvent? _auditionOutput;
    private SongPlaybackEngine? _auditionEngine;

    /// <summary>PCM straight out of the same reSID port Audition plays through - see <see
    /// cref="RenderFullClip"/> - kept around purely so the analysis graph can draw what the chip
    /// will actually output, not just the source .wav.</summary>
    private float[] _renderedAudio = [];

    // Polls SongPlaybackEngine.SamplesProduced - a monotonic count driven by the same Read()
    // calls that produce the actual sound (see that property's own doc comment on why this is
    // safer than a wall-clock timestamp) - and turns it into a frame position for the playhead.
    private readonly System.Windows.Forms.Timer _playheadTimer = new() { Interval = 30 };
    private float? _playheadFrame;

    public DigiToSidOscForm(EditorSession session)
    {
        _session = session;

        Text = "Digi to SID-osc";
        StartPosition = FormStartPosition.CenterScreen;
        DarkTheme.ApplyTitleBar(this);
        ClientSize = new Size(1280, 640);
        // The settings row is a non-wrapping FlowLayoutPanel, so anything narrower than its
        // own content simply hides the buttons at the right-hand end - Add to Song first.
        MinimumSize = new Size(1000, 520);
        BackColor = EditorPalette.Background;
        ForeColor = EditorPalette.Body;

        _carrierWaveform.Items.AddRange([
            "Auto (search all)",
            "Pulse only ($41)",
            "Triangle+Pulse only ($51)",
            "Ring+Triangle only ($15)",
            "Ring+Triangle+Pulse only ($55)",
            "Ring+Sync+Triangle+Pulse only ($57)",
        ]);
        _carrierWaveform.SelectedIndex = 0;

        _loadButton.Click += async (_, _) => await LoadWavAsync();
        _reanalyzeButton.Click += async (_, _) => { Analyze(); await RenderFullClipAsync(); _canvas.Invalidate(); };
        _auditionButton.Click += async (_, _) => await AuditionAsync();
        _stopButton.Click += (_, _) => StopAudition();
        _addToSongButton.Click += async (_, _) => await AddToSongAsync();
        _canvas.Paint += (_, e) => DrawAnalysis(e.Graphics);
        _canvas.ZoomAt += OnZoomAt;
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
        FormClosed += (_, _) => StopAudition();

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 0, 0),
            BackColor = EditorPalette.Background,
        };
        row1.Controls.Add(_loadButton);
        row1.Controls.Add(_fileLabel);

        // Height generous on purpose: each Labelled() field stacks a caption above the control -
        // a row sized just to the control's own height clips the caption. Same trap as the old
        // InstrumentEditorForm dialog's GroupBox rows (removed 2026-08-31) and the first cut of
        // SignalPathForm's toolbar.
        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 62,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 6, 0, 0),
            BackColor = EditorPalette.Background,
        };
        row2.Controls.Add(Labelled("Voiced threshold %", _voicedThreshold));
        row2.Controls.Add(Labelled("Cutoff min", _cutoffMin));
        row2.Controls.Add(Labelled("Cutoff max", _cutoffMax));
        row2.Controls.Add(Labelled("Segment ms", _segmentMs));
        row2.Controls.Add(Labelled("Carrier waveform", _carrierWaveform));
        row2.Controls.Add(_reanalyzeButton);
        row2.Controls.Add(new Panel { Width = 16, Height = 1 });
        row2.Controls.Add(_auditionButton);
        row2.Controls.Add(_stopButton);
        row2.Controls.Add(new Panel { Width = 16, Height = 1 });
        row2.Controls.Add(_addToSongButton);

        var summaryRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            BackColor = EditorPalette.Background,
        };
        summaryRow.Controls.Add(_summary);
        summaryRow.Controls.Add(_searchProgress);

        Controls.Add(_canvas);
        Controls.Add(summaryRow);
        Controls.Add(row2);
        Controls.Add(row1);

        UpdateSummary();
    }

    private static Control Labelled(string caption, Control field)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Margin = new Padding(0, 0, 16, 0),
        };
        panel.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = EditorPalette.Subtitle });
        panel.Controls.Add(field);
        return panel;
    }

    // ---------------------------------------------------------------- wav loading + analysis

    private async Task LoadWavAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "WAV files (*.wav)|*.wav" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _fileLabel.Text = Path.GetFileName(dialog.FileName);
        var (samples, sampleRate) = ReadWav(dialog.FileName);
        _rawSamples = samples;
        _sampleRate = sampleRate;
        Analyze();
        await RenderFullClipAsync();
        _canvas.Invalidate();
    }

    private float[] _rawSamples = [];
    private int _sampleRate = 44100;

    private static (float[] Samples, int SampleRate) ReadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        int sampleRate = reader.WaveFormat.SampleRate;

        var all = new List<float>();
        var chunk = new float[sampleRate * Math.Max(1, channels)];
        int read;
        while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                all.Add(chunk[i]);
            }
        }

        if (channels <= 1)
        {
            return (all.ToArray(), sampleRate);
        }

        var mono = new float[all.Count / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                sum += all[i * channels + c];
            }
            mono[i] = (float)(sum / channels);
        }
        return (mono, sampleRate);
    }

    private void Analyze()
    {
        if (_rawSamples.Length == 0)
        {
            _analysis = null;
            UpdateSummary();
            return;
        }

        int samplesPerFrame = _sampleRate / FrameRate;
        int frameCount = Math.Min(_rawSamples.Length / samplesPerFrame, MaxFrames);

        var amplitude = new float[frameCount];
        var pitchHz = new float[frameCount];
        var voiced = new bool[frameCount];
        float threshold = (float)_voicedThreshold.Value / 100f;

        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, _rawSamples.Length - offset);

            double sumSq = 0;
            for (int i = 0; i < count; i++)
            {
                sumSq += _rawSamples[offset + i] * (double)_rawSamples[offset + i];
            }
            amplitude[f] = (float)Math.Sqrt(sumSq / Math.Max(1, count));

            float hz = DetectPitchHz(_rawSamples, offset, count, _sampleRate, out float confidence);
            pitchHz[f] = hz;
            voiced[f] = hz > 0 && confidence >= threshold;
        }

        SmoothPitch(pitchHz, voiced);

        var formantHz = new float[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, _rawSamples.Length - offset);
            formantHz[f] = DetectFormantHz(_rawSamples, offset, count, _sampleRate);
        }

        // Median-of-3 for the same reason SmoothPitch runs on the pitch track: a resonance
        // estimate that jumps a whole band between adjacent 20ms frames reads as a warble, not a
        // vowel - real formants glide, they don't teleport.
        MedianSmooth3(formantHz);

        _analysis = new AnalysisResult
        {
            Amplitude = amplitude,
            PitchHz = pitchHz,
            Voiced = voiced,
            FormantHz = formantHz,
            FrameCount = frameCount,
        };

        // Fit-to-width by default; the user zooms from there with the mouse wheel.
        int visibleWidth = Math.Max(1, _canvas.ClientSize.Width - GraphMargin * 2);
        _pxPerFrame = Math.Clamp(visibleWidth / (float)Math.Max(1, frameCount), MinPxPerFrame, MaxPxPerFrame);
        _canvas.AutoScrollMinSize = new Size(GraphMargin * 2 + (int)(frameCount * _pxPerFrame), 0);
        _canvas.AutoScrollPosition = new Point(0, 0);

        UpdateSummary();
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

    private void UpdateSummary()
    {
        _summary.Text = _analysis is null
            ? "No .wav loaded."
            : $"{_analysis.FrameCount} frames ({_analysis.FrameCount / (double)FrameRate:0.00}s), " +
              $"{_analysis.Voiced.Count(v => v)} voiced / {_analysis.Voiced.Count(v => !v)} unvoiced";
    }

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
    private readonly record struct Segment(
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
    private static double NormalizedFormant(double hz)
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
    private static NoteName CarrierNoteFor(Instrument carrier, Segment segment) =>
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

    private sealed class EncodeResult
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
    /// <summary>Guards against two searches running at once (e.g. Audition clicked again before
    /// the last one finished) and lets Stop cancel an in-flight one instead of only stopping
    /// playback.</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>The result of the last completed search, plus every input it depended on - so a
    /// second call with nothing actually changed (Audition right after Load/Re-analyze already
    /// ran one, for instance) can hand back the same answer instead of rendering every candidate
    /// against the chip all over again. Keyed on the AnalysisResult's own identity rather than a
    /// deep comparison: Analyze() always allocates a fresh one, so a reference match already
    /// means nothing about the source or the segmentation changed.</summary>
    private EncodeResult? _cachedResult;
    private (AnalysisResult Analysis, byte CutoffMin, byte CutoffMax, int SegmentMs, int ForcedIndex)? _cachedKey;

    /// <summary>
    /// Reads everything the search needs off the UI thread (NumericUpDown/ComboBox values can
    /// only be touched there), then - unless <see cref="_cachedResult"/> already answers this
    /// exact question - hands the actual work off to a background thread via Task.Run, so the UI
    /// keeps pumping messages throughout instead of Windows marking the window "not responding"
    /// partway through a longer search. <see cref="BuildInstrumentsCore"/> is where all of that
    /// work actually happens, and it touches nothing but plain data for exactly this reason.
    /// </summary>
    private async Task<EncodeResult> BuildInstrumentsAsync()
    {
        var a = _analysis ?? throw new InvalidOperationException("No analysis to encode.");

        byte cutoffMinV = (byte)_cutoffMin.Value;
        byte cutoffMaxV = (byte)_cutoffMax.Value;
        int segmentMs = (int)_segmentMs.Value;
        int forcedIndex = _carrierWaveform.SelectedIndex;

        var key = (a, cutoffMinV, cutoffMaxV, segmentMs, forcedIndex);
        if (_cachedResult is not null && _cachedKey == key)
        {
            return _cachedResult;
        }

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var progress = new Progress<int>(percent =>
        {
            _summary.Text = "Searching candidates against the SID chip...";
            _searchProgress.Value = percent;
        });

        string previousSummary = _summary.Text;
        UseWaitCursor = true;
        SetSearchControlsEnabled(false);
        _searchProgress.Value = 0;
        _searchProgress.Visible = true;
        try
        {
            var result = await Task.Run(
                () => BuildInstrumentsCore(a, cutoffMinV, cutoffMaxV, segmentMs, forcedIndex, progress, cts.Token),
                cts.Token);
            _cachedResult = result;
            _cachedKey = key;
            return result;
        }
        finally
        {
            UseWaitCursor = false;
            SetSearchControlsEnabled(true);
            _searchProgress.Visible = false;
            _summary.Text = previousSummary;
            if (_searchCts == cts)
            {
                _searchCts = null;
            }
        }
    }

    private void SetSearchControlsEnabled(bool enabled)
    {
        _loadButton.Enabled = enabled;
        _reanalyzeButton.Enabled = enabled;
        _auditionButton.Enabled = enabled;
        _addToSongButton.Enabled = enabled;
    }

    /// <summary>The actual brute-force search - static, touching only its own parameters and the
    /// (immutable-in-practice) AnalysisResult, so it is safe to run on a background thread. Every
    /// UI value it needs arrives already-captured from <see cref="BuildInstrumentsAsync"/>.</summary>
    private static EncodeResult BuildInstrumentsCore(
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

    // ---------------------------------------------------------------- audition

    private async Task AuditionAsync()
    {
        if (_analysis is null)
        {
            return;
        }
        StopAudition();

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search (Re-analyze, another Audition click, ...)
        }
        NumberForPreview(result);

        var song = BuildPreviewSong(result, _analysis.FrameCount);

        _auditionEngine = new SongPlaybackEngine(song, ChipModel.Mos8580);
        _auditionOutput = new WaveOutEvent { DesiredLatency = 150 };
        _auditionOutput.Init(_auditionEngine);
        _auditionOutput.Play();

        _playheadFrame = 0f;
        _playheadTimer.Start();
    }

    private static void NumberForPreview(EncodeResult result)
    {
        result.Modulator.Number = 1;
        for (int i = 0; i < result.CarrierLibrary.Count; i++)
        {
            result.CarrierLibrary[i].Number = 2 + i;
        }
    }

    /// <summary>Renders the whole clip through the real reSID port - not Audition's own live
    /// engine, a throwaway one, so scrubbing the graph never needs the sound device to be
    /// playing - purely so <see cref="DrawAnalysis"/> can show what the chip will actually
    /// output next to the source waveform. Runs the same brute-force search Audition/Add to Song
    /// do, so this is exactly the analysis-quality preview for whatever the current sliders
    /// would produce, not a cheaper approximation of it.</summary>
    private async Task RenderFullClipAsync()
    {
        if (_analysis is null || _analysis.FrameCount == 0)
        {
            _renderedAudio = [];
            return;
        }

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search
        }
        NumberForPreview(result);
        var song = BuildPreviewSong(result, _analysis.FrameCount);

        var engine = new SongPlaybackEngine(song, ChipModel.Mos8580);
        int samplesNeeded = (int)((_analysis.FrameCount + 8) / (double)FrameRate * SongPlaybackEngine.SampleRate);
        var buffer = new byte[samplesNeeded * 2];
        engine.Read(buffer, 0, buffer.Length);

        var samples = new float[samplesNeeded];
        for (int i = 0; i < samplesNeeded; i++)
        {
            samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        }
        _renderedAudio = samples;
    }

    private void StopAudition()
    {
        _searchCts?.Cancel(); // Stop also cancels an in-flight search, not just playback

        _auditionOutput?.Stop();
        _auditionOutput?.Dispose();
        _auditionOutput = null;
        _auditionEngine = null;

        _playheadTimer.Stop();
        _playheadFrame = null;
        _canvas.Invalidate();
    }

    /// <summary>Converts the engine's own sample-accurate elapsed count into a frame position and
    /// nudges the graph to follow it once zoomed in far enough that the playhead would otherwise
    /// scroll off the visible edge.</summary>
    private void UpdatePlayhead()
    {
        if (_auditionEngine is null || _analysis is null)
        {
            return;
        }

        // See AuditionClock.SamplesPlayed for why this reads the device's play position
        // rather than the engine's own produced count when an output device is attached.
        long samples;
        if (_auditionOutput is not null)
        {
            long? played = AuditionClock.SamplesPlayed(_auditionOutput, _auditionEngine);
            if (played is null)
            {
                return;
            }
            samples = played.Value;
        }
        else
        {
            samples = _auditionEngine.SamplesProduced;
        }

        double seconds = samples / (double)SongPlaybackEngine.SampleRate;
        float frame = (float)(seconds * FrameRate);

        if (frame >= _analysis.FrameCount + 8)
        {
            StopAudition();
            return;
        }

        _playheadFrame = frame;

        float playheadX = GraphMargin + frame * _pxPerFrame;
        int scrollX = -_canvas.AutoScrollPosition.X;
        int visibleWidth = _canvas.ClientSize.Width;
        if (playheadX > scrollX + visibleWidth - 40)
        {
            int contentWidth = GraphMargin * 2 + (int)(_analysis.FrameCount * _pxPerFrame);
            int newScroll = (int)(playheadX - visibleWidth + 60);
            newScroll = Math.Max(0, Math.Min(newScroll, Math.Max(0, contentWidth - visibleWidth)));
            _canvas.AutoScrollPosition = new Point(newScroll, 0);
        }

        _canvas.Invalidate();
    }

    /// <summary>A throwaway Song for auditioning: the modulator and the whole carrier library
    /// triggered together on the two channels the SID's own ring-mod wiring requires, the third
    /// channel silent. Never touches the user's actual song.</summary>
    private static Song BuildPreviewSong(EncodeResult result, int frameCount)
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

    // ---------------------------------------------------------------- add to song

    private async Task AddToSongAsync()
    {
        if (_analysis is null)
        {
            return;
        }

        EncodeResult result;
        try
        {
            result = await BuildInstrumentsAsync();
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer search
        }
        var song = _session.Song;

        // BuildInstrumentsAsync may hand back a cached result also in use for Audition/the
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
        AddHolds(modPattern, _analysis.FrameCount + 8);
        song.Patterns.Add(modPattern);

        var carPattern = new Pattern { Number = nextPat + 1 };
        BuildCarrierRows(carPattern, clonedResult);
        song.Patterns.Add(carPattern);

        _session.NotifyStructureChanged();

        var instrumentNumbers = string.Join(", ", new[] { clonedModulator }.Concat(clonedLibrary).Select(i => i.Number));
        MessageBox.Show(this,
            $"Added instruments {instrumentNumbers} (modulator + a {clonedLibrary.Count}-instrument " +
            $"carrier library) and pattern {modPattern.Number} (modulator) and {carPattern.Number} (carrier, " +
            $"{clonedResult.SegmentAssignments.Count} segments).\n\n" +
            $"Place pattern {modPattern.Number} on channel {ModulatorChannel + 1} and pattern " +
            $"{carPattern.Number} on channel {CarrierChannel + 1}, at the same song position, so they trigger " +
            "together - the SID's ring modulation only works between those two specific adjacent voices.",
            "Digi to SID-osc", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        };
        clone.Wavetable.AddRange(source.Wavetable);
        clone.Arpeggio.AddRange(source.Arpeggio);
        clone.Filter.AddRange(source.Filter);
        return clone;
    }

    // ---------------------------------------------------------------- zoom

    private const int GraphMargin = 12;
    private const float MinPxPerFrame = 2f;
    private const float MaxPxPerFrame = 80f;

    /// <summary>Mouse-wheel zoom, keeping the frame under the cursor stationary - so zooming in
    /// on a spot in the middle of a long clip doesn't fling the view somewhere else. ZoomCanvas
    /// hands back the raw wheel event; the frame math belongs here since only this method knows
    /// what a "frame" and GraphMargin mean.</summary>
    private void OnZoomAt(object? sender, MouseEventArgs e)
    {
        if (_analysis is null || _analysis.FrameCount == 0)
        {
            return;
        }

        float oldPx = _pxPerFrame;
        int scrollX = -_canvas.AutoScrollPosition.X;
        float frameUnderMouse = (e.X - GraphMargin + scrollX) / oldPx;

        float factor = e.Delta > 0 ? 1.2f : 1f / 1.2f;
        _pxPerFrame = Math.Clamp(oldPx * factor, MinPxPerFrame, MaxPxPerFrame);

        int contentWidth = GraphMargin * 2 + (int)(_analysis.FrameCount * _pxPerFrame);
        _canvas.AutoScrollMinSize = new Size(contentWidth, 0);

        int newScrollX = (int)(frameUnderMouse * _pxPerFrame - (e.X - GraphMargin));
        newScrollX = Math.Max(0, Math.Min(newScrollX, Math.Max(0, contentWidth - _canvas.ClientSize.Width)));
        _canvas.AutoScrollPosition = new Point(newScrollX, 0);

        _canvas.Invalidate();
    }

    // ---------------------------------------------------------------- drawing

    private void DrawAnalysis(Graphics g)
    {
        g.Clear(EditorPalette.Background);
        if (_analysis is null || _analysis.FrameCount == 0)
        {
            using var brush = new SolidBrush(EditorPalette.Subtitle);
            g.DrawString("Load a .wav to see the source waveform and detected pitch here.",
                new Font("Segoe UI", 9f), brush, 12, 12);
            return;
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var a = _analysis;
        int margin = GraphMargin;
        int height = Math.Max(1, _canvas.ClientSize.Height - margin * 2);
        float px = _pxPerFrame;

        // Four bands stacked top to bottom: the raw waveform first (so it reads as "the source"
        // the other three are derived from), then the rendered reSID output/formant/voiced-pitch.
        int bandHeight = height / 4;
        int waveTop = margin, waveBottom = margin + bandHeight - 8;
        int residTop = margin + bandHeight + 8, residBottom = margin + bandHeight * 2 - 8;
        int brightTop = margin + bandHeight * 2 + 8, brightBottom = margin + bandHeight * 3 - 8;
        int pitchTop = margin + bandHeight * 3 + 8, pitchBottom = margin + height;

        double minHz = NoteFrequencies[0].Hz;
        double maxHz = NoteFrequencies[^1].Hz;

        using var wavePen = new Pen(EditorPalette.Body, 1f);
        using var waveMidPen = new Pen(EditorPalette.Border);
        using var residPen = new Pen(EditorPalette.Title, 1f);
        using var residMidPen = new Pen(EditorPalette.Border);
        using var brightPen = new Pen(Color.FromArgb(0xd8, 0xa8, 0x3a), 1.4f);
        using var voicedBrush = new SolidBrush(Color.FromArgb(0x2f, 0x6b, 0x2f));
        using var unvoicedBrush = new SolidBrush(Color.FromArgb(0x5a, 0x20, 0x20));
        using var pitchPen = new Pen(EditorPalette.Mono, 1.6f);
        using var labelBrush = new SolidBrush(EditorPalette.Subtitle);
        using var labelFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);

        g.DrawString("waveform (source .wav)", labelFont, labelBrush, margin, waveTop);
        g.DrawString("reSID output (rendered, what the chip will actually play)", labelFont, labelBrush, margin, residTop - 16);
        g.DrawString("formant (dominant resonance, drives the carrier filter)", labelFont, labelBrush, margin, brightTop - 16);
        g.DrawString("voiced / pitch", labelFont, labelBrush, margin, pitchTop - 16);

        DrawWaveform(g, wavePen, waveMidPen, margin, waveTop, waveBottom, px);
        DrawRenderedWaveform(g, residPen, residMidPen, margin, residTop, residBottom, px);

        var brightPoints = new List<PointF>();
        for (int f = 0; f < a.FrameCount; f++)
        {
            float x0 = margin + f * px;
            float x1 = x0 + px;

            float formantNorm = (float)NormalizedFormant(a.FormantHz[f]);
            float by = brightBottom - formantNorm * (brightBottom - brightTop - 4);
            brightPoints.Add(new PointF(x0, by));
            brightPoints.Add(new PointF(x1, by));

            g.FillRectangle(a.Voiced[f] ? voicedBrush : unvoicedBrush,
                x0, pitchTop, Math.Max(1, px), pitchBottom - pitchTop);
        }
        if (brightPoints.Count > 1)
        {
            g.DrawLines(brightPen, brightPoints.ToArray());
        }

        var pitchPoints = new List<PointF>();
        for (int f = 0; f < a.FrameCount; f++)
        {
            if (!a.Voiced[f] || a.PitchHz[f] <= 0)
            {
                if (pitchPoints.Count > 1)
                {
                    g.DrawLines(pitchPen, pitchPoints.ToArray());
                }
                pitchPoints.Clear();
                continue;
            }

            float x = margin + (f + 0.5f) * px;
            double norm = Math.Clamp(Math.Log(a.PitchHz[f] / minHz) / Math.Log(maxHz / minHz), 0.0, 1.0);
            float y = (float)(pitchBottom - norm * (pitchBottom - pitchTop));
            pitchPoints.Add(new PointF(x, y));
        }
        if (pitchPoints.Count > 1)
        {
            g.DrawLines(pitchPen, pitchPoints.ToArray());
        }

        if (_playheadFrame is float playhead)
        {
            using var playheadPen = new Pen(Color.FromArgb(0xff, 0xd7, 0x00), 2f);
            float x = margin + playhead * px;
            g.DrawLine(playheadPen, x, margin, x, margin + height);
        }
    }

    /// <summary>Peak-envelope waveform of the raw samples, one min/max pair per analysis frame -
    /// drawn on the same per-frame grid as the three derived rows below it (rather than true
    /// per-sample resolution) so a column here lines up exactly with the column it produced in
    /// amplitude/formant/voiced-pitch, at any zoom level.</summary>
    private void DrawWaveform(Graphics g, Pen wavePen, Pen midPen, int margin, int top, int bottom, float px)
    {
        int mid = (top + bottom) / 2;
        g.DrawLine(midPen, margin, mid, margin + _analysis!.FrameCount * px, mid);

        if (_rawSamples.Length == 0)
        {
            return;
        }

        int samplesPerFrame = Math.Max(1, _sampleRate / FrameRate);
        float halfHeight = (bottom - top) / 2f - 2f;

        DrawWaveformDetail(g, margin, mid, halfHeight, px, samplesPerFrame);

        // One continuous polyline rather than a separate min/max tick per frame: each frame
        // contributes its peak then its trough (in time order), so the envelope traces as one
        // connected zigzag - a proper waveform line, not disconnected per-column strokes.
        var points = new List<PointF>(_analysis.FrameCount * 2);
        for (int f = 0; f < _analysis.FrameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, _rawSamples.Length - offset);
            if (count <= 0)
            {
                break;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float s = _rawSamples[offset + i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            float x = margin + (f + 0.5f) * px;
            float yMax = mid - Math.Clamp(max, -1f, 1f) * halfHeight;
            float yMin = mid - Math.Clamp(min, -1f, 1f) * halfHeight;

            // Alternate which extreme comes first so consecutive frames' lines don't all cross
            // through the same corner - keeps the envelope's outline visually cleaner.
            if (f % 2 == 0)
            {
                points.Add(new PointF(x, yMax));
                points.Add(new PointF(x, yMin));
            }
            else
            {
                points.Add(new PointF(x, yMin));
                points.Add(new PointF(x, yMax));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(wavePen, points.ToArray());
        }
    }

    /// <summary>Same peak-envelope idea as <see cref="DrawWaveform"/>, but over <see
    /// cref="_renderedAudio"/> - real PCM out of <see cref="SongPlaybackEngine"/>/SidChip, not a
    /// prediction of it - so what actually clips, buzzes, or goes silent because a candidate's
    /// filter closed all the way is visible before ever pressing Audition. Always at
    /// SongPlaybackEngine.SampleRate (44.1kHz) regardless of the source .wav's own rate, since
    /// that is the engine's fixed output rate, not something this clip's source file chose.
    /// Nothing is drawn - just the center line - until a search has actually populated the
    /// buffer (see RenderFullClip): Re-analyze and Load .wav both trigger one.</summary>
    private void DrawRenderedWaveform(Graphics g, Pen wavePen, Pen midPen, int margin, int top, int bottom, float px)
    {
        int mid = (top + bottom) / 2;
        g.DrawLine(midPen, margin, mid, margin + _analysis!.FrameCount * px, mid);

        if (_renderedAudio.Length == 0)
        {
            return;
        }

        int samplesPerFrame = SongPlaybackEngine.SampleRate / FrameRate;
        float halfHeight = (bottom - top) / 2f - 2f;

        var points = new List<PointF>(_analysis.FrameCount * 2);
        for (int f = 0; f < _analysis.FrameCount; f++)
        {
            int offset = f * samplesPerFrame;
            int count = Math.Min(samplesPerFrame, _renderedAudio.Length - offset);
            if (count <= 0)
            {
                break;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float s = _renderedAudio[offset + i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            float x = margin + (f + 0.5f) * px;
            float yMax = mid - Math.Clamp(max, -1f, 1f) * halfHeight;
            float yMin = mid - Math.Clamp(min, -1f, 1f) * halfHeight;

            if (f % 2 == 0)
            {
                points.Add(new PointF(x, yMax));
                points.Add(new PointF(x, yMin));
            }
            else
            {
                points.Add(new PointF(x, yMin));
                points.Add(new PointF(x, yMax));
            }
        }

        if (points.Count > 1)
        {
            g.DrawLines(wavePen, points.ToArray());
        }
    }

    /// <summary>The same peak-envelope idea as <see cref="DrawWaveform"/>'s own line, but one
    /// column per screen pixel instead of one per analysis frame, in a dim tone behind it - so
    /// zooming in reveals real waveform detail (individual pitch periods, transients) the
    /// frame-grid line can't show, since that one is deliberately locked to the same 20ms grid
    /// as amplitude/formant/voiced-pitch below it.</summary>
    private void DrawWaveformDetail(Graphics g, int margin, int mid, float halfHeight, float px, int samplesPerFrame)
    {
        using var detailPen = new Pen(Color.FromArgb(70, EditorPalette.Body), 1f);

        double samplesPerPixel = samplesPerFrame / (double)px;
        int contentLeft = margin;
        int contentRight = margin + (int)(_analysis!.FrameCount * px);

        var points = new List<PointF>(Math.Max(0, contentRight - contentLeft) * 2);
        bool firstHalf = true;
        for (int x = contentLeft; x < contentRight; x++)
        {
            double samplePos = (x - margin) / (double)px * samplesPerFrame;
            int startSample = (int)samplePos;
            int endSample = Math.Min(_rawSamples.Length, (int)(samplePos + Math.Max(1, samplesPerPixel)));
            if (startSample >= endSample)
            {
                continue;
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = startSample; i < endSample; i++)
            {
                float s = _rawSamples[i];
                if (s < min) min = s;
                if (s > max) max = s;
            }

            float yMax = mid - Math.Clamp(max, -1f, 1f) * halfHeight;
            float yMin = mid - Math.Clamp(min, -1f, 1f) * halfHeight;

            if (firstHalf)
            {
                points.Add(new PointF(x, yMax));
                points.Add(new PointF(x, yMin));
            }
            else
            {
                points.Add(new PointF(x, yMin));
                points.Add(new PointF(x, yMax));
            }
            firstHalf = !firstHalf;
        }

        if (points.Count > 1)
        {
            g.DrawLines(detailPen, points.ToArray());
        }
    }

    private sealed class AnalysisResult
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

    /// <summary>A Panel whose mouse wheel zooms the analysis graph (via <see cref="ZoomAt"/>)
    /// instead of doing the default AutoScroll vertical scroll, which this graph never needs -
    /// it only ever scrolls horizontally, driven by the zoom math in DigiToSidOscForm itself
    /// (this class knows nothing about frames or margins, just that wheel input happened).</summary>
    private sealed class ZoomCanvas : Panel
    {
        public event MouseEventHandler? ZoomAt;

        public ZoomCanvas()
        {
            AutoScroll = true;

            // Without this, the playhead timer's ~30/sec Invalidate() during Audition visibly
            // flickers - each repaint erases to the background color and redraws in two visible
            // steps instead of one. Standard fix: paint to an off-screen buffer and blit it in
            // one go.
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomAt?.Invoke(this, e);
            // Deliberately not calling base.OnMouseWheel: the default vertical AutoScroll
            // behavior would fight with the zoom, scrolling the (fixed-height) content up and
            // down for no reason.
        }
    }
}
