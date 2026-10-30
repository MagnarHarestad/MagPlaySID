using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Media.TextFormatting;
using MagPlay.C64;
using MagPlaySid.Player;

namespace MagPlaySid;

/// <summary>
/// A keyboard of the SID's eight octaves, C-0 to B-7, playing what the chip plays: each voice's
/// key lights in the voice's colour - the scopes' - as bright as its envelope, so a note's release
/// fades on the key. Read from the chip itself at the sample being heard, so it shows what sounds -
/// an arpeggio, a slide, a drum - in any tune, whatever the transcription makes of it. A 2SID or
/// 3SID tune's other chips light keys too, each voice in the colour of its place on its chip. The
/// keys can be struck, too - and slid across - for the window to do with as it will.
/// </summary>
internal sealed class PianoView : Control
{
    private const int Octaves = 8;
    private const int Keys = Octaves * 12;
    private const int WhiteKeys = Octaves * 7;

    /// <summary>The keys of an octave that are black.</summary>
    private static readonly bool[] Black = [false, true, false, true, false, false, true, false, true, false, true, false];

    /// <summary>Each semitone's white key, or for a black one the white key it sits after.</summary>
    private static readonly int[] WhiteOf = [0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6];

    private static readonly Color Ivory = Color.FromRgb(0xC9, 0xC9, 0xD8);
    private static readonly Color IvoryLow = Color.FromRgb(0xE2, 0xE2, 0xEE);
    private static readonly Color Ebony = Color.FromRgb(0x16, 0x16, 0x22);
    private static readonly Color EbonyEdge = Color.FromRgb(0x30, 0x30, 0x46);

    /// <summary>A struck key's glow: warm, as candlelight on ivory.</summary>
    private static readonly Color Struck = Color.FromRgb(0xFF, 0xDC, 0x8C);

    /// <summary>The white keys' face, made once.</summary>
    private static readonly IBrush IvoryFace = new Avalonia.Media.Immutable.ImmutableLinearGradientBrush(
        [new Avalonia.Media.Immutable.ImmutableGradientStop(0, Ivory), new Avalonia.Media.Immutable.ImmutableGradientStop(1, IvoryLow)],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));

    /// <summary>How brightly each key is lit by each voice, 0-1; and the frame's being worked out,
    /// to draw again only when a key's light has changed.</summary>
    private double[,] _lit = new double[Keys, 3];
    private double[,] _next = new double[Keys, 3];

    /// <summary>The octaves' names, laid out once for the text's size - as TextLayouts, which keep
    /// their layout when drawn.</summary>
    private TextLayout[]? _labels;
    private double _textScale = 1;

    /// <summary>How brightly each struck key glows, 0-1, fading; the timer runs only while one does.</summary>
    private readonly double[] _glow = new double[Keys];
    private readonly DispatcherTimer _glowTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _glowClock = new();
    private int _held = -1;

    /// <summary>A key struck - or slid onto while the button is held: its number, C-0 as 0, and the
    /// middle of its top edge.</summary>
    public event Action<int, Point>? KeyStruck;

    /// <summary>1 for the small text, more for bigger: the octaves' names under the keys.</summary>
    public double TextScale
    {
        get => _textScale;
        set
        {
            _textScale = value;
            foreach (var label in _labels ?? [])
                label.Dispose();
            _labels = null;
            InvalidateVisual();
        }
    }

    public PianoView()
    {
        Height = 64;
        _glowTimer.Tick += (_, _) => Fade();
    }

    /// <summary>A key lit as struck, fading over half a second.</summary>
    public void Glow(int key)
    {
        if (key is < 0 or >= Keys)
            return;
        _glow[key] = 1;
        if (!_glowTimer.IsEnabled)
        {
            _glowClock.Restart();
            _glowTimer.Start();
        }
        InvalidateVisual();
    }

    private void Fade()
    {
        double dt = _glowClock.Elapsed.TotalSeconds;
        _glowClock.Restart();
        bool any = false;
        for (int key = 0; key < Keys; key++)
        {
            _glow[key] = Math.Max(0, _glow[key] - dt / 0.55);
            any |= _glow[key] > 0;
        }
        if (!any)
            _glowTimer.Stop();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || KeyAt(e.GetPosition(this)) is not { } key)
            return;
        _held = key;
        KeyStruck?.Invoke(key, TopOf(key));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_held < 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (KeyAt(e.GetPosition(this)) is { } key && key != _held)
        {
            _held = key;
            KeyStruck?.Invoke(key, TopOf(key));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _held = -1;
    }

    // ------------------------------------------------------------------ where the keys are

    private Rect KeysArea => new Rect(Bounds.Size).Deflate(new Thickness(4, 4, 4, 5));

    private Rect KeyRect(int key)
    {
        var keys = KeysArea;
        double white = keys.Width / WhiteKeys;
        if (!Black[key % 12])
            return new Rect(keys.X + (key / 12 * 7 + WhiteOf[key % 12]) * white, keys.Y, white, keys.Height);
        double blackWidth = white * 0.6;
        double x = keys.X + (key / 12 * 7 + WhiteOf[key % 12] + 1) * white - blackWidth / 2;
        return new Rect(x, keys.Y, blackWidth, keys.Height * 0.62);
    }

    /// <summary>The key under a point: a black one first, as it lies over the white.</summary>
    private int? KeyAt(Point point)
    {
        for (int key = 0; key < Keys; key++)
            if (Black[key % 12] && KeyRect(key).Contains(point))
                return key;
        for (int key = 0; key < Keys; key++)
            if (!Black[key % 12] && KeyRect(key).Contains(point))
                return key;
        return null;
    }

    private Point TopOf(int key)
    {
        var rect = KeyRect(key);
        return new Point(rect.Center.X, rect.Y + 2);
    }

    /// <summary>The keys the voices are on at sample <paramref name="at"/>; a muted voice lights none.</summary>
    public void Update(C64PlaybackEngine engine, long at, IReadOnlyList<bool> muted)
    {
        Array.Clear(_next);
        for (int voice = 0; voice < 3 * engine.Chips; voice++)
        {
            if (muted[voice])
                continue;

            var (frequency, control, envelope) = engine.VoiceAt(voice, at);
            bool sounds = envelope > 0 && (control & 0xF0) != 0 && (control & 0x08) == 0;
            double note = engine.NoteOf(frequency);
            if (!sounds || double.IsNaN(note))
                continue;

            int key = (int)Math.Round(note);
            if (key is >= 0 and < Keys)
                _next[key, voice % 3] = Math.Max(_next[key, voice % 3], Math.Sqrt(envelope / 255.0));
        }

        // Drawn again only when a light has changed - held notes and silence need no new frame.
        bool changed = false;
        for (int key = 0; key < Keys && !changed; key++)
            for (int voice = 0; voice < 3; voice++)
                if (Math.Abs(_next[key, voice] - _lit[key, voice]) > 0.004)
                {
                    changed = true;
                    break;
                }
        if (!changed)
            return;
        (_lit, _next) = (_next, _lit);
        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_lit);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground), bounds);
        context.DrawRectangle(SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder), bounds);

        var keys = KeysArea;
        double white = keys.Width / WhiteKeys;
        var ivory = IvoryFace;
        var seam = SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder);

        for (int key = 0; key < Keys; key++)
        {
            if (Black[key % 12])
                continue;
            var rect = KeyRect(key);
            context.DrawRectangle(ivory, seam, new RoundedRect(rect.Deflate(new Thickness(0.5, 0)), 0, 0, 3, 3));
            Light(context, key, new RoundedRect(rect.Deflate(new Thickness(1, 0, 1, 1)), 0, 0, 3, 3));
        }

        for (int key = 0; key < Keys; key++)
        {
            if (!Black[key % 12])
                continue;
            var rect = KeyRect(key);
            context.DrawRectangle(SidPlayerPalette.Brush(Ebony), SidPlayerPalette.Pen(EbonyEdge), new RoundedRect(rect, 0, 0, 2, 2));
            Light(context, key, new RoundedRect(rect.Deflate(1), 0, 0, 2, 2));
        }

        // C of each octave named under its key, as on the rows: C-0 to C-7.
        _labels ??= Enumerable.Range(0, Octaves)
            .Select(octave => new TextLayout($"C{octave}", Typeface.Default, 9 * _textScale, SidPlayerPalette.Brush(SidPlayerPalette.Subtitle)))
            .ToArray();
        for (int octave = 0; octave < Octaves; octave++)
        {
            var label = _labels[octave];
            label.Draw(context, new Point(keys.X + octave * 7 * white + (white - label.Width) / 2, keys.Bottom - label.Height - 1));
        }
    }

    /// <summary>A key in the colours of the voices on it, each as bright as its envelope.</summary>
    private void Light(DrawingContext context, int key, RoundedRect shape)
    {
        for (int voice = 0; voice < 3; voice++)
        {
            double level = _lit[key, voice];
            if (level <= 0)
                continue;
            var colour = SidPlayerPalette.VoiceColour(voice);
            context.DrawRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(colour, (int)(70 + 185 * level))), null, shape);
        }
        if (_glow[key] > 0)
            context.DrawRectangle(SidPlayerPalette.Brush(SidPlayerPalette.WithAlpha(Struck, (int)(230 * _glow[key]) / 8 * 8)), null, shape);
    }
}
