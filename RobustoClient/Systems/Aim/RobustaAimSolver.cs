using System;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace RobustoClient.Systems.Aim;

/// <summary>
/// Intercept maths for a straight-line projectile in 2D map space.
/// </summary>
internal static class RobustaAimSolver
{
    /// <summary>Speeds at or below this are treated as hitscan (no travel time).</summary>
    private const float HitscanSpeedThreshold = 0.01f;

    /// <summary>Segments shorter than this are treated as "no ray to cast".</summary>
    private const float MinRayLength = 0.01f;

    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Solves for the time a projectile needs to reach the target, given relative position and velocity.
    /// </summary>
    /// <returns>
    /// True when a real intercept exists. False when the target outruns the projectile, in which case
    /// <paramref name="flightTime"/> still holds the straight-line estimate so the caller can aim somewhere
    /// sensible.
    /// </returns>
    public static bool TrySolveFlightTime(Vector2 relPos, Vector2 relVel, float speed, out float flightTime)
    {
        flightTime = 0f;

        if (speed <= HitscanSpeedThreshold || relPos.LengthSquared() <= Epsilon)
            return true;

        var a = Vector2.Dot(relVel, relVel) - speed * speed;
        var b = 2f * Vector2.Dot(relVel, relPos);
        var c = Vector2.Dot(relPos, relPos);

        if (MathF.Abs(a) > Epsilon)
        {
            var discriminant = b * b - 4f * a * c;
            if (discriminant >= 0f)
            {
                var root = MathF.Sqrt(discriminant);
                var t1 = (-b - root) / (2f * a);
                var t2 = (-b + root) / (2f * a);

                if (t1 > 0f && t2 > 0f)
                {
                    flightTime = MathF.Min(t1, t2);
                    return true;
                }

                if (t1 > 0f)
                {
                    flightTime = t1;
                    return true;
                }

                if (t2 > 0f)
                {
                    flightTime = t2;
                    return true;
                }
            }
        }
        else if (MathF.Abs(b) > Epsilon)
        {
            var t = -c / b;
            if (t > 0f)
            {
                flightTime = t;
                return true;
            }
        }

        flightTime = relPos.Length() / speed;
        return false;
    }

    /// <summary>Target-relative lead vector for the given relative velocity and flight time.</summary>
    public static Vector2 LeadOffset(Vector2 relVel, float flightTime, float damping)
        => relVel * (flightTime * damping);

    /// <summary>
    /// Converts a map-space offset into the target's local frame, so it survives the target's world rotation.
    /// </summary>
    public static Vector2 ToLocalOffset(Vector2 worldOffset, Angle parentWorldRotation)
        => new Angle(-parentWorldRotation.Theta).RotateVec(worldOffset);

    /// <summary>
    /// Blends the instant direction agreement between two velocity samples into a 0..1 lead scale.
    /// A target running in a straight line keeps full lead; a target reversing loses most of it, which
    /// matches where its average future position actually is.
    /// </summary>
    public static float LeadDamping(Vector2 velocity, Vector2 previous, float previousDamping)
    {
        var lengths = velocity.Length() * previous.Length();
        if (lengths < 0.01f)
            return previousDamping;

        var cos = Math.Clamp(Vector2.Dot(velocity, previous) / lengths, -1f, 1f);
        return Math.Clamp(previousDamping * 0.7f + cos * 0.3f, 0f, 1f);
    }

    /// <summary>Index of the first sample the predicate accepts, or -1 when none are visible.</summary>
    public static int FirstVisibleSample(ReadOnlySpan<Vector2> samples, Func<Vector2, bool> isVisible)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (isVisible(samples[i]))
                return i;
        }

        return -1;
    }

    /// <summary>Whether a map point lies inside a reach limit measured from another map point.</summary>
    public static bool WithinReach(MapCoordinates point, MapCoordinates origin, float range)
        => point.MapId == origin.MapId && (point.Position - origin.Position).LengthSquared() <= range * range;

    /// <summary>
    /// Builds the line-of-sight ray from the shooter to an aim point.
    /// </summary>
    /// <remarks>
    /// The direction must be unit length. The physics raycast measures hit distance along it and drops
    /// everything past <c>maxLength</c> by comparing that distance, so a scaled direction silently disables
    /// the cutoff and geometry beyond the target starts counting as a blocker.
    /// </remarks>
    public static bool TryBuildRay(Vector2 from, Vector2 to, int mask, out CollisionRay ray, out float length)
    {
        ray = default;

        var delta = to - from;
        length = delta.Length();
        if (length <= MinRayLength)
            return false;

        ray = new CollisionRay(from, delta / length, mask);
        return true;
    }

    /// <summary>
    /// Fraction of the throw distance a friction-compensated throw spends in the air.
    /// </summary>
    public const float SlipReadyFraction = 0.8f;

    /// <summary>Bisection steps used to find the slip lead. Twelve is sub-millimetre over an 8 m range.</summary>
    private const int SlipBisectionSteps = 12;

    /// <summary>
    /// Speed below which a target is treated as having no direction of travel at all.
    /// </summary>
    private const float HeadingEpsilon = 0.5f;

    /// <summary>
    /// Distance ahead of the target to place a thrown slip item.
    /// </summary>
    /// <param name="shooterPos">Thrower position in map space.</param>
    /// <param name="targetPos">Target position in map space.</param>
    /// <param name="targetVelocity">Target map velocity.</param>
    /// <param name="throwSpeed">Throw speed, from <c>HandsComponent.BaseThrowspeed</c>.</param>
    /// <param name="maxRange">Throw range, from <c>HandsComponent.ThrowRange</c>.</param>
    /// <param name="networkLead">Measured one-way delay, since the point is anchored to the world.</param>
    /// <param name="minTriggerSpeed">Speed the target must exceed for the step trigger to fire.</param>
    /// <param name="minLead">Minimum lead used when the target cannot slip on this pass.</param>
    /// <returns>
    /// Lead distance along the target's heading. Zero means drop it under them, which only happens when the
    /// target has no heading at all. Negative means no placement works within range.
    /// </returns>
    public static float SolveSlipLead(
        Vector2 shooterPos,
        Vector2 targetPos,
        Vector2 targetVelocity,
        float throwSpeed,
        float maxRange,
        float networkLead,
        float minTriggerSpeed,
        float minLead)
    {
        if (throwSpeed <= HitscanSpeedThreshold || maxRange <= 0f)
            return -1f;

        var speed = targetVelocity.Length();

        // No direction of travel at all: the item belongs at their feet.
        if (speed < HeadingEpsilon)
            return 0f;

        var heading = targetVelocity / speed;
        var toTarget = targetPos - shooterPos;

        // Throw distance to the placement point for a given lead along the heading.
        float Distance(float lead) => (toTarget + heading * lead).Length();

        // Furthest lead whose throw distance still fits the range. The server clamps the throw direction to
        // ThrowRange, so anything past this lands short of the intended spot.
        var reachable = maxRange;
        if (Distance(maxRange) > maxRange)
        {
            var lowDistance = 0f;
            var highDistance = maxRange;

            for (var i = 0; i < SlipBisectionSteps; i++)
            {
                var mid = (lowDistance + highDistance) * 0.5f;
                if (Distance(mid) <= maxRange)
                    lowDistance = mid;
                else
                    highDistance = mid;
            }

            reachable = lowDistance;
        }

        // Already out of range where they stand, so there is nowhere to put it.
        if (reachable <= 0f)
            return -1f;

        // Too slow to slip on this pass. Lead anyway so the item is in their path once they speed up, but
        // skip the arrival deadline because it does not apply.
        if (speed < minTriggerSpeed)
            return Math.Min(MathF.Max(minLead, 0f), reachable);

        // f(lead) = time for the item to become slippery - time for the target to arrive.
        // Negative means the item is ready first, which is what we want.
        float Slack(float lead) => SlipReadyFraction * Distance(lead) / throwSpeed + networkLead - lead / speed;

        // Already ready at zero lead: drop it on their feet and let them walk into it.
        if (Slack(0f) <= 0f)
            return 0f;

        // Not ready even at the furthest reachable point: no placement works.
        if (Slack(reachable) > 0f)
            return -1f;

        var low = 0f;
        var high = reachable;

        for (var i = 0; i < SlipBisectionSteps; i++)
        {
            var mid = (low + high) * 0.5f;
            if (Slack(mid) > 0f)
                low = mid;
            else
                high = mid;
        }

        return high;
    }
}
