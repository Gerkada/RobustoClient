using System.Reflection;
using Content.Client.MouseRotator;
using Content.Client.Weapons.Melee;
using FluentAssertions;
using HarmonyLib;
using Xunit;

namespace RobustoClient.Tests.Patches;

/// <summary>
/// The melee aimbot rewrites the aim point from prefixes on private upstream methods. An upstream rename,
/// or a method small enough for RyuJIT to inline, would put the aimbot back on the raw mouse target with no
/// error, so these pin the targets themselves.
/// </summary>
public class MeleePatchTargetTests
{
    [Theory]
    [InlineData("ClientLightAttack")]
    [InlineData("ClientHeavyAttack")]
    [InlineData("ClientDisarm")]
    public void MeleeAttackTargets_ResolveAndStayPatchable(string name)
    {
        var target = AccessTools.Method(typeof(MeleeWeaponSystem), name);

        target.Should().NotBeNull($"{name} is the melee aimbot patch target");
        AssertAboveInlineThreshold(target!);
    }

    [Fact]
    public void MouseRotatorTarget_ResolvesAndStaysPatchable()
    {
        var target = AccessTools.Method(typeof(MouseRotatorSystem), nameof(MouseRotatorSystem.Update));

        target.Should().NotBeNull("MouseRotatorSystem.Update is the rotation patch target");
        AssertAboveInlineThreshold(target!);
    }

    private static void AssertAboveInlineThreshold(MethodBase target)
    {
        var il = target.GetMethodBody()?.GetILAsByteArray();
        il.Should().NotBeNull();
        il!.Length.Should().BeGreaterThan(32, $"{target.DeclaringType?.Name}.{target.Name} must stay above RyuJIT's 32B inline threshold");
    }
}
