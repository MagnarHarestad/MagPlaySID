using System.Numerics;
using SkiaSharp;

namespace MagPlaySid.Player;

/// <summary>
/// A fighter's body as a mesh of lit triangles: its limbs as tubes rounded at their ends, its head,
/// pads and fists as ellipsoids, its straps, belt and plates, each shaded where it stands - the
/// moon's cool light from above, a warm rim from the lanterns behind, a sheen on what is metal or
/// lacquered - and laid back to front, ready to paint at once. Its triangles are the same in number
/// every frame, so the arrays it fills are made once.
/// </summary>
internal sealed class NinjaMesh
{
    /// <summary>What a surface is made of: cloth takes the light softly, skin a little warmer, metal
    /// and lacquer with a highlight, and a glow is lit by itself.</summary>
    public enum Stuff : byte { Cloth, Skin, Metal, Lacquer, Glow, Ground, Water }

    private const int Sides = 8;

    private Vector3[] _position = new Vector3[2048];
    private Vector3[] _normal = new Vector3[2048];
    private uint[] _colour = new uint[2048];
    private Stuff[] _stuff = new Stuff[2048];
    private int _vertices;
    private int[] _triangles = new int[6144];
    private int _indices;

    private float[] _depth = [];
    private int[] _order = [];
    private SKPoint[] _screen = [];
    private float[] _screenDepth = [];
    private SKColor[] _lit = [];
    private bool[] _away = [];
    private byte[] _outside = [];

    public void Begin()
    {
        _vertices = 0;
        _indices = 0;
    }

    private int Vertex(Vector3 position, Vector3 normal, uint colour, Stuff stuff)
    {
        if (_vertices == _position.Length)
        {
            Array.Resize(ref _position, _vertices * 2);
            Array.Resize(ref _normal, _vertices * 2);
            Array.Resize(ref _colour, _vertices * 2);
            Array.Resize(ref _stuff, _vertices * 2);
        }
        _position[_vertices] = position;
        _normal[_vertices] = normal;
        _colour[_vertices] = colour;
        _stuff[_vertices] = stuff;
        return _vertices++;
    }

    /// <summary>A point of a mesh built by hand - the ground's.</summary>
    public int Point(Vector3 position, Vector3 normal, uint colour, Stuff stuff) => Vertex(position, normal, colour, stuff);

    public void Face(int a, int b, int c) => Triangle(a, b, c);

    private void Triangle(int a, int b, int c)
    {
        if (_indices + 3 > _triangles.Length)
            Array.Resize(ref _triangles, _triangles.Length * 2);
        _triangles[_indices++] = a;
        _triangles[_indices++] = b;
        _triangles[_indices++] = c;
    }

    /// <summary>Two directions square to <paramref name="axis"/> and to each other.</summary>
    private static (Vector3 U, Vector3 V) Square(Vector3 axis, Vector3 hint)
    {
        var u = Vector3.Cross(axis, hint);
        if (u.LengthSquared() < 1e-6f)
            u = Vector3.Cross(axis, Vector3.UnitX);
        u = Vector3.Normalize(u);
        return (u, Vector3.Cross(u, axis));
    }

    /// <summary>A tube from <paramref name="a"/> to <paramref name="b"/>, its radius going from one to
    /// the other, its section stretched <paramref name="wide"/> times across <paramref name="side"/>,
    /// rounded off at both ends.</summary>
    public void Tube(Vector3 a, Vector3 b, float ra, float rb, uint colour, Stuff stuff, Vector3 side = default, float wide = 1)
    {
        var axis = b - a;
        float length = axis.Length();
        if (length < 1e-4f)
            return;
        axis /= length;
        var (u, v) = Square(axis, side == default ? Vector3.UnitY : side);
        if (side != default)
            (u, v) = (Vector3.Normalize(side - (axis * Vector3.Dot(side, axis))), Vector3.Cross(axis, Vector3.Normalize(side - (axis * Vector3.Dot(side, axis)))));

        // Rings from the cap at a, along the tube, to the cap at b: where along, how wide, how far
        // the normal leans back along the axis.
        Span<(Vector3 Centre, float Radius, float Lean)> rings =
        [
            (a - (axis * ra * 0.7f), ra * 0.71f, -0.71f),
            (a, ra, 0),
            (b, rb, 0),
            (b + (axis * rb * 0.7f), rb * 0.71f, 0.71f),
        ];
        int first = _vertices;
        int poleA = Vertex(a - (axis * ra), -axis, colour, stuff);
        foreach (var (centre, radius, lean) in rings)
        {
            for (int s = 0; s < Sides; s++)
            {
                float angle = s * MathF.Tau / Sides;
                var out_ = (u * (MathF.Cos(angle) * wide)) + (v * MathF.Sin(angle));
                var normal = Vector3.Normalize((Vector3.Normalize(out_) * MathF.Sqrt(1 - (lean * lean))) + (axis * lean));
                Vertex(centre + (out_ * radius), normal, colour, stuff);
            }
        }
        int poleB = Vertex(b + (axis * rb), axis, colour, stuff);
        int ring0 = first + 1;
        for (int s = 0; s < Sides; s++)
            Triangle(poleA, ring0 + ((s + 1) % Sides), ring0 + s);
        for (int r = 0; r < rings.Length - 1; r++)
        {
            int here = ring0 + (r * Sides), next = here + Sides;
            for (int s = 0; s < Sides; s++)
            {
                int s1 = (s + 1) % Sides;
                Triangle(here + s, here + s1, next + s1);
                Triangle(here + s, next + s1, next + s);
            }
        }
        int last = ring0 + ((rings.Length - 1) * Sides);
        for (int s = 0; s < Sides; s++)
            Triangle(last + s, last + ((s + 1) % Sides), poleB);
    }

    /// <summary>An ellipsoid about <paramref name="centre"/> with the three half-axes given.</summary>
    public void Ball(Vector3 centre, Vector3 x, Vector3 y, Vector3 z, uint colour, Stuff stuff)
    {
        const int Rings = 5;
        int top = Vertex(centre + y, Vector3.Normalize(y), colour, stuff);
        int first = _vertices;
        for (int r = 1; r < Rings; r++)
        {
            float polar = r * MathF.PI / Rings;
            for (int s = 0; s < Sides; s++)
            {
                float angle = s * MathF.Tau / Sides;
                var unit = (x * (MathF.Sin(polar) * MathF.Cos(angle))) + (y * MathF.Cos(polar)) + (z * (MathF.Sin(polar) * MathF.Sin(angle)));
                // The normal of an ellipsoid: the point's direction scaled by each axis' inverse.
                var n = (Vector3.Normalize(x) * (Vector3.Dot(unit, Vector3.Normalize(x)) / x.LengthSquared()))
                        + (Vector3.Normalize(y) * (Vector3.Dot(unit, Vector3.Normalize(y)) / y.LengthSquared()))
                        + (Vector3.Normalize(z) * (Vector3.Dot(unit, Vector3.Normalize(z)) / z.LengthSquared()));
                Vertex(centre + unit, Vector3.Normalize(n), colour, stuff);
            }
        }
        int bottom = Vertex(centre - y, -Vector3.Normalize(y), colour, stuff);
        for (int s = 0; s < Sides; s++)
            Triangle(top, first + s, first + ((s + 1) % Sides));
        for (int r = 0; r < Rings - 2; r++)
        {
            int here = first + (r * Sides), next = here + Sides;
            for (int s = 0; s < Sides; s++)
            {
                int s1 = (s + 1) % Sides;
                Triangle(here + s, next + s, next + s1);
                Triangle(here + s, next + s1, here + s1);
            }
        }
        int last = first + ((Rings - 2) * Sides);
        for (int s = 0; s < Sides; s++)
            Triangle(last + s, bottom, last + ((s + 1) % Sides));
    }

    /// <summary>A sphere, for a fist, a stud, a knot.</summary>
    public void Ball(Vector3 centre, float radius, uint colour, Stuff stuff) =>
        Ball(centre, new Vector3(radius, 0, 0), new Vector3(0, radius, 0), new Vector3(0, 0, radius), colour, stuff);

    /// <summary>A strip of cloth along <paramref name="points"/>, <paramref name="width"/> wide across
    /// <paramref name="across"/>, narrowing to its end: both its faces.</summary>
    public void Ribbon(ReadOnlySpan<Vector3> points, Vector3 across, float width, uint colour)
    {
        int first = _vertices;
        for (int i = 0; i < points.Length; i++)
        {
            float w = width * (1 - (i * 0.6f / points.Length));
            var along = i + 1 < points.Length ? points[i + 1] - points[i] : points[i] - points[i - 1];
            var normal = Vector3.Cross(along, across);
            normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
            Vertex(points[i] - (across * w), normal, colour, Stuff.Cloth);
            Vertex(points[i] + (across * w), normal, colour, Stuff.Cloth);
        }
        for (int i = 0; i < points.Length - 1; i++)
        {
            int a = first + (2 * i);
            Triangle(a, a + 1, a + 3);
            Triangle(a, a + 3, a + 2);
        }
    }

    /// <summary>The nearest a corner may be to the camera, and the farthest off the view it may land,
    /// in pixels, for its triangle to be drawn.</summary>
    private const float NearestDepth = 1.5f, Farthest = 20000;

    /// <summary>
    /// The mesh lit, seen and laid out into <paramref name="frame"/>'s mesh <paramref name="slot"/>:
    /// each vertex shaded and put on the view, each triangle at the depth of its middle, farthest
    /// first. The light: the moon from above and to the side, cool; the lanterns' warmth on the
    /// edges turned away from the camera; a highlight on metal and lacquer.
    /// </summary>
    public bool Paint(NinjaFrame frame, int slot, Func<Vector3, (bool Seen, float X, float Y, float Depth)> project, Vector3 eye, float fade, float key = float.NaN)
    {
        int triangles = _indices / 3;
        if (_screen.Length < _vertices)
        {
            _screen = new SKPoint[_vertices];
            _screenDepth = new float[_vertices];
            _lit = new SKColor[_vertices];
            _away = new bool[_vertices];
            _outside = new byte[_vertices];
        }
        // Kept and grown, never shrunk: one mesh is made and painted for each fighter in turn.
        if (_depth.Length < triangles)
        {
            _depth = new float[triangles];
            _order = new int[triangles];
        }

        float width = (float)frame.Size.Width, height = (float)frame.Size.Height;
        float sum = 0;
        int seenCount = 0;
        for (int i = 0; i < _vertices; i++)
        {
            var (seen, x, y, depth) = project(_position[i]);
            // A corner too near the camera, or behind it, lands far off the view - farther than Skia
            // can draw a triangle to - and one that came out of nothing, a direction from two points
            // in one place, is not a number: the triangles with such a corner are left out.
            if (!seen || depth < NearestDepth || !float.IsFinite(x) || !float.IsFinite(y) || MathF.Abs(x) > Farthest || MathF.Abs(y) > Farthest)
            {
                _screenDepth[i] = float.NaN;
                continue;
            }
            _screen[i] = new SKPoint(x, y);
            _screenDepth[i] = depth;
            _away[i] = Vector3.Dot(_normal[i], eye - _position[i]) < 0;
            _outside[i] = (byte)((x < 0 ? 1 : 0) | (x > width ? 2 : 0) | (y < 0 ? 4 : 0) | (y > height ? 8 : 0));
            sum += depth;
            seenCount++;
            _lit[i] = Shade(_position[i], _normal[i], _colour[i], _stuff[i], eye, fade);
        }
        if (seenCount == 0)
            return false;

        int drawn = 0;
        for (int t = 0; t < triangles; t++)
        {
            int a = _triangles[3 * t], b = _triangles[(3 * t) + 1], c = _triangles[(3 * t) + 2];
            float depth = _screenDepth[a] + _screenDepth[b] + _screenDepth[c];
            // Left out: a triangle with a corner left out, one turned wholly away from the camera -
            // hidden behind the rest - and one wholly off one side of the view.
            if (float.IsNaN(depth) || (_away[a] && _away[b] && _away[c]) || (_outside[a] & _outside[b] & _outside[c]) != 0)
                continue;
            _depth[drawn] = -depth;
            _order[drawn++] = t;
        }
        if (drawn == 0)
            return false;
        Array.Sort(_depth, _order, 0, drawn);

        // The arrays come in steps of a few hundred triangles, so they are seldom made anew; the
        // corners past the last triangle drawn are one point, off the view, drawing nothing.
        var (points, colours) = frame.MeshArrays(slot, ((drawn + 255) / 256) * 256 * 3);
        for (int k = 0; k < drawn; k++)
        {
            int t = _order[k];
            for (int corner = 0; corner < 3; corner++)
            {
                int v = _triangles[(3 * t) + corner];
                points[(3 * k) + corner] = _screen[v];
                colours[(3 * k) + corner] = _lit[v];
            }
        }
        Array.Fill(points, new SKPoint(-16, -16), drawn * 3, points.Length - (drawn * 3));
        Array.Fill(colours, SKColors.Transparent, drawn * 3, colours.Length - (drawn * 3));
        frame.Mesh(key is float.NaN ? -(sum / seenCount) : key, slot);
        return true;
    }

    /// <summary>The evening's light: the sun low in the west, warm; the sky's cool blue and violet
    /// from above; the haze it all fades into with distance.</summary>
    public static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.8f, 0.3f, -0.45f));
    public const float HazeR = 0.27f, HazeG = 0.22f, HazeB = 0.36f;

    private static SKColor Shade(Vector3 p, Vector3 n, uint colour, Stuff stuff, Vector3 eye, float fade)
    {
        float r = ((colour >> 16) & 0xFF) / 255f, g = ((colour >> 8) & 0xFF) / 255f, b = (colour & 0xFF) / 255f;
        byte alpha = (byte)(((colour >> 24) & 0xFF) * fade);
        float lr, lg, lb;
        var view = Vector3.Normalize(eye - p);
        if (stuff == Stuff.Glow)
        {
            (lr, lg, lb) = (r, g, b);
        }
        else if (stuff == Stuff.Water)
        {
            // The rice paddies hold the evening sky: their colour is its reflection, and the sun glints.
            float glint = MathF.Pow(MathF.Max(0, Vector3.Dot(Vector3.Reflect(-view, Vector3.UnitY), Sun)), 40) * 1.4f;
            (lr, lg, lb) = (r + glint, g + (glint * 0.6f), b + (glint * 0.3f));
        }
        else
        {
            float facing = Vector3.Dot(n, view);
            if (facing < 0 && stuff != Stuff.Ground)
            {
                n = -n;
                facing = -facing;
            }
            float sun = MathF.Max(0, Vector3.Dot(n, Sun));
            float sky = 0.5f + (0.5f * n.Y);
            float rim = stuff == Stuff.Ground ? 0 : MathF.Pow(1 - MathF.Abs(facing), 3) * 0.75f;
            var half = Vector3.Normalize(Sun + view);
            float shine = stuff switch
            {
                Stuff.Metal => MathF.Pow(MathF.Max(0, Vector3.Dot(n, half)), 30) * 1.1f,
                Stuff.Lacquer => MathF.Pow(MathF.Max(0, Vector3.Dot(n, half)), 18) * 0.7f,
                Stuff.Skin => MathF.Pow(MathF.Max(0, Vector3.Dot(n, half)), 10) * 0.2f,
                _ => 0,
            };
            // The sun orange, the sky's fill blue-violet, the rim the lanterns' warmth.
            lr = (r * ((sun * 1.15f) + (sky * 0.22f))) + (rim * 0.6f) + shine;
            lg = (g * ((sun * 0.72f) + (sky * 0.2f))) + (rim * 0.32f) + (shine * 0.85f);
            lb = (b * ((sun * 0.45f) + (sky * 0.38f))) + (rim * 0.14f) + (shine * 0.7f);
        }

        // Haze, thicker with distance.
        float far = Vector3.Distance(p, eye);
        float haze = far < 14 ? 0 : (1 - MathF.Exp(-(far - 14) * 0.03f)) * 0.85f;
        lr += (HazeR - lr) * haze;
        lg += (HazeG - lg) * haze;
        lb += (HazeB - lb) * haze;
        return new SKColor((byte)Math.Clamp(lr * 255, 0, 255), (byte)Math.Clamp(lg * 255, 0, 255), (byte)Math.Clamp(lb * 255, 0, 255), alpha);
    }
}
