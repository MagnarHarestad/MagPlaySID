using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MagPlay.C64;
using MagSid.Editor.Avalonia.Player;

namespace MagPlaySid;

/// <summary>
/// A keyboard of the SID's eight octaves, C-0 to B-7, playing what the chip plays: each voice's
/// key lights in the voice's colour - the scopes' - as bright as its envelope, so a note's release
/// fades on the key. Read from the chip itself at the sample being heard, so it shows what sounds -
/// an arpeggio, a slide, a drum - in any tune, whatever the transcription makes of it. A 2SID or
/// 3SID tune's other chips light keys too, each voice in the colour of its place on its chip.
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

    /// <summary>How brightly each key is lit by each voice, 0-1.</summary>
    private readonly double[,] _lit = new double[Keys, 3];

    public PianoView()
    {
        Height = 64;
    }

    /// <summary>The keys the voices are on at sample <paramref name="at"/>; a muted voice lights none.</summary>
    public void Update(C64PlaybackEngine engine, long at, IReadOnlyList<bool> muted)
    {
        Array.Clear(_lit);
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
                _lit[key, voice % 3] = Math.Max(_lit[key, voice % 3], Math.Sqrt(envelope / 255.0));
        }
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
        context.DrawRectangle(new Pen(SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder)), bounds);

        var keys = bounds.Deflate(new Thickness(4, 4, 4, 5));
        double white = keys.Width / WhiteKeys;
        var ivory = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Ivory, 0), new GradientStop(IvoryLow, 1) },
        };
        var seam = new Pen(SidPlayerPalette.Brush(SidPlayerPalette.PanelBorder));

        for (int key = 0; key < Keys; key++)
        {
            if (Black[key % 12])
                continue;
            var rect = new Rect(keys.X + (key / 12 * 7 + WhiteOf[key % 12]) * white, keys.Y, white, keys.Height);
            context.DrawRectangle(ivory, seam, new RoundedRect(rect.Deflate(new Thickness(0.5, 0)), 0, 0, 3, 3));
            Light(context, key, new RoundedRect(rect.Deflate(new Thickness(1, 0, 1, 1)), 0, 0, 3, 3));
        }

        double blackWidth = white * 0.6, blackHeight = keys.Height * 0.62;
        for (int key = 0; key < Keys; key++)
        {
            if (!Black[key % 12])
                continue;
            double x = keys.X + (key / 12 * 7 + WhiteOf[key % 12] + 1) * white - blackWidth / 2;
            var rect = new Rect(x, keys.Y, blackWidth, blackHeight);
            context.DrawRectangle(new SolidColorBrush(Ebony), new Pen(new SolidColorBrush(EbonyEdge)), new RoundedRect(rect, 0, 0, 2, 2));
            Light(context, key, new RoundedRect(rect.Deflate(1), 0, 0, 2, 2));
        }

        // C of each octave named under its key, as on the rows: C-0 to C-7.
        for (int octave = 0; octave < Octaves; octave++)
        {
            var label = new FormattedText($"C{octave}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                          Typeface.Default, 9, SidPlayerPalette.Brush(SidPlayerPalette.Subtitle));
            context.DrawText(label, new Point(keys.X + octave * 7 * white + (white - label.Width) / 2, keys.Bottom - label.Height - 1));
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
            context.DrawRectangle(new SolidColorBrush(SidPlayerPalette.WithAlpha(colour, (int)(70 + 185 * level))), null, shape);
        }
    }
}
