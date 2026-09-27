namespace MagSid.Sid;

/// <summary>
/// Direct port of reSID's spline.h - a piecewise-cubic interpolator approximating the
/// properties of Catmull-Rom splines for a single-valued function y = f(x), used to turn
/// the small set of measured FC-to-cutoff-frequency control points into full 2048-entry
/// lookup tables (see Filter). Ported using array indices instead of the original's
/// pointer arithmetic; the forward-difference evaluation is used, matching what reSID
/// itself runs by default (SPLINE_BRUTE_FORCE is not defined in the source).
/// </summary>
public static class CubicSpline
{
    /// <summary>
    /// Interpolates the curve through <paramref name="points"/> (x range must span
    /// [0, 2047] with repeated end points, exactly as reSID's fc_point tables do) and
    /// calls <paramref name="plot"/> for each sampled (x, y) pair at step <paramref name="res"/>.
    /// </summary>
    public static void Interpolate(IReadOnlyList<(double X, double Y)> points, Action<double, double> plot, double res)
    {
        int lastIndex = points.Count - 1;

        for (int i2 = 2; i2 < lastIndex; i2++)
        {
            var p0 = points[i2 - 2];
            var p1 = points[i2 - 1];
            var p2 = points[i2];
            var p3 = points[i2 + 1];

            // p1 and p2 equal; single point.
            if (p1.X == p2.X)
            {
                continue;
            }

            double k1, k2;
            if (p0.X == p1.X && p2.X == p3.X)
            {
                // Both end points repeated; straight line.
                k1 = k2 = (p2.Y - p1.Y) / (p2.X - p1.X);
            }
            else if (p0.X == p1.X)
            {
                // p0 and p1 equal; use f''(x1) = 0.
                k2 = (p3.Y - p1.Y) / (p3.X - p1.X);
                k1 = (3 * (p2.Y - p1.Y) / (p2.X - p1.X) - k2) / 2;
            }
            else if (p2.X == p3.X)
            {
                // p2 and p3 equal; use f''(x2) = 0.
                k1 = (p2.Y - p0.Y) / (p2.X - p0.X);
                k2 = (3 * (p2.Y - p1.Y) / (p2.X - p1.X) - k1) / 2;
            }
            else
            {
                // Normal curve.
                k1 = (p2.Y - p0.Y) / (p2.X - p0.X);
                k2 = (p3.Y - p1.Y) / (p3.X - p1.X);
            }

            InterpolateSegmentForwardDifference(p1.X, p1.Y, p2.X, p2.Y, k1, k2, plot, res);
        }
    }

    private static void CubicCoefficients(
        double x1, double y1, double x2, double y2, double k1, double k2,
        out double a, out double b, out double c, out double d)
    {
        double dx = x2 - x1, dy = y2 - y1;

        a = ((k1 + k2) - 2 * dy / dx) / (dx * dx);
        b = ((k2 - k1) / dx - 3 * (x1 + x2) * a) / 2;
        c = k1 - (3 * x1 * a + 2 * b) * x1;
        d = y1 - ((x1 * a + b) * x1 + c) * x1;
    }

    private static void InterpolateSegmentForwardDifference(
        double x1, double y1, double x2, double y2, double k1, double k2, Action<double, double> plot, double res)
    {
        CubicCoefficients(x1, y1, x2, y2, k1, k2, out double a, out double b, out double c, out double d);

        double y = ((a * x1 + b) * x1 + c) * x1 + d;
        double dy = (3 * a * (x1 + res) + 2 * b) * x1 * res + ((a * res + b) * res + c) * res;
        double d2y = (6 * a * (x1 + res) + 2 * b) * res * res;
        double d3y = 6 * a * res * res * res;

        for (double x = x1; x <= x2; x += res)
        {
            plot(x, y);
            y += dy;
            dy += d2y;
            d2y += d3y;
        }
    }
}
