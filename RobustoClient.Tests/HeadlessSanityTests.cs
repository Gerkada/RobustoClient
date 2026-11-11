using System.Numerics;
using System.Reflection;
using FluentAssertions;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using RobustoClient.Patches;
using RobustoClient.Systems.AutoChem;
using RobustoClient.Systems.Construction;
using RobustoClient.Systems.Hacking;
using Xunit;

namespace RobustoClient.Tests;

public class HeadlessSanityTests
{
    [Fact]
    public void RobustPrimitives_CanBeInstantiatedHeadlessly_WithoutIoC()
    {
        var entity = new EntityUid(42);
        var coords = new MapCoordinates(new Vector2(10, 20), new MapId(1));

        entity.Valid.Should().BeTrue();
        entity.Id.Should().Be(42);
        coords.MapId.Should().Be(new MapId(1));
        coords.Position.X.Should().Be(10);
        coords.Position.Y.Should().Be(20);
    }

    [Fact]
    public void RobustoClientDomainTypes_CanBeInstantiatedHeadlessly()
    {
        var plan = new DispensePlan
        {
            TargetReagent = "Water",
            TargetAmount = 30f
        };
        var step = new FindWireStep("Power", "wire_power");

        plan.TargetReagent.Should().Be("Water");
        plan.TargetAmount.Should().Be(30f);
        step.WireName.Should().Be("Power");
        step.Binding.Should().Be("wire_power");
    }

    [Fact]
    public void RobustoClientInternals_AreAccessibleViaInternalsVisibleTo()
    {
        // DrawOcclusionDepthPatch is an internal class in RobustoClient.Patches
        var patchType = typeof(DrawOcclusionDepthPatch);
        patchType.Should().NotBeNull();
        patchType.IsClass.Should().BeTrue();
    }

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
