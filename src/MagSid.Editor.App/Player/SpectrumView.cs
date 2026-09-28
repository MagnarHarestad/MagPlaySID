using MagSid.Playback;
using NAudio.Dsp;

namespace MagSid.Editor.App.Player;

/// <summary>
/// The spectrum: a bar per frequency band across the audible range, with a peak mark above each that
/// falls slowly.
///
/// The bars are the instant, the peaks are the memory - between them the display answers both "what
/// is sounding now" and "what has this tune used", which is what a single falling bar cannot. The
/// peaks only ever fall, so a band lit once stays marked long enough to be seen.
/// </summary>
internal sealed class SpectrumView : Control
{
    private const int Bands = 96;

    /// <summary>The SID's musical content lives well below 11kHz; above that is mostly the noise
    /// waveform and the resampler's own business, and giving it a third of the width would waste
    /// the display on it.</summary>
    private const double LowestHz = 40.0;
    private const double HighestHz = 11000.0;

    /// <summary>Floor of the dB scale. -72 keeps a quiet inner voice visible without the noise
    /// floor lifting every bar off the baseline.</summary>
    private const float FloorDb = -72f;

    /// <summary>Bars rise instantly and fall smoothly. A rising bar that lagged would blunt every
    /// transient, which on a SID is most of the character.</summary>
    private const float FallPerFrame = 0.055f;
    private const float PeakFallPerFrame = 0.006f;

    /// <summary>How much of the panel the mirrored reflection below the baseline takes.</summary>
    private const float ReflectionShare = 0.26f;

    private readonly float[] _levels = new float[Bands];
    private readonly float[] _peaks = new float[Bands];
    private readonly float[] _window = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly float[] _samples = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly Complex[] _fft = new Complex[SidFilePlaybackEngine.ScopeSamples];
    private readonly int[] _bandStart = new int[Bands + 1];

    public SpectrumView()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SidPlayerPalette.Background;

        // Hann, so a tone between bins does not smear across half the display.
        for (int i = 0; i < _window.Length; i++)
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (_window.Length - 1))));

        BuildBandEdges();
    }

    /// <summary>Bands are spaced logarithmically, because pitch is: an octave takes the same width
    /// whether it is the bass or the lead, which is what makes a chord read as a shape.</summary>
    private void BuildBandEdges()
    {
        int bins = _fft.Length / 2;
        double binHz = (double)SidFilePlaybackEngine.SampleRate / _fft.Length;

        for (int band = 0; band <= Bands; band++)
        {
            double hz = LowestHz * Math.Pow(HighestHz / LowestHz, (double)band / Bands);
            _bandStart[band] = Math.Clamp((int)Math.Round(hz / binHz), 1, bins - 1);
        }

        // A band narrower than one bin would read as empty for ever; give it the bin above.
        for (int band = 0; band < Bands; band++)
        {
            if (_bandStart[band + 1] <= _bandStart[band])
                _bandStart[band + 1] = _bandStart[band] + 1;
        }
    }

    /// <summary>Takes the window of mixed output the listener is hearing and advances the display by
    /// one frame.</summary>
    /// <param name="atSample">Where the listener actually is, from the output device - see
    /// SidPlayerForm.</param>
    public void Update(SidFilePlaybackEngine engine, long atSample)
    {
        engine.CaptureMix(_samples, atSample);

        for (int i = 0; i < _fft.Length; i++)
        {
            _fft[i].X = _samples[i] * _window[i];
            _fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(_fft.Length), _fft);

        for (int band = 0; band < Bands; band++)
        {
            double sum = 0;
            int from = _bandStart[band];
            int to = _bandStart[band + 1];

            for (int bin = from; bin < to; bin++)
                sum += (_fft[bin].X * _fft[bin].X) + (_fft[bin].Y * _fft[bin].Y);

            // Mean power in the band, not the sum: a wide high band would otherwise tower over a
            // narrow low one carrying the same energy.
            double power = sum / Math.Max(1, to - from);
            float db = power > 1e-12 ? (float)(10.0 * Math.Log10(power)) : FloorDb;
            float level = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);

            _levels[band] = level > _levels[band] ? level : Math.Max(level, _levels[band] - FallPerFrame);
            _peaks[band] = _levels[band] > _peaks[band]
                ? _levels[band]
                : Math.Max(_levels[band], _peaks[band] - PeakFallPerFrame);
        }

        Invalidate();
    }

    public void Clear()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SidPlayerPalette.Background);

        int width = Math.Max(1, Width);
        int height = Math.Max(1, Height);
        int baseline = (int)(height * (1f - ReflectionShare));

        float slot = width / (float)Bands;
        float barWidth = Math.Max(1.5f, slot * 0.62f);

        for (int band = 0; band < Bands; band++)
        {
            float position = Bands > 1 ? band / (float)(Bands - 1) : 0f;
            var colour = SidPlayerPalette.Sample(position);

            float x = (band * slot) + ((slot - barWidth) / 2f);
            float barHeight = _levels[band] * baseline;

            if (barHeight >= 1f)
            {
                using var brush = new SolidBrush(colour);
                g.FillRectangle(brush, x, baseline - barHeight, barWidth, barHeight);

                // The reflection: short, dim, and fading away from the baseline.
                float reflectionHeight = Math.Min(barHeight * 0.45f, height - baseline - 1);
                if (reflectionHeight >= 1f)
                {
                    using var reflection = new SolidBrush(SidPlayerPalette.WithAlpha(colour, 46));
                    g.FillRectangle(reflection, x, baseline + 1, barWidth, reflectionHeight);
                }
            }

            // Peak: the memory of what this band has reached.
            float peakY = baseline - (_peaks[band] * baseline);
            if (_peaks[band] > 0.01f)
            {
                using var peak = new SolidBrush(SidPlayerPalette.WithAlpha(colour, 150));
                g.FillRectangle(peak, x, Math.Max(0f, peakY - 1.5f), barWidth, 1.5f);
            }
        }

        using var floorPen = new Pen(Color.FromArgb(0x1E, 0x1E, 0x2C));
        g.DrawLine(floorPen, 0, baseline, width, baseline);
    }
}
