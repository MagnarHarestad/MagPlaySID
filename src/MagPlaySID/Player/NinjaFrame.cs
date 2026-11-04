using Avalonia;
using SkiaSharp;

namespace MagPlaySid.Player;

/// <summary>
/// One frame of the Last Ninja meter as shapes on the view - strokes, discs, ellipses, polygons and
/// flashes, each with its colour, a blur for what glows and whether its light adds to what is under
/// it - and the order to paint them in: the sky, the ground in the order laid, all that stands on
/// it farthest first, the flashes over everything. Made on the window's thread, painted on the
/// renderer's; the arrays are kept and refilled, so a frame costs nothing to make.
/// </summary>
internal sealed class NinjaFrame
{
    public enum Kind : byte { Line, Disc, Ellipse, Polygon, Outline, Flash, Mesh }

    public struct Shape
    {
        public Kind Kind;
        public bool Additive;
        public float X1, Y1, X2, Y2, Width, Blur;
        public uint Colour;
        public int First, Count;
    }

    /// <summary>The key that lays a shape flat on the ground, painted in the order laid, under all
    /// that stands - on the land far below, or on the hill, as <see cref="Layer"/> says.</summary>
    public const float Ground = float.MinValue;

    /// <summary>The layers, painted one after another: the land far off; all that stands on it,
    /// farthest first; the hill over it, which hides what lies below and behind it; all that stands
    /// on the hill, farthest first; the flashes over everything.</summary>
    public enum Layers { Land, Valley, Hill }

    /// <summary>The layer what is laid now goes into.</summary>
    public Layers Layer { get; set; }

    private const double LandFrom = -4e9, ValleyFrom = -3e9, HillFrom = -2e9, Over = 1e9;

    public Shape[] Shapes = new Shape[2048];
    public double[] Keys = new double[2048];
    public int Count;
    public float[] Points = new float[8192];
    public int PointCount;

    public Size Size { get; private set; }
    public uint SkyTop { get; private set; }
    public uint SkyBottom { get; private set; }

    private int _laid;
    private int _polygonFrom;

    public void Begin(Size size)
    {
        Size = size;
        Count = 0;
        PointCount = 0;
        _laid = 0;
        Layer = Layers.Land;
    }

    public void Background(uint top, uint bottom) => (SkyTop, SkyBottom) = (top, bottom);

    private ref Shape Add(float key, Kind kind)
    {
        if (Count == Shapes.Length)
        {
            Array.Resize(ref Shapes, Count * 2);
            Array.Resize(ref Keys, Count * 2);
        }
        // What lies on the ground, and the flashes, keep the order they are laid in; what stands is
        // painted farthest first, in the valley before the hill.
        Keys[Count] = key >= 1e6f ? Over + _laid++
                    : key == Ground ? (Layer == Layers.Hill ? HillFrom : LandFrom) + _laid++
                    : Layer == Layers.Valley ? ValleyFrom + key
                    : Layer == Layers.Hill ? key
                    : LandFrom + _laid++;
        ref var shape = ref Shapes[Count++];
        shape = default;
        shape.Kind = kind;
        return ref shape;
    }

    public void Line(float key, float x1, float y1, float x2, float y2, float width, uint colour, bool additive = false, float blur = 0)
    {
        ref var s = ref Add(key, Kind.Line);
        (s.X1, s.Y1, s.X2, s.Y2, s.Width, s.Colour, s.Additive, s.Blur) = (x1, y1, x2, y2, width, colour, additive, blur);
    }

    public void Disc(float key, float x, float y, float radius, uint colour, float blur, bool additive)
    {
        ref var s = ref Add(key, Kind.Disc);
        (s.X1, s.Y1, s.Width, s.Colour, s.Blur, s.Additive) = (x, y, radius, colour, blur, additive);
    }

    public void Ellipse(float key, float x, float y, float rx, float ry, uint colour, float blur)
    {
        ref var s = ref Add(key, Kind.Ellipse);
        (s.X1, s.Y1, s.X2, s.Y2, s.Colour, s.Blur) = (x, y, rx, ry, colour, blur);
    }

    public void BeginPolygon() => _polygonFrom = PointCount;

    public void Point(float x, float y)
    {
        if (PointCount + 2 > Points.Length)
            Array.Resize(ref Points, Points.Length * 2);
        Points[PointCount++] = x;
        Points[PointCount++] = y;
    }

    /// <summary>The points since <see cref="BeginPolygon"/>, filled or outlined - in the ground's
    /// layer, unless a <paramref name="key"/> sets it among what stands.</summary>
    public void EndPolygon(bool fill, float width, uint colour, bool additive, float blur, float key = Ground)
    {
        int count = (PointCount - _polygonFrom) / 2;
        if (count < 3)
        {
            PointCount = _polygonFrom;
            return;
        }
        ref var s = ref Add(key, fill ? Kind.Polygon : Kind.Outline);
        (s.First, s.Count, s.Width, s.Colour, s.Additive, s.Blur) = (_polygonFrom, count, width, colour, additive, blur);
    }

    /// <summary>The meshes' lit triangles, each mesh in a slot of its own, three corners a triangle,
    /// back to front: kept, and made anew only when a mesh needs more room than they have.</summary>
    private readonly List<(SKPoint[] Points, SKColor[] Colours)> _meshes = [];

    public (SKPoint[] Points, SKColor[] Colours) MeshArrays(int slot, int corners)
    {
        while (_meshes.Count <= slot)
            _meshes.Add(([], []));
        if (_meshes[slot].Points.Length < corners)
            _meshes[slot] = (new SKPoint[corners], new SKColor[corners]);
        return _meshes[slot];
    }

    public (SKPoint[] Points, SKColor[] Colours) MeshOf(int slot) => _meshes[slot];

    /// <summary>The mesh in <paramref name="slot"/>, painted at once where <paramref name="key"/> puts it.</summary>
    public void Mesh(float key, int slot)
    {
        ref var s = ref Add(key, Kind.Mesh);
        s.First = slot;
    }

    /// <summary>The whole view lit up in <paramref name="colour"/>, over everything.</summary>
    public void Flash(uint colour)
    {
        ref var s = ref Add(1e7f, Kind.Flash);
        (s.Colour, s.Additive) = (colour, true);
    }

    /// <summary>The edges darkened, over everything but the flashes laid after it.</summary>
    public void Vignette()
    {
        ref var s = ref Add(1e7f, Kind.Flash);
        s.Colour = 0;
    }

    /// <summary>The shapes in the order to paint them.</summary>
    public void Sort() => Array.Sort(Keys, Shapes, 0, Count);
}
