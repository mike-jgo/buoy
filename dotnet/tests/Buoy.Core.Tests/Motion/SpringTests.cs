using Buoy.Core.Motion;

namespace Buoy.Core.Tests.Motion;

public class SpringTests
{
    private static (List<double> Values, double SettleTime) Run(Spring spring, double seconds = 2, double frame = 1.0 / 60)
    {
        var values = new List<double>();
        double settled = -1;
        for (var t = 0.0; t < seconds; t += frame)
        {
            spring.Step(frame);
            values.Add(spring.Value);
            if (settled < 0 && spring.IsAtRest) settled = t + frame;
        }
        return (values, settled);
    }

    [Fact]
    public void CriticallyDampedNeverOvershoots()
    {
        var spring = new Spring(0, dampingRatio: 1, response: 0.3);
        spring.Retarget(300);
        var (values, settled) = Run(spring);
        Assert.All(values, v => Assert.InRange(v, 0, 300));
        Assert.Equal(300, spring.Value);
        // Response 0.3 settles in roughly a third of a second, not a fixed duration.
        Assert.InRange(settled, 0.2, 0.5);
    }

    [Fact]
    public void UnderDampedOvershootsThenSettles()
    {
        var spring = new Spring(0, dampingRatio: 0.68, response: 0.34);
        spring.Retarget(100);
        var (values, _) = Run(spring);
        Assert.True(values.Max() > 100, "a 0.68 spring should overshoot");
        Assert.Equal(100, spring.Value);
    }

    [Fact]
    public void ResponseSetsTheSpeed()
    {
        var quick = new Spring(0, response: 0.2);
        var slow = new Spring(0, response: 0.6);
        quick.Retarget(100);
        slow.Retarget(100);
        Assert.True(Run(quick).SettleTime < Run(slow).SettleTime);
    }

    /// <summary>The skill's "brick wall": a reversal must bend, not restart from rest.</summary>
    [Fact]
    public void RetargetingMidFlightKeepsVelocity()
    {
        var spring = new Spring(0, response: 0.3);
        spring.Retarget(300);
        for (var i = 0; i < 6; i++) spring.Step(1.0 / 60);
        var midway = spring.Value;
        var speed = spring.Velocity;
        Assert.True(speed > 0);

        spring.Retarget(0); // reverse
        Assert.Equal(midway, spring.Value);
        Assert.Equal(speed, spring.Velocity);
        spring.Step(1.0 / 60);
        Assert.True(spring.Value > midway, "momentum carries it on briefly before it turns back");
        Run(spring);
        Assert.Equal(0, spring.Value);
    }

    [Fact]
    public void SnapStopsAtOnce()
    {
        var spring = new Spring(0);
        spring.Retarget(50);
        spring.Step(1.0 / 60);
        spring.Snap(80);
        Assert.True(spring.IsAtRest);
        Assert.Equal(80, spring.Value);
    }

    [Fact]
    public void AHitchDoesNotFlingIt()
    {
        var spring = new Spring(0, response: 0.3);
        spring.Retarget(100);
        spring.Step(5); // a five-second stall
        Assert.InRange(spring.Value, 0, 100);
    }
}
