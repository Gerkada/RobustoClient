using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using HarmonyLib;
using RobustoClient.Patches;
using RobustoClient.Systems.AutoChem;
using RobustoClient.Systems.Construction;
using RobustoClient.Systems.Hacking;
using Xunit;

namespace RobustoClient.Tests.Patches;

/// <summary>
/// Both self-display patches hang off upstream members by name, so an upstream rename would put the
/// health bar and status icons back on the local player without any error. These assert the targets
/// still resolve and keep the shapes the prefixes and postfixes assume.
/// </summary>
public class LocalPlayerOverlayFilterTests
{
    [Fact]
    public void HealthBarPatchTarget_Resolves()
    {
        var target = SingleTarget(typeof(SelfHealthBarPatch));

        target.Name.Should().Be("CalcProgress");
        // The prefix fakes a null result, which is what makes the draw loop skip the entity.
        ((MethodInfo)target).ReturnType.Should().Be(typeof((float, bool)?));
        target.GetParameters().Should().Contain(p => p.Name == "uid");
        AssertAboveInlineThreshold(target);
    }

    [Fact]
    public void StatusIconPatchTarget_Resolves()
    {
        var target = SingleTarget(typeof(SelfStatusIconPatch));

        target.Name.Should().Be("GetStatusIcons");
        // The postfix clears the returned list in place, so the return type must be a mutable list.
        ((MethodInfo)target).ReturnType.Should().Be(typeof(List<Content.Shared.StatusIcon.StatusIconData>));
        target.GetParameters().Should().Contain(p => p.Name == "uid");
        AssertAboveInlineThreshold(target);
    }

    [Fact]
    public void OverlaysPatchTargets_ResolveAndExceedInlineThreshold()
    {
        var targetMethods = ((IEnumerable)AccessTools.Method(typeof(OverlaysPatch), "TargetMethods")!.Invoke(null, null)!)
            .Cast<MethodBase>()
            .ToArray();

        targetMethods.Should().HaveCount(5, "all five suppressed upstream overlays must resolve");
        foreach (var target in targetMethods)
        {
            target.Name.Should().Be("BeforeDraw");
            AssertAboveInlineThreshold(target);
        }
    }

    private static void AssertAboveInlineThreshold(MethodBase target)
    {
        var il = target.GetMethodBody()?.GetILAsByteArray();
        il.Should().NotBeNull();
        il!.Length.Should().BeGreaterThan(32, $"{target.DeclaringType?.Name}.{target.Name} must stay above RyuJIT's 32B inline threshold");
    }

    private static MethodBase SingleTarget(Type patchType)
    {
        var targets = (IEnumerable)AccessTools.Method(patchType, "TargetMethods")!.Invoke(null, null)!;
        return targets.Cast<MethodBase>().Should().ContainSingle().Subject;
    }
}

/// <summary>
/// The solvers are used from headless tests and from the client, so a field that pulls in a
/// client-only assembly would make them uninstantiable without a running client.
/// </summary>
public class SolverAssemblyBoundaryTests
{
    [Theory]
    [InlineData(typeof(RobustaRecipeSolver))]
    [InlineData(typeof(RobustaWiresSolver))]
    [InlineData(typeof(RobustaConstructionSolver))]
    public void SolverFields_DoNotReferenceClientOnlyAssemblies(Type solverType)
    {
        var fields = solverType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var field in fields)
        {
            var asmName = field.FieldType.Assembly.GetName().Name ?? string.Empty;
            asmName.Should().NotStartWith("Robust.Client", $"field {solverType.Name}.{field.Name} prevents headless instantiation");
            asmName.Should().NotStartWith("Content.Client", $"field {solverType.Name}.{field.Name} prevents headless instantiation");
        }
    }
}

