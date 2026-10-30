using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace MagPlaySid;

/// <summary>
/// Fluffy, magical dust over the whole window: a puff of it from each key struck while no tune
/// plays - soft glowing motes and the odd twinkling star, drifting up and swaying as they fade. It
/// takes no clicks, and its timer runs only while there is dust in the air.
/// </summary>
internal sealed class MagicDust : Control
{
    private const int MostMotes = 700;

    /// <summary>Pastels the dust glows in: gold, rose, lavender, mint, sky, white.</summary>
    private static readonly Color[] Colours =
    [
        Color.FromRgb(0xFF, 0xE3, 0x9A), Color.FromRgb(0xFF, 0xB3, 0xD9), Color.FromRgb(0xC9, 0xB6, 0xFF),
        Color.FromRgb(0xB5, 0xF5, 0xE1), Color.FromRgb(0xA9, 0xD8, 0xFF), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    /// <summary>How finely a mote's fading is drawn: as many brushes as this a colour.</summary>
    private const int Shades = 16;

    // A mote's halo - its colour fading out to nothing - its bright core and a star's rays, made
    // once each for every colour and shade: a mote's fading is a brush to pick, not a layer to push.
    private static readonly IBrush[,] Halos = Made((c, o) => new ImmutableRadialGradientBrush(
        [new ImmutableGradientStop(0, Color.FromArgb(150, c.R, c.G, c.B)), new ImmutableGradientStop(0.45, Color.FromArgb(55, c.R, c.G, c.B)),
         new ImmutableGradientStop(1, Color.FromArgb(0, c.R, c.G, c.B))], opacity: o, radius: 0.5));
    private static readonly IBrush[,] Cores = Made((c, o) => new ImmutableSolidColorBrush(
        Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2)), o));
    private static readonly IPen[,] Rays = Made((c, o) => new ImmutablePen(
        new ImmutableSolidColorBrush(Color.FromArgb(220, c.R, c.G, c.B), o), 1.1, lineCap: PenLineCap.Round));

    private static T[,] Made<T>(Func<Color, double, T> make)
    {
        var made = new T[Colours.Length, Shades];
        for (int c = 0; c < Colours.Length; c++)
            for (int s = 0; s < Shades; s++)
                made[c, s] = make(Colours[c], (s + 1) / (double)Shades);
        return made;
    }

    private struct Mote
    {
        public double X, Y, Vx, Vy, Size, Life, Age, Sway, Phase;
        public int Colour;
        public bool Star;
    }

    private readonly List<Mote> _motes = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private readonly Random _random = new();
    private double _last;

    public MagicDust()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => Step();
    }

    /// <summary>Dust in the air now.</summary>
    public int Motes => _motes.Count;

    /// <summary>A puff of dust from <paramref name="at"/>, the top of a key struck: rising, spreading.</summary>
    public void Burst(Point at)
    {
        for (int i = 0; i < 46 && _motes.Count < MostMotes; i++)
        {
            double angle = -Math.PI / 2 + (_random.NextDouble() - 0.5) * 1.9;
            double speed = 35 + _random.NextDouble() * 150;
            bool star = _random.NextDouble() < 0.18;
            _motes.Add(new Mote
            {
                X = at.X + (_random.NextDouble() - 0.5) * 10,
                Y = at.Y + (_random.NextDouble() - 0.5) * 6,
                Vx = Math.Cos(angle) * speed,
                Vy = Math.Sin(angle) * speed,
                Size = star ? 2.5 + _random.NextDouble() * 2.5 : 1.2 + _random.NextDouble() * 3.4,
                Life = 1.0 + _random.NextDouble() * 1.4,
                Sway = 10 + _random.NextDouble() * 26,
                Phase = _random.NextDouble() * Math.PI * 2,
                Colour = _random.Next(Colours.Length),
                Star = star,
            });
        }
        if (!_timer.IsEnabled)
        {
            _clock.Restart();
            _last = 0;
            _timer.Start();
        }
        InvalidateVisual();
    }

    /// <summary>The dust moved on by the time since the last frame: up, swaying, slowing, fading.</summary>
    public void Step()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        Advance(dt);
    }

    /// <summary>The dust moved on by <paramref name="dt"/> seconds - the timer's, or a test's.</summary>
    public void Advance(double dt)
    {
        double drag = Math.Pow(0.32, dt);
        for (int i = _motes.Count - 1; i >= 0; i--)
        {
            var m = _motes[i];
            m.Age += dt;
            if (m.Age >= m.Life)
            {
                _motes.RemoveAt(i);
                continue;
            }
            m.Vx = m.Vx * drag + Math.Sin(m.Phase + m.Age * 3.1) * m.Sway * dt * 4;
            m.Vy = m.Vy * drag - 26 * dt;
            m.X += m.Vx * dt;
            m.Y += m.Vy * dt;
            _motes[i] = m;
        }
        if (_motes.Count == 0)
            _timer.Stop();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        foreach (var m in _motes)
        {
            double t = m.Age / m.Life;
            double fade = Math.Min(1, m.Age / 0.12) * (t < 0.55 ? 1 : 1 - (t - 0.55) / 0.45);
            double twinkle = 0.7 + 0.3 * Math.Sin(m.Age * 13 + m.Phase);
            int shade = (int)(fade * twinkle * Shades) - 1;
            if (shade < 0)
                continue;
            shade = Math.Min(shade, Shades - 1);
            var centre = new Point(m.X, m.Y);
            double halo = m.Size * 3.4;
            context.DrawEllipse(Halos[m.Colour, shade], null, centre, halo, halo);
            context.DrawEllipse(Cores[m.Colour, shade], null, centre, m.Size * 0.55, m.Size * 0.55);
            if (m.Star)
            {
                double ray = m.Size * (2.2 + 0.8 * Math.Sin(m.Age * 9 + m.Phase));
                context.DrawLine(Rays[m.Colour, shade], new Point(m.X - ray, m.Y), new Point(m.X + ray, m.Y));
                context.DrawLine(Rays[m.Colour, shade], new Point(m.X, m.Y - ray), new Point(m.X, m.Y + ray));
            }
        }
    }
}
