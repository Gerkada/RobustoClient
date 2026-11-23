using System;
using System.Numerics;
using FluentAssertions;
using RobustoClient.Systems.Aim;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Xunit;

namespace RobustoClient.Tests.Systems.Aim;

public class RobustaAimSolverTests
{
    private const float BulletSpeed = 40f;

    [Fact]
    public void StationaryTarget_NeedsNoLead()
    {
        var solved = RobustaAimSolver.TrySolveFlightTime(new Vector2(10f, 0f), Vector2.Zero, BulletSpeed, out var flightTime);

        solved.Should().BeTrue();
        flightTime.Should().BeApproximately(0.25f, 0.001f);
        RobustaAimSolver.LeadOffset(Vector2.Zero, flightTime, 1f).Should().Be(Vector2.Zero);
    }

    [Fact]
    public void PerpendicularStrafe_LeadPointMatchesTheRealIntercept()
    {
        var targetPos = new Vector2(10f, 0f);
        var targetVel = new Vector2(0f, 4f);

        RobustaAimSolver.TrySolveFlightTime(targetPos, targetVel, BulletSpeed, out var flightTime)
            .Should().BeTrue();

        var lead = RobustaAimSolver.LeadOffset(targetVel, flightTime, 1f);
        var aimPoint = targetPos + lead;

        // Where the bullet actually is once it has flown the solved time.
        var direction = Vector2.Normalize(aimPoint);
        var bulletPos = direction * BulletSpeed * flightTime;

        // Where the target actually is after the same time.
        var targetAtImpact = targetPos + targetVel * flightTime;

        (bulletPos - targetAtImpact).Length().Should().BeLessThan(0.01f);
    }

    [Fact]
    public void TargetMovingTowardsShooter_StillIntercepts()
    {
        var targetPos = new Vector2(10f, 0f);
        var targetVel = new Vector2(-3f, 0f);

        RobustaAimSolver.TrySolveFlightTime(targetPos, targetVel, BulletSpeed, out var flightTime)
            .Should().BeTrue();

        var aimPoint = targetPos + RobustaAimSolver.LeadOffset(targetVel, flightTime, 1f);
        var bulletPos = Vector2.Normalize(aimPoint) * BulletSpeed * flightTime;

        (bulletPos - (targetPos + targetVel * flightTime)).Length().Should().BeLessThan(0.01f);
    }

    [Fact]
    public void TargetOutrunningTheProjectile_FallsBackToStraightLineTime()
    {
        var solved = RobustaAimSolver.TrySolveFlightTime(
            new Vector2(10f, 0f),
            new Vector2(60f, 0f),
            BulletSpeed,
            out var flightTime);

        solved.Should().BeFalse();
        flightTime.Should().BeApproximately(10f / BulletSpeed, 0.001f);
        float.IsFinite(flightTime).Should().BeTrue();
    }

    [Fact]
    public void ZeroSpeed_ProducesNoFlightTime()
    {
        RobustaAimSolver.TrySolveFlightTime(new Vector2(10f, 0f), new Vector2(0f, 4f), 0f, out var flightTime)
            .Should().BeTrue();

        flightTime.Should().Be(0f);
    }

    [Fact]
    public void LeadOffset_ScalesWithDamping()
    {
        var relVel = new Vector2(0f, 4f);

        RobustaAimSolver.LeadOffset(relVel, 0.25f, 1f).Y.Should().BeApproximately(1f, 0.001f);
        RobustaAimSolver.LeadOffset(relVel, 0.25f, 0.5f).Y.Should().BeApproximately(0.5f, 0.001f);
        RobustaAimSolver.LeadOffset(relVel, 0.25f, 0f).Should().Be(Vector2.Zero);
    }

    [Fact]
    public void ToLocalOffset_RoundTripsThroughTheTargetRotation()
    {
        var worldOffset = new Vector2(1.5f, -0.75f);
        var targetRot = new Angle(MathF.PI / 2f);

        var local = RobustaAimSolver.ToLocalOffset(worldOffset, targetRot);
        var recovered = targetRot.RotateVec(local);

        recovered.X.Should().BeApproximately(worldOffset.X, 0.0001f);
        recovered.Y.Should().BeApproximately(worldOffset.Y, 0.0001f);
    }

    [Fact]
    public void LeadDamping_KeepsFullLeadForAStraightRunner()
    {
        var damping = 1f;
        var velocity = new Vector2(0f, 4f);

        for (var i = 0; i < 20; i++)
            damping = RobustaAimSolver.LeadDamping(velocity, velocity, damping);

        damping.Should().BeApproximately(1f, 0.01f);
    }

    [Fact]
    public void LeadDamping_DropsForATargetThatReverses()
    {
        var damping = 1f;
        var velocity = new Vector2(0f, 4f);

        for (var i = 0; i < 20; i++)
        {
            var reversed = -velocity;
            damping = RobustaAimSolver.LeadDamping(reversed, velocity, damping);
            velocity = reversed;
        }

        damping.Should().BeLessThan(0.3f);
    }

    [Fact]
    public void FirstVisibleSample_PrefersTheCentreAndSkipsOccludedSamples()
    {
        Span<Vector2> samples = stackalloc Vector2[3];
        samples[0] = Vector2.Zero;
        samples[1] = new Vector2(0f, 0.2f);
        samples[2] = new Vector2(0f, -0.2f);

        RobustaAimSolver.FirstVisibleSample(samples, _ => true).Should().Be(0);

        RobustaAimSolver.FirstVisibleSample(samples, sample => sample.Y > 0f).Should().Be(1);

        RobustaAimSolver.FirstVisibleSample(samples, _ => false).Should().Be(-1);
    }

    [Fact]
    public void TryBuildRay_ProducesAUnitDirectionAndTheSegmentLength()
    {
        var from = new Vector2(1f, 2f);
        var to = new Vector2(9f, 6f);

        RobustaAimSolver.TryBuildRay(from, to, 66, out var ray, out var length)
            .Should().BeTrue();

        // A scaled direction silently disables the raycast's maxLength cutoff, which was what made
        // geometry behind the target count as a blocker.
        ray.Direction.Length().Should().BeApproximately(1f, 0.0001f);
        length.Should().BeApproximately(Vector2.Distance(from, to), 0.0001f);
        ray.CollisionMask.Should().Be(66);
    }

    [Fact]
    public void TryBuildRay_RejectsDegenerateSegments()
    {
        RobustaAimSolver.TryBuildRay(Vector2.One, Vector2.One, 66, out _, out _).Should().BeFalse();
    }

    // --- Slip placement ---

    private const float ThrowSpeed = 11f;
    private const float ThrowRange = 8f;
    private const float TriggerSpeed = 3.5f;
    private const float MinLead = 1f;

    private static float Solve(Vector2 shooter, Vector2 target, Vector2 velocity)
        => RobustaAimSolver.SolveSlipLead(
            shooter, target, velocity, ThrowSpeed, ThrowRange, 0f, TriggerSpeed, MinLead);

    /// <summary>The inequality the solver must satisfy: the item is slippery before the target arrives.</summary>
    private static bool IsReadyInTime(Vector2 shooter, Vector2 target, Vector2 velocity, float lead, float netLead)
    {
        var distance = (target + Vector2.Normalize(velocity) * lead - shooter).Length();
        var readyAt = RobustaAimSolver.SlipReadyFraction * distance / ThrowSpeed + netLead;
        var arriveAt = lead / velocity.Length();
        return readyAt <= arriveAt + 0.0001f;
    }

    [Fact]
    public void SlipLead_PlacesAheadOfARunnerAndIsReadyInTime()
    {
        var shooter = Vector2.Zero;
        var target = new Vector2(5f, 0f);
        var velocity = new Vector2(0f, 4f);

        var lead = Solve(shooter, target, velocity);

        lead.Should().BeGreaterThan(0f);
        lead.Should().BeLessThan(ThrowRange);
        IsReadyInTime(shooter, target, velocity, lead, 0f).Should().BeTrue();
    }

    [Fact]
    public void SlipLead_IsTheSmallestValidLead()
    {
        var shooter = Vector2.Zero;
        var target = new Vector2(5f, 0f);
        var velocity = new Vector2(0f, 4f);

        var lead = Solve(shooter, target, velocity);

        // Slightly closer is too late: the peel would still be airborne when they walk over the spot.
        IsReadyInTime(shooter, target, velocity, lead - 0.05f, 0f).Should().BeFalse();
        IsReadyInTime(shooter, target, velocity, lead, 0f).Should().BeTrue();
    }

    [Fact]
    public void SlipLead_WalkingTarget_IsStillPlacedAhead()
    {
        // Walking is 2.5 m/s, under the 3.5 m/s step-trigger threshold. Such a target cannot slip on this
        // pass, but it still has a heading, so the item belongs in their path rather than under their feet.
        var shooter = Vector2.Zero;
        var target = new Vector2(5f, 0f);
        var velocity = new Vector2(0f, 2.5f);

        var lead = Solve(shooter, target, velocity);

        lead.Should().BeApproximately(MinLead, 0.001f);
        var point = target + Vector2.Normalize(velocity) * lead;
        point.Y.Should().BeGreaterThan(target.Y, "the item must land in front of them, not where they were");
    }

    [Fact]
    public void SlipLead_StationaryTarget_GetsTheItemUnderfoot()
    {
        Solve(Vector2.Zero, new Vector2(5f, 0f), Vector2.Zero).Should().Be(0f);
    }

    [Fact]
    public void SlipLead_BarelyMovingTarget_CountsAsStationary()
    {
        // Below the heading epsilon there is no direction of travel worth leading along.
        Solve(Vector2.Zero, new Vector2(5f, 0f), new Vector2(0f, 0.2f)).Should().Be(0f);
    }

    [Fact]
    public void SlipLead_UnreachableTarget_ReportsNoPlacement()
    {
        // Far away and fast enough that even maximum range lands after they have gone past.
        Solve(Vector2.Zero, new Vector2(7.9f, 0f), new Vector2(0f, 40f)).Should().BeLessThan(0f);
    }

    [Fact]
    public void SlipLead_TargetBeyondThrowRange_ReportsNoPlacement()
    {
        Solve(Vector2.Zero, new Vector2(10f, 0f), new Vector2(0f, 4f)).Should().BeLessThan(0f);
    }

    [Fact]
    public void SlipLead_RecedingTarget_ThrowDistanceStaysInRange()
    {
        // The regression this guards: bounding the search by the lead instead of the throw distance let a
        // receding target ask for a throw past range, which the server clamps short and lands behind them.
        var shooter = Vector2.Zero;
        var target = new Vector2(5f, 0f);
        var velocity = new Vector2(4f, 0f);

        var lead = Solve(shooter, target, velocity);

        lead.Should().BeGreaterThanOrEqualTo(0f);
        var distance = (target + Vector2.Normalize(velocity) * lead - shooter).Length();
        distance.Should().BeLessThanOrEqualTo(ThrowRange + 0.001f);
    }

    [Fact]
    public void SlipLead_RecedingTargetOutOfReach_ReportsNoPlacement()
    {
        // Seven metres away and running: no in-range throw can land before they pass the spot, so the
        // honest answer is "cannot place" rather than a point the server would clamp short.
        Solve(Vector2.Zero, new Vector2(7f, 0f), new Vector2(4f, 0f)).Should().BeLessThan(0f);
    }

    [Fact]
    public void SlipLead_NeverExceedsThrowRange()
    {
        var lead = Solve(Vector2.Zero, new Vector2(7f, 0f), new Vector2(0f, 6f));

        lead.Should().BeLessThanOrEqualTo(ThrowRange);

        var distance = (new Vector2(7f, 0f) + new Vector2(0f, lead) - Vector2.Zero).Length();
        distance.Should().BeLessThanOrEqualTo(ThrowRange + 0.001f);
    }

    [Fact]
    public void SlipLead_NetworkDelayRequiresMoreLead()
    {
        var shooter = Vector2.Zero;
        var target = new Vector2(5f, 0f);
        var velocity = new Vector2(0f, 4f);

        var noLag = Solve(shooter, target, velocity);
        var withLag = RobustaAimSolver.SolveSlipLead(
            shooter, target, velocity, ThrowSpeed, ThrowRange, 0.1f, TriggerSpeed, MinLead);

        withLag.Should().BeGreaterThanOrEqualTo(noLag);
    }

    [Fact]
    public void SlipLead_TargetWalkingTowardsTheShooter_PlacesCloserThanAwayFromIt()
    {
        var shooter = Vector2.Zero;
        var target = new Vector2(5f, 0f);

        var approaching = Solve(shooter, target, new Vector2(-4f, 0f));
        var receding = Solve(shooter, target, new Vector2(4f, 0f));

        // A target walking at the thrower needs less lead than one walking away, because the peel and the
        // target close the distance from opposite ends.
        approaching.Should().BeLessThanOrEqualTo(receding);
    }

    [Fact]
    public void SlipLead_AcrossDistancesAndSpeeds_StaysInRangeAndAhead()
    {
        float[] distances = [2f, 4f, 6f, 7.5f];
        float[] speeds = [0f, 0.2f, 2.5f, 3.4f, 3.6f, 4.5f, 6f];

        foreach (var distance in distances)
        {
            foreach (var speed in speeds)
            {
                var shooter = Vector2.Zero;
                var target = new Vector2(distance, 0f);
                var velocity = new Vector2(0f, speed);

                var lead = Solve(shooter, target, velocity);

                if (lead < 0f)
                    continue;

                var point = speed > 0f
                    ? target + Vector2.Normalize(velocity) * lead
                    : target;

                (point - shooter).Length().Should().BeLessThanOrEqualTo(
                    ThrowRange + 0.001f,
                    $"distance {distance}, speed {speed} must stay inside throw range");

                point.Y.Should().BeGreaterThanOrEqualTo(
                    target.Y - 0.001f,
                    $"distance {distance}, speed {speed} must never place the item behind the target");
            }
        }
    }

    [Fact]
    public void WithinReach_AcceptsInsideAndBoundary()
    {
        var origin = new MapCoordinates(Vector2.Zero, new MapId(1));

        RobustaAimSolver.WithinReach(new MapCoordinates(new Vector2(3f, 4f), new MapId(1)), origin, 5f)
            .Should().BeTrue("a point exactly at the limit is still reachable");
        RobustaAimSolver.WithinReach(new MapCoordinates(new Vector2(1f, 1f), new MapId(1)), origin, 5f)
            .Should().BeTrue();
    }

    [Fact]
    public void WithinReach_RejectsOutsideAndOtherMaps()
    {
        var origin = new MapCoordinates(Vector2.Zero, new MapId(1));

        RobustaAimSolver.WithinReach(new MapCoordinates(new Vector2(5.01f, 0f), new MapId(1)), origin, 5f)
            .Should().BeFalse();
        RobustaAimSolver.WithinReach(new MapCoordinates(Vector2.Zero, new MapId(2)), origin, 5f)
            .Should().BeFalse("a point on another map is never in reach");
    }
}
