namespace BendWin.Core;

internal static class BendMath
{
    // Converts lid angle to a 0-1 bend progress using smoothstep.
    // clearAngle: angle at which the desktop is fully clear (default 110°)
    // angle: current lid angle in degrees
    public static float Progress(float angle, float clearAngle)
    {
        float t = Math.Clamp((clearAngle - angle) / Math.Max(1f, clearAngle - 12f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Exponential damping toward target — spring-like feel with 75ms time constant.
    public static float Damp(float current, float target, float dt)
    {
        float clampedDt = Math.Min(dt, 0.1f);
        return current + (target - current) * (1f - MathF.Exp(-clampedDt / 0.075f));
    }
}
