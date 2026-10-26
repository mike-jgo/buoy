namespace Buoy.Core.Motion;

/// <summary>
/// A damped spring driven frame by frame, for what composition can't animate
/// (the window's own size and position). Parameters are Apple's: damping ratio
/// (1 = no overshoot) and response (the undamped period, in seconds).
///
/// Retargeting keeps the current value and velocity, so a spring redirected
/// mid-flight bends smoothly instead of restarting from rest: the
/// interruptibility that a fixed-duration curve can't give.
/// </summary>
public sealed class Spring
{
    private const double SubStep = 1.0 / 1000; // integrate in 1 ms steps, whatever the frame rate
    private const double RestDistance = 0.5;
    private const double RestSpeed = 5;

    public double DampingRatio { get; }
    public double Response { get; }

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }

    public bool IsAtRest => Value == Target && Velocity == 0;

    public Spring(double value, double dampingRatio = 1.0, double response = 0.3)
    {
        if (dampingRatio <= 0) throw new ArgumentOutOfRangeException(nameof(dampingRatio));
        if (response <= 0) throw new ArgumentOutOfRangeException(nameof(response));
        Value = Target = value;
        DampingRatio = dampingRatio;
        Response = response;
    }

    /// <summary>Aims at a new target from wherever the spring is now, keeping its velocity.</summary>
    public void Retarget(double target) => Target = target;

    /// <summary>Jumps to <paramref name="value"/> and stops (reduced motion, or a reset).</summary>
    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    /// <summary>Advances by <paramref name="seconds"/>; settles exactly on the target once close and slow.</summary>
    public void Step(double seconds)
    {
        if (IsAtRest) return;
        var omega = 2 * Math.PI / Response;
        // A long frame (a hitch, a suspended window) shouldn't fling the spring.
        var remaining = Math.Min(seconds, 1.0 / 20);
        while (remaining > 0)
        {
            var dt = Math.Min(SubStep, remaining);
            var acceleration = -omega * omega * (Value - Target) - 2 * DampingRatio * omega * Velocity;
            Velocity += acceleration * dt; // semi-implicit Euler: stable at these step sizes
            Value += Velocity * dt;
            remaining -= dt;
        }
        if (Math.Abs(Value - Target) < RestDistance && Math.Abs(Velocity) < RestSpeed)
            Snap(Target);
    }
}
