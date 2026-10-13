namespace YunYun.Core;

/// <summary>
/// How a part follows the window motion. Measured on a 62 s recording of the game.
/// The game does not swing parts like pendulums: each part has a "lean" angle that
/// moves towards a target at a limited speed and stops at a limit:
///
///     target = clamp(Kx * vx + Ky * vy, -Max, +Max)     vx, vy = window velocity, px/s
///     lean  += clamp(target - lean, -Rate * dt, +Rate * dt)
///
/// Rigid parts: values in degrees, the lean IS the rotation.
/// Chains: values in radians, the lean bends the chain; damped springs add a little wobble.
/// </summary>
public sealed record DriveParams(double Kx, double Ky, double Max, double Rate, double Tau);

public sealed record PartParams(
    DriveParams Drive,
    double PeriodRoot = 0.4,      // chains: spring period of a link, s (root -> tip)
    double PeriodTip = 0.3,
    double Zeta = 0.8,            // damping ratio (0 = swings forever, 1 = no overshoot)
    double Inertia = 0.0,         // kick from the window being yanked (the game has none)
    double Follow = 0.0,          // whip from the parent link
    double InputTau = 0.15,       // smoothing of the motion fed to inertia, s
    double[]? MaxBend = null,     // per-joint limit, deg (root -> tip); one value = all joints
    double RootLimit = 30,        // limit for the first link, deg
    bool LeanAtRoot = false)      // distribute the lean towards the root (tail) instead of the tip
{
    public static readonly IReadOnlyDictionary<string, PartParams> Defaults = new Dictionary<string, PartParams>
    {
        ["legs"] = new(new DriveParams(0.377, 0, 7.8, 3.3, 0.05)),
        ["arms"] = new(new DriveParams(0.316, 0, 3.9, 1.8, 0.05)),
        ["wing"] = new(new DriveParams(0.0455, 0, 5.6, 12.9, 0.05)),
        ["hair_r2"] = new(new DriveParams(0.0017, 0, 1.0, 3.0, 0.6), MaxBend: new[] { 16.0 }),
        ["hair_r1"] = new(new DriveParams(0.0017, 0, 1.0, 3.0, 0.6), MaxBend: new[] { 16.0 }),
        ["hair_l2"] = new(new DriveParams(0.0017, 0, 1.0, 3.0, 0.6), MaxBend: new[] { 16.0 }),
        ["tail"] = new(new DriveParams(0.08, 0, 0.65, 0.55, 0.05), LeanAtRoot: true, RootLimit: 40,
                       MaxBend: new[] { 4.0, 4, 5, 6, 8, 12, 12, 10, 4, 3, 3 }),
    };
}
