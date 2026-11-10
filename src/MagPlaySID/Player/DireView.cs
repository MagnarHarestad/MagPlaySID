using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MagPlay.C64;
using Avalonia.Rendering;

namespace MagPlaySid.Player;

/// <summary>
/// The music as a dance: the Dire Guys - one stocky in dungarees with a cigar, one tall in a striped
/// shirt and a red hat - dancing on top of a great turning mirror ball in a dark club. Every note the
/// tune starts moves them: the drums and the bass dip them on the beat and are the blue guy's claps,
/// fist pumps, steps and twists; the melody is the red guy's - a point to the sky, a wave, a kick, a
/// spin when it leaps. The spotlights and the ball's glitter take the voices' colours. It moves by
/// the tune's own time, so it stands still when the tune is paused. Painted like the Last Ninja.
/// </summary>
internal sealed class DireView : Control, ICustomHitTest
{
    /// <summary>Under the pointer anywhere in its bounds: tested so, not against each of the
    /// lines and shapes it last drew - every move of the mouse tested them all.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private readonly DireWorld _world = new();
    private readonly NinjaFrame[] _frames = [new(), new(), new()];
    private readonly NinjaPainter _painter = new();
    private int _nextFrame;

    private readonly int[] _control = new int[9];
    private readonly int[] _frequency = new int[9];
    private double _melodyNote = -1;

    public DireView() => ClipToBounds = true;

    /// <summary>The dance brought to the tune's sample <paramref name="at"/>: every note started
    /// since the last update, a move or a beat.</summary>
    public void Update(ITunePlayback engine, long at)
    {
        int voices = Math.Min(9, engine.Voices);
        Span<(int Voice, int Envelope)> started = stackalloc (int, int)[9];
        int count = 0;
        int low = -1, high = -1;
        for (int v = 0; v < voices; v++)
        {
            var (frequency, control, envelope) = engine.VoiceAt(v, at);
            bool gate = (control & 1) != 0;
            bool noise = (control & 0x80) != 0;
            if (gate && ((_control[v] & 1) == 0 || (!noise && Math.Abs(Note(frequency) - Note(_frequency[v])) >= 0.5)))
                started[count++] = (v, envelope);
            _control[v] = control;
            _frequency[v] = frequency;
            if (gate && !noise && frequency > 0)
            {
                if (low < 0 || frequency < _frequency[low])
                    low = v;
                if (high < 0 || frequency > _frequency[high])
                    high = v;
            }
        }

        foreach (var (voice, envelope) in started[..count])
        {
            var colour = SidPlayerPalette.VoiceColour(voice % 3);
            uint argb = 0xFF000000u | ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B;
            _world.Strike(BeatOf(voice, low, high), voice, argb, Math.Clamp(0.4f + (envelope / 255f), 0.4f, 1.2f));
        }
        _world.Advance(at / (double)C64PlaybackEngine.SampleRate);
        InvalidateVisual();
    }

    /// <summary>What a note is to the dance: a drum's a kick or a snare, the lowest voice the bass,
    /// the highest the melody - leaping up or down when it leaps a fifth or more - the rest the middle.</summary>
    private DireWorld.Beat BeatOf(int voice, int low, int high)
    {
        if ((_control[voice] & 0x80) != 0)
            return _frequency[voice] < 0x1800 ? DireWorld.Beat.Kick : DireWorld.Beat.Snare;
        if (voice == low && low != high)
            return DireWorld.Beat.Bass;
        if (voice != high && low != high)
            return DireWorld.Beat.Middle;
        double note = Note(_frequency[voice]);
        double leap = _melodyNote < 0 ? 0 : note - _melodyNote;
        _melodyNote = note;
        return leap >= 7 ? DireWorld.Beat.Leap : leap <= -7 ? DireWorld.Beat.Fall : DireWorld.Beat.Melody;
    }

    private static double Note(int frequency) => frequency <= 0 ? 0 : 12 * Math.Log2(frequency / (double)0x0115);

    public void Clear()
    {
        _world.Reset();
        Array.Clear(_control);
        Array.Clear(_frequency);
        _melodyNote = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2)
            return;
        var frame = _frames[_nextFrame];
        _nextFrame = (_nextFrame + 1) % _frames.Length;
        _world.Compose(frame, size);
        frame.Sort();
        context.Custom(new Paint(new Rect(size), frame, _painter));
    }

    private sealed class Paint(Rect bounds, NinjaFrame frame, NinjaPainter painter) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            painter.Paint(lease.SkCanvas, frame);
        }
    }
}
