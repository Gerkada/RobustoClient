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
    /// Fraction of the throw distance a friction-compensated throw spends in the air. The item becomes
    /// steppable at that point, about 78% of the way to the aim point at typical ranges.
    /// </summary>
    public const float SlipReadyFraction = 0.8f;

    /// <summary>
    /// Seconds the item keeps creeping toward its landing point after it becomes steppable.
    /// </summary>
    public const float SlipSettleSeconds = 0.2f;

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
    /// <param name="targetPos">
    /// Target position in map space, corrected for the client's render lag. The lead is measured from here,
    /// so this must be where the target actually is, not where the client draws it.
    /// </param>
    /// <param name="targetVelocity">Target map velocity.</param>
    /// <param name="throwSpeed">Throw speed, from <c>HandsComponent.BaseThrowspeed</c>.</param>
    /// <param name="maxRange">Throw range, from <c>HandsComponent.ThrowRange</c>.</param>
    /// <param name="networkLead">Deadline cost of the message transit, in seconds.</param>
    /// <param name="minTriggerSpeed">Speed the target must exceed for the step trigger to fire.</param>
    /// <param name="minLead">Forward margin in metres, applied to every target with a heading.</param>
    /// <returns>
    /// The lead and whether it beats the deadline. <c>Lead</c> is negative only when the target's path is
    /// unreachable inside <paramref name="maxRange"/> at every lead, which is the one case with no point.
    /// </returns>
    public static SlipLeadPlan SolveSlipLead(
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
            return new SlipLeadPlan(-1f, false);

        var speed = targetVelocity.Length();

        // No direction of travel at all: the item belongs at their feet.
        if (speed < HeadingEpsilon)
            return new SlipLeadPlan(0f, true);

        var heading = targetVelocity / speed;
        var toTarget = targetPos - shooterPos;
        var reachable = ReachableLead(toTarget, heading, maxRange);

        if (reachable <= 0f)
            return new SlipLeadPlan(-1f, false);

        // The margin is a distance along the heading, so it is capped by what the throw can reach.
        var floor = Math.Clamp(minLead, 0f, reachable);

        // Too slow to slip on this pass. No deadline applies, so the item just goes the margin ahead of them.
        if (speed < minTriggerSpeed)
            return new SlipLeadPlan(floor, true);

        float Distance(float lead) => (toTarget + heading * lead).Length();

        // f(lead) = time for the item to settle - time for the target to arrive. Negative means the item is
        // ready first, which is what we want.
        float Slack(float lead) =>
            SlipReadyFraction * Distance(lead) / throwSpeed + SlipSettleSeconds + networkLead - lead / speed;

        // Ready even at the closest allowed point
        if (Slack(floor) <= 0f)
            return new SlipLeadPlan(floor, true);

        // Not ready even at the furthest reachable point. Lead as far as the throw allows and report it,
        // rather than dropping the throw back to the raw cursor.
        if (Slack(reachable) > 0f)
            return new SlipLeadPlan(reachable, false);

        var low = floor;
        var high = reachable;

        for (var i = 0; i < SlipBisectionSteps; i++)
        {
            var mid = (low + high) * 0.5f;
            if (Slack(mid) > 0f)
                low = mid;
            else
                high = mid;
        }

        return new SlipLeadPlan(high, true);
    }

    /// <summary>Furthest lead at which the landing point is still inside the throw range.</summary>
    /// <remarks>
    /// <c>|toTarget + heading * lead|² &lt;= range²</c> is a quadratic in lead. Its valid interval, when one
    /// exists, lies between the roots, so the upper root is the furthest point the server will not clamp
    /// short of.
    /// </remarks>
    private static float ReachableLead(Vector2 toTarget, Vector2 heading, float maxRange)
    {
        var along = Vector2.Dot(toTarget, heading);
        var discriminant = along * along - Vector2.Dot(toTarget, toTarget) + maxRange * maxRange;
        if (discriminant < 0f)
            return 0f;

        return MathF.Max(0f, -along + MathF.Sqrt(discriminant));
    }

}
