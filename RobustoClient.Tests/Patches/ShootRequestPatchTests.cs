using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Content.Client.Weapons.Ranged.Systems;
using Content.Shared.Weapons.Ranged.Events;
using FluentAssertions;
using HarmonyLib;
using RobustoClient.Patches;
using Xunit;

namespace RobustoClient.Tests.Patches;

/// <summary>
/// The shoot redirect is injected by IL surgery, so an upstream change to how the request is built would
/// silently stop retargeting instead of failing loudly. These pin the shape the transpiler relies on.
/// </summary>
public class ShootRequestPatchTests
{
    [Fact]
    public void Update_StillStoresShootCoordinates()
    {
        var coordinatesField = AccessTools.Field(typeof(RequestShootEvent), nameof(RequestShootEvent.Coordinates));

        PatchProcessor.GetOriginalInstructions(UpdateMethod())
            .Should().Contain(instruction => instruction.StoresField(coordinatesField));
    }

    [Fact]
    public void Transpiler_InjectsCoordinatePatchAndTargetWrite()
    {
        var transpiler = AccessTools.Method(typeof(GunUpdatePatch), "Transpiler");
        transpiler.Should().NotBeNull();

        var original = PatchProcessor.GetOriginalInstructions(UpdateMethod());
        var patched = ((IEnumerable<CodeInstruction>) transpiler!.Invoke(null, [original])!).ToList();

        var patchIndex = IndexOfCall(patched, typeof(GunUpdatePatch), nameof(GunUpdatePatch.Patch));
        var targetIndex = IndexOfCall(patched, typeof(GunUpdatePatch), nameof(GunUpdatePatch.SetShootTarget));

        patchIndex.Should().BeGreaterThanOrEqualTo(0, "the coordinate redirect must be injected");

        // Layout after injection: Call Patch, stfld Coordinates, Dup, Call SetShootTarget.
        // stfld keeps the event reference on the stack, so Dup is what makes the Target write stack-legal.
        targetIndex.Should().Be(patchIndex + 3, "the target write follows the coordinate redirect and the stfld");
        patched[targetIndex - 1].opcode.Should().Be(OpCodes.Dup);
    }

    private static MethodBase UpdateMethod()
    {
        var update = AccessTools.Method(typeof(GunSystem), nameof(GunSystem.Update));
        update.Should().NotBeNull("GunSystem.Update is the patch target");
        return update!;
    }

    private static int IndexOfCall(List<CodeInstruction> instructions, System.Type owner, string name)
    {
        return instructions.FindIndex(instruction =>
            instruction.operand is MethodInfo method &&
            method.Name == name &&
            method.DeclaringType == owner);
    }
}
