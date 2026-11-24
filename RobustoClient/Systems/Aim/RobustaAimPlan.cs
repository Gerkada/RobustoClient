using System.Numerics;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.Aim;

/// <summary>
/// Everything the intercept solver needs to know about the weapon, resolved from prototypes rather than
/// guessed from component or entity names.
/// </summary>
/// <param name="Speed">Projectile speed in m/s. Non-positive means "no lead" (hitscan or prediction off).</param>
/// <param name="Hitscan">True when the ammo resolves as a hitscan, which has no travel time.</param>
/// <param name="Mask">Collision mask the shot actually collides with, taken from the ammo prototype.</param>
/// <param name="MaxDistance">Hitscan range, zero for projectiles.</param>
public readonly record struct AimProfile(float Speed, bool Hitscan, int Mask, float MaxDistance);

/// <summary>The solved aim point for one shot.</summary>
/// <param name="Target">Entity the shot is aimed at and should name in <c>RequestShootEvent.Target</c>.</param>
/// <param name="LocalOffset">Aim point in the target's local frame, ready for <c>EntityCoordinates</c>.</param>
/// <param name="WorldPoint">The same point in map space, for overlays and diagnostics.</param>
/// <param name="FlightTime">Projectile travel time, zero for hitscan.</param>
/// <param name="LeadSeconds">Network lead applied to the offset. Always zero for gun shots.</param>
/// <param name="Damping">Lead scale applied for jittery targets, 0..1.</param>
/// <param name="Blocked">True when no sample of the body was reachable from the shooter.</param>
/// <param name="Blocker">First entity the shot would hit instead, when <paramref name="Blocked"/>.</param>
public readonly record struct ShotPlan(
    EntityUid Target,
    Vector2 LocalOffset,
    Vector2 WorldPoint,
    float FlightTime,
    float LeadSeconds,
    float Damping,
    bool Blocked,
    EntityUid? Blocker);

/// <summary>Placement for a thrown slip item, in the target's heading frame.</summary>
public readonly record struct SlipLeadPlan(float Lead, bool ReadyInTime);

/// <summary>Outcome of a slip-item throw placement.</summary>
public enum SlipPlanStatus
{
    NoTarget,
    Placed,
    Underfoot,
    Late,
    OutOfRange,
    Blocked,
}
