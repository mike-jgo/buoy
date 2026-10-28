using Buoy.Core.Motion;

namespace Buoy.Core.Tests.Motion;

public class CubicBezierTests
{
    [Fact]
    public void StartsAtZeroAndEndsAtOne()
    {
        foreach (var curve in new[] { CubicBezier.EaseOut, CubicBezier.EaseInOut })
        {
            Assert.Equal(0, curve.Evaluate(0));
            Assert.Equal(1, curve.Evaluate(1));
        }
    }

    [Fact]
    public void ClampsOutsideTheUnitInterval()
    {
        Assert.Equal(0, CubicBezier.EaseOut.Evaluate(-0.5));
        Assert.Equal(1, CubicBezier.EaseOut.Evaluate(1.5));
    }

    [Fact]
    public void ControlPointsOnTheDiagonalAreLinear()
    {
        var linear = new CubicBezier(1.0 / 3, 1.0 / 3, 2.0 / 3, 2.0 / 3);
        for (var t = 0.0; t <= 1; t += 0.05)
            Assert.Equal(t, linear.Evaluate(t), 6);
    }

    [Fact]
    public void EaseInOutIsSymmetric()
    {
        Assert.Equal(0.5, CubicBezier.EaseInOut.Evaluate(0.5), 6);
        for (var t = 0.05; t < 0.5; t += 0.05)
            Assert.Equal(1, CubicBezier.EaseInOut.Evaluate(t) + CubicBezier.EaseInOut.Evaluate(1 - t), 6);
    }

    [Fact]
    public void EaseOutRisesMonotonicallyAndFrontLoads()
    {
        var previous = 0.0;
        for (var t = 0.01; t <= 1; t += 0.01)
        {
            var value = CubicBezier.EaseOut.Evaluate(t);
            Assert.True(value >= previous);
            previous = value;
        }
        // Most of the distance is covered early: the reason it feels quick.
        Assert.True(CubicBezier.EaseOut.Evaluate(0.5) > 0.6);
    }
}
