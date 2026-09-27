using MagSid.Sid;

namespace MagSid.Sid.Tests;

/// <summary>Verifies the spline control points against the exact values read from
/// simonowen/resid's filter.cc (f0_points_6581 / f0_points_8580) during porting.</summary>
public class FilterCutoffPointsTests
{
    [Fact]
    public void Mos6581_points_match_source()
    {
        var p = FilterCutoffPoints.Mos6581;
        Assert.Equal(31, p.Length);
        Assert.Equal((0, 220), p[0]);
        Assert.Equal((0, 220), p[1]);
        Assert.Equal((1023, 6000), p[14]);
        Assert.Equal((1023, 6000), p[15]); // discontinuity: same X, different curve segment
        Assert.Equal((1024, 4600), p[16]);
        Assert.Equal((2047, 18000), p[^1]);
    }

    [Fact]
    public void Mos8580_points_match_source()
    {
        var p = FilterCutoffPoints.Mos8580;
        Assert.Equal(19, p.Length);
        Assert.Equal((0, 0), p[0]);
        Assert.Equal((1024, 6500), p[9]);
        Assert.Equal((2047, 12500), p[^1]);
    }
}
