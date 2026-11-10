using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MagSid.Playback;
using NAudio.Dsp;
using Avalonia.Rendering;

namespace MagPlaySid.Player;

/// <summary>
/// The spectrum: a bar per frequency band across the audible range, with a peak mark above each
/// that falls slowly - the instant and the memory of it. A port of the Windows editor's view.
/// </summary>
internal sealed class SpectrumView : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private const int Bands = 96;
    private const double LowestHz = 40.0;
    private const double HighestHz = 11000.0;
    private const float FloorDb = -72f;
    private const float FallPerFrame = 0.055f;
    private const float PeakFallPerFrame = 0.006f;
    private const float ReflectionShare = 0.26f;

    /// <summary>Below this a band counts as silent, however quiet it has lately been.</summary>
    private const float QuietestLoud = 0.15f;

    /// <summary>How fast a band forgets how loud it has been, per frame: about half a minute from full to silent.</summary>
    private const float ForgetPerFrame = 0.0005f;

    private readonly float[] _levels = new float[Bands];
    private readonly float[] _peaks = new float[Bands];
    private readonly float[] _loudest = new float[Bands];
    private readonly float[] _relative = new float[Bands];
    private readonly float[] _window = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly float[] _samples = new float[SidFilePlaybackEngine.ScopeSamples];
    private readonly Complex[] _fft = new Complex[SidFilePlaybackEngine.ScopeSamples];
    private readonly int[] _bandStart = new int[Bands + 1];
    private readonly IBrush[] _bar = new IBrush[Bands];
    private readonly IBrush[] _reflection = new IBrush[Bands];
    private readonly IBrush[] _peak = new IBrush[Bands];
    private readonly Pen _floor = new(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2C)));

    public SpectrumView()
    {
        // Hann, so a tone between bins does not smear across half the display.
        for (int i = 0; i < _window.Length; i++)
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (_window.Length - 1))));

        int bins = _fft.Length / 2;
        double binHz = (double)SidFilePlaybackEngine.SampleRate / _fft.Length;
        for (int band = 0; band <= Bands; band++)
        {
            double hz = LowestHz * Math.Pow(HighestHz / LowestHz, (double)band / Bands);
            _bandStart[band] = Math.Clamp((int)Math.Round(hz / binHz), 1, bins - 1);
        }
        for (int band = 0; band < Bands; band++)
        {
            if (_bandStart[band + 1] <= _bandStart[band])
                _bandStart[band + 1] = _bandStart[band] + 1;

            var colour = SidPlayerPalette.Sample(Bands > 1 ? band / (float)(Bands - 1) : 0f);
            _bar[band] = new SolidColorBrush(colour);
            _reflection[band] = new SolidColorBrush(SidPlayerPalette.WithAlpha(colour, 46));
            _peak[band] = new SolidColorBrush(SidPlayerPalette.WithAlpha(colour, 150));
        }
    }

    /// <summary>Each band's level now, 0-1, from low to high.</summary>
    public IReadOnlyList<float> Levels => _levels;

    /// <summary>Each band's level now as a share of its own recent loudest, from low to high: the
    /// quiet treble moves as much as the bass.</summary>
    public IReadOnlyList<float> Relative => _relative;

    /// <summary>Advances the display by one frame from the mix the listener is hearing.</summary>
    public void Update(IScopeSource engine, long atSample)
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
            int from = _bandStart[band], to = _bandStart[band + 1];
            for (int bin = from; bin < to; bin++)
                sum += (_fft[bin].X * _fft[bin].X) + (_fft[bin].Y * _fft[bin].Y);

            double power = sum / Math.Max(1, to - from);
            float db = power > 1e-12 ? (float)(10.0 * Math.Log10(power)) : FloorDb;
            float level = Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);

            _levels[band] = level > _levels[band] ? level : Math.Max(level, _levels[band] - FallPerFrame);
            _peaks[band] = _levels[band] > _peaks[band] ? _levels[band] : Math.Max(_levels[band], _peaks[band] - PeakFallPerFrame);
            _loudest[band] = Math.Max(Math.Max(_levels[band], QuietestLoud), _loudest[band] - ForgetPerFrame);
            _relative[band] = _levels[band] / _loudest[band];
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
        Array.Clear(_loudest);
        Array.Clear(_relative);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Math.Max(1, Bounds.Width), height = Math.Max(1, Bounds.Height);
        double baseline = height * (1 - ReflectionShare);
        double slot = width / Bands;
        double barWidth = Math.Max(1.5, slot * 0.62);

        for (int band = 0; band < Bands; band++)
        {
            double x = (band * slot) + ((slot - barWidth) / 2);
            double barHeight = _levels[band] * baseline;

            if (barHeight >= 1)
            {
                context.FillRectangle(_bar[band], new Rect(x, baseline - barHeight, barWidth, barHeight));
                double reflectionHeight = Math.Min(barHeight * 0.45, height - baseline - 1);
                if (reflectionHeight >= 1)
                    context.FillRectangle(_reflection[band], new Rect(x, baseline + 1, barWidth, reflectionHeight));
            }

            if (_peaks[band] > 0.01f)
            {
                double peakY = baseline - (_peaks[band] * baseline);
                context.FillRectangle(_peak[band], new Rect(x, Math.Max(0, peakY - 1.5), barWidth, 1.5));
            }
        }

        context.DrawLine(_floor, new Point(0, baseline), new Point(width, baseline));
    }
}
