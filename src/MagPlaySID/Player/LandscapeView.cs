using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MagPlaySid.Player;

/// <summary>
/// The music as a landscape to fly into, in lines: each moment of the spectrum is a ridge that rises
/// on the horizon and comes towards the viewer, the bass in tall peaks down the middle, the treble
/// lower and further out, and the land sinking to a flat grid as it nears. It moves by the tune's own
/// time, so it stands still when the tune is paused and rushes when it fast-forwards. Nearer ridges
/// hide what lies behind them, and the lines fade with distance. Worked out and painted only when
/// the tune has moved.
/// </summary>
internal sealed class LandscapeView : Control
{
    private const int Rows = 30;
    private const int Columns = 121;

    /// <summary>The columns either side of the middle that the bands rise in; beyond them lies the
    /// floor, only rippling.</summary>
    private const int PeakColumns = 40;

    /// <summary>The tune's samples between two ridges: twenty a second.</summary>
    private const int RowSamples = 2205;

    private const double Near = 1.0;
    private const double RowDepth = 0.4;

    /// <summary>A column's width, as a share of the view's height one unit of depth away: the same
    /// land however wide the view, its columns only widening towards the edges to reach them.</summary>
    private const double ColumnWidth = 0.42;

    private const double CameraHeight = 0.95;
    private const double HorizonAt = 0.52;

    /// <summary>How far off, as a share of the way to the horizon, the floor beyond the peaks starts
    /// to thin out: drawn in full, its lines would run together into a grey wall.</summary>
    private const double ThinFrom = 0.3;

    /// <summary>Of the far floor, one ridge in this many is drawn right across, and one column in half as many.</summary>
    private const int ThinRidges = 4;

    /// <summary>The tallest peak, as a share of the view's height, on the horizon.</summary>
    private const double PeakHeight = 1.5;

    /// <summary>How finely the fading with distance is drawn: as many pens as this.</summary>
    private const int Shades = 16;

    /// <summary>Below this a band's level leaves the ground flat: quiet is plain, loud is mountains.</summary>
    private const float GroundLevel = 0.32f;

    private static readonly IBrush Ground = SidPlayerPalette.Brush(SidPlayerPalette.PanelBackground);
    private static readonly IPen Border = SidPlayerPalette.Pen(SidPlayerPalette.PanelBorder);
    private static readonly IPen[] Lines = Enumerable.Range(0, Shades)
        .Select(s => SidPlayerPalette.Pen(SidPlayerPalette.WithAlpha(SidPlayerPalette.Title, 70 + 180 * s / (Shades - 1)), 0.8))
        .ToArray();

    /// <summary>The ridges' heights, a ring: <see cref="_newest"/> is the one on the horizon.</summary>
    private readonly float[][] _heights = Enumerable.Range(0, Rows).Select(_ => new float[Columns]).ToArray();
    private int _newest;
    private long _row = -1;
    private double _between;

    private Point[] _nearRidge = new Point[Columns];
    private Point[] _farRidge = new Point[Columns];
    private readonly double[] _across = new double[Columns];

    public LandscapeView() => ClipToBounds = true;

    /// <summary>On to the tune's sample <paramref name="at"/>: a new ridge from
    /// <paramref name="levels"/>, the spectrum's bands from low to high, for each one due.</summary>
    public void Update(IReadOnlyList<float> levels, long at)
    {
        long row = at / RowSamples;
        _between = (at % RowSamples) / (double)RowSamples;
        if (_row < 0)
            _row = row - 1;

        int due = (int)Math.Clamp(row - _row, 0, Rows);
        _row = row;
        for (int i = 0; i < due; i++)
        {
            _newest = (_newest + 1) % Rows;
            Ridge(levels, _heights[_newest]);
        }
        InvalidateVisual();
    }

    /// <summary>A flat land again, for a new tune.</summary>
    public void Clear()
    {
        foreach (var ridge in _heights)
            Array.Clear(ridge);
        _row = -1;
        _between = 0;
        InvalidateVisual();
    }

    /// <summary>A ridge from the bands: lowest in the middle, highest at the edges, the peaks falling
    /// away outward; the two sides a band apart, so the land is not a mirror.</summary>
    private static void Ridge(IReadOnlyList<float> levels, float[] heights)
    {
        int middle = Columns / 2;
        for (int column = 0; column < Columns; column++)
        {
            double out_ = Math.Min(1, Math.Abs(column - middle) / (double)PeakColumns);
            int band = (int)(Math.Pow(out_, 0.75) * (levels.Count - 2)) + (column > middle ? 1 : 0);
            double level = Math.Max(0, (levels[band] - GroundLevel) / (1 - GroundLevel));

            double falling = 1 - out_;
            heights[column] = (float)(Math.Pow(level, 1.15) * (0.05 + 0.95 * Math.Pow(falling, 4)));
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Ground, bounds);
        Spread(bounds);

        // From the horizon forward, each band of land covering what is behind it, and of its columns
        // only those in view: the nearer the land, the fewer.
        Project(_heights[_newest], Depth(0), bounds, _farRidge);
        for (int age = 1; age < Rows; age++)
        {
            double depth = Depth(age);
            Project(_heights[(_newest - age + Rows) % Rows], depth, bounds, _nearRidge);

            int from = 0, to = Columns - 1;
            while (from < Columns / 2 && _farRidge[from + 1].X < 0)
                from++;
            while (to > Columns / 2 && _farRidge[to - 1].X > bounds.Width)
                to--;

            var land = new StreamGeometry();
            using (var path = land.Open())
            {
                path.BeginFigure(_nearRidge[from], true);
                for (int column = from + 1; column <= to; column++)
                    path.LineTo(_nearRidge[column]);
                for (int column = to; column >= from; column--)
                    path.LineTo(_farRidge[column]);
                path.EndFigure(true);
            }
            context.DrawGeometry(Ground, null, land);

            double distance = (depth - Near) / ((Rows - 1) * RowDepth);
            bool thin = distance > ThinFrom;
            bool across = !thin || (_row - age) % ThinRidges == 0;
            int peaksFrom = Math.Max(from, Columns / 2 - PeakColumns), peaksTo = Math.Min(to, Columns / 2 + PeakColumns);

            var lines = new StreamGeometry();
            using (var path = lines.Open())
            {
                int lineFrom = across ? from : peaksFrom, lineTo = across ? to : peaksTo;
                path.BeginFigure(_nearRidge[lineFrom], false);
                for (int column = lineFrom + 1; column <= lineTo; column++)
                    path.LineTo(_nearRidge[column]);
                path.EndFigure(false);
                for (int column = from; column <= to; column++)
                {
                    if (thin && (column < peaksFrom || column > peaksTo) && (column - Columns / 2) % (ThinRidges / 2) != 0)
                        continue;
                    path.BeginFigure(_nearRidge[column], false);
                    path.LineTo(_farRidge[column]);
                    path.EndFigure(false);
                }
            }
            int shade = (int)(Math.Pow(1 - Math.Clamp(distance, 0, 1), 1.4) * (Shades - 1));
            context.DrawGeometry(null, Lines[shade], lines);

            (_nearRidge, _farRidge) = (_farRidge, _nearRidge);
        }

        context.DrawRectangle(Border, bounds.Deflate(0.5));
    }

    /// <summary>How far away a ridge is that came over the horizon <paramref name="age"/> ridges ago,
    /// creeping forward between two ridges so the flight is smooth.</summary>
    private static double Depth(int age, double between) => Near + (Rows - 1 - age - between) * RowDepth;

    private double Depth(int age) => Depth(age, _between);

    /// <summary>Where each column lies from the middle: evenly round the peaks, and further and further
    /// apart outward, so that the last are at the view's edges on the horizon.</summary>
    private void Spread(Rect bounds)
    {
        int middle = Columns / 2;
        double reach = bounds.Width / 2 / Math.Max(1, bounds.Height) * Depth(0, 0);
        double widening = Math.Max(0, reach / (ColumnWidth * middle) - 1);
        for (int column = 0; column < Columns; column++)
        {
            double out_ = (column - middle) / (double)middle;
            _across[column] = ColumnWidth * (column - middle) * (1 + widening * out_ * out_);
        }
    }

    private void Project(float[] heights, double depth, Rect bounds, Point[] points)
    {
        double horizon = bounds.Height * HorizonAt;
        double scale = bounds.Height / depth;

        // The land flattens as it nears, and its far peaks keep more of their height than distance
        // alone would leave them: a floor to fly along, and towers ahead.
        double far = Math.Clamp((depth - Near) / ((Rows - 1) * RowDepth), 0, 1);
        double rising = Math.Clamp((far - 0.18) / 0.34, 0, 1);
        rising *= rising * (3 - 2 * rising);
        double rise = bounds.Height * PeakHeight * rising * Math.Pow(1 / depth, 0.22);
        for (int column = 0; column < Columns; column++)
        {
            points[column] = new Point(
                bounds.Width / 2 + _across[column] * scale,
                horizon + CameraHeight * scale * 0.9 - heights[column] * rise);
        }
    }
}
