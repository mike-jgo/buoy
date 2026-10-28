namespace Buoy.Core.Motion;

/// <summary>
/// A timing curve from (0,0) to (1,1) through two control points, for
/// animations that step themselves frame by frame. The same curves as Core
/// Animation's named timing functions, which the Mac's window animations use:
/// <see cref="EaseOut"/> is (0, 0, 0.58, 1), <see cref="EaseInOut"/> (0.42, 0, 0.58, 1).
/// </summary>
public readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
{
    public static readonly CubicBezier EaseOut = new(0, 0, 0.58, 1);
    public static readonly CubicBezier EaseInOut = new(0.42, 0, 0.58, 1);

    /// <summary>The curve's progress at time fraction <paramref name="t"/>, clamped to 0...1.</summary>
    public double Evaluate(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t is 0 or 1) return t;

        // Find the curve parameter whose x is t (Newton's method, bisection if it
        // stalls on a flat stretch), then read y there.
        var u = t;
        for (var i = 0; i < 8; i++)
        {
            var error = Sample(X1, X2, u) - t;
            if (Math.Abs(error) < 1e-7) return Sample(Y1, Y2, u);
            var slope = Slope(X1, X2, u);
            if (Math.Abs(slope) < 1e-6) break;
            u -= error / slope;
        }

        double low = 0, high = 1;
        u = t;
        for (var i = 0; i < 50; i++)
        {
            var x = Sample(X1, X2, u);
            if (Math.Abs(x - t) < 1e-7) break;
            if (x < t) low = u;
            else high = u;
            u = (low + high) / 2;
        }
        return Sample(Y1, Y2, u);
    }

    private static double Sample(double p1, double p2, double u) =>
        ((1 - 3 * p2 + 3 * p1) * u + (3 * p2 - 6 * p1)) * u * u + 3 * p1 * u;

    private static double Slope(double p1, double p2, double u) =>
        3 * (1 - 3 * p2 + 3 * p1) * u * u + 2 * (3 * p2 - 6 * p1) * u + 3 * p1;
}
