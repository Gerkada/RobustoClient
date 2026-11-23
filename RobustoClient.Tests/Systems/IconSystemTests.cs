using System;
using System.Reflection;
using FluentAssertions;
using RobustoClient.Systems;
using Xunit;

namespace RobustoClient.Tests.Systems;

/// <summary>
/// The mindshield badge is a <c>layer: Mod</c> icon in the job-icon slot, so it outlines the job icon
/// rather than standing beside it. It therefore has to follow the Job Icons switch.
/// </summary>
public class IconSystemTests
{
    [Theory]
    [InlineData(typeof(RobustaJobIconsSystem))]
    [InlineData(typeof(RobustaMindShieldIcons))]
    public void IconSystems_GateOnTheJobIconsSwitch(Type system)
    {
        var onAttached = system.GetMethod("OnAttached", BindingFlags.NonPublic | BindingFlags.Instance);

        onAttached.Should().NotBeNull();
        onAttached!.DeclaringType.Should().Be(system,
            $"{system.Name} must override OnAttached, or the base adds its component unconditionally");
    }

    [Theory]
    [InlineData(typeof(RobustaJobIconsSystem))]
    [InlineData(typeof(RobustaMindShieldIcons))]
    public void IconSystems_ExposeSetEnabled(Type system)
    {
        system.GetMethod("SetEnabled", [typeof(bool)]).Should().NotBeNull();
    }
}
