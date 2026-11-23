using System;
using FluentAssertions;
using RobustoClient.Systems.Aim;
using Xunit;

namespace RobustoClient.Tests.Systems.Aim;

public class RobustaLeadEstimatorTests
{
    private static readonly TimeSpan TickPeriod = TimeSpan.FromSeconds(1.0 / 30.0);

    private const float MaxLead = 0.5f;

    [Fact]
    public void SteadyLag_ConvergesToTheObservedTicks()
    {
        var estimator = new RobustaLeadEstimator();

        for (var i = 0; i < 60; i++)
            estimator.Sample(3f, starving: false);

        estimator.AppliedTicks.Should().BeApproximately(3f, 0.01f);
        estimator.LeadSeconds(TickPeriod, MaxLead).Should().BeApproximately(0.1f, 0.005f);
    }

    [Fact]
    public void LagSpike_RaisesTheEstimateAboveTheMean()
    {
        var estimator = new RobustaLeadEstimator();

        for (var i = 0; i < 40; i++)
            estimator.Sample(3f, starving: false);

        var before = estimator.AppliedTicks;
        estimator.Sample(20f, starving: false);

        estimator.AppliedTicks.Should().BeGreaterThan(before);
        estimator.AppliedTicks.Should().BeLessThan(20f);
    }

    [Fact]
    public void BufferStarvation_BiasesTowardThePeak()
    {
        var estimator = new RobustaLeadEstimator();

        for (var i = 0; i < 40; i++)
            estimator.Sample(3f, starving: false);

        estimator.Sample(20f, starving: true);

        // The spike must be treated as the real skew, not as noise to be averaged away.
        estimator.AppliedTicks.Should().BeApproximately(20f, 0.01f);
    }

    [Fact]
    public void Ceiling_ClampsTheAppliedLead()
    {
        var estimator = new RobustaLeadEstimator();

        for (var i = 0; i < 60; i++)
            estimator.Sample(30f, starving: false);

        estimator.LeadSeconds(TickPeriod, MaxLead).Should().Be(MaxLead);
    }

    [Fact]
    public void SubTickChange_DoesNotMoveTheEstimate()
    {
        var estimator = new RobustaLeadEstimator();

        for (var i = 0; i < 40; i++)
            estimator.Sample(4f, starving: false);

        var before = estimator.AppliedTicks;
        estimator.Sample(4.2f, starving: false);

        estimator.AppliedTicks.Should().Be(before);
    }

    [Fact]
    public void NegativeOrBrokenSamples_AreIgnored()
    {
        var estimator = new RobustaLeadEstimator();

        estimator.Sample(float.NaN, starving: false);
        estimator.Sample(-5f, starving: false);

        estimator.AppliedTicks.Should().BeGreaterThanOrEqualTo(0f);
        estimator.LeadSeconds(TickPeriod, MaxLead).Should().BeGreaterThanOrEqualTo(0f);
    }

    [Fact]
    public void Reset_ClearsTheEstimate()
    {
        var estimator = new RobustaLeadEstimator();

        for (var i = 0; i < 20; i++)
            estimator.Sample(6f, starving: false);

        estimator.Reset();

        estimator.AppliedTicks.Should().Be(0f);
        estimator.LeadSeconds(TickPeriod, MaxLead).Should().Be(0f);
    }
}
