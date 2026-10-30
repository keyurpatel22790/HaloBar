namespace HaloBar.UI;

/// <summary>
/// A damped spring that moves <see cref="Value"/> toward <see cref="Target"/>. Retargeting
/// mid-flight keeps the velocity, which is what makes the island's size changes feel physical.
/// </summary>
internal struct Spring(double value, double stiffness = 340, double dampingRatio = 0.86)
{
    public double Value = value;
    public double Target = value;
    public double Velocity;
    private readonly double _stiffness = stiffness;
    private readonly double _damping = 2 * Math.Sqrt(stiffness) * dampingRatio;

    public readonly bool Settled => Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.05;

    public void Step(double dt)
    {
        // Fixed small sub-steps keep the integration stable when a frame comes late.
        const double maxStep = 1 / 240.0;
        dt = Math.Min(dt, 0.1);
        while (dt > 0)
        {
            var h = Math.Min(dt, maxStep);
            // Reduce motion: quicker and at least critically damped, so things go straight to their
            // place without bouncing past it (less movement, not slower movement).
            var stiffness = Prefs.NoBounce ? _stiffness * 1.8 : _stiffness;
            var damping = Prefs.NoBounce ? Math.Max(_damping * Math.Sqrt(1.8), 2 * Math.Sqrt(stiffness)) : _damping;
            var accel = stiffness * (Target - Value) - damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (Settled)
        {
            Value = Target;
            Velocity = 0;
        }
    }

    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    public static implicit operator float(Spring s) => (float)s.Value;
}
