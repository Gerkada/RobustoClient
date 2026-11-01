using System.Collections;
using System.Numerics;
using System.Reflection;
using Content.Shared.DoAfter;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    private bool IsPlayerInActiveDoAfter(EntityUid player)
    {
        if (!TryComp<DoAfterComponent>(player, out var doAfterComp))
            return false;

        if (GetMemberValue(doAfterComp, "DoAfters") is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var d = entry.Value;
                if (d == null)
                    continue;

                bool cancelled = GetBoolMember(d, "Cancelled");
                bool completed = GetBoolMember(d, "Completed");
                if (cancelled && _sawDoAfterForStep)
                {
                    _doAfterCancelledForStep = true;
                }

                if (!cancelled && !completed)
                    return true;
            }
        }

        return false;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        var type = obj.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        var prop = type.GetProperty(name, flags);
        if (prop != null)
            return prop.GetValue(obj);

        var field = type.GetField(name, flags) ?? type.GetField("_" + name, flags);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }

    private IComponent? FindComponentByName(EntityUid uid, string componentTypeName)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, componentTypeName, StringComparison.OrdinalIgnoreCase))
                return comp;
        }
        return null;
    }

    private static string? GetStringMember(object? obj, string name)
    {
        var val = GetMemberValue(obj, name);
        if (val == null)
            return null;
        if (val is string s)
            return s;
        if (GetMemberValue(val, "Id") is string idStr)
            return idStr;
        return val.ToString();
    }

    private static bool GetBoolMember(object? obj, string name)
        => GetMemberValue(obj, name) is true;

    private static bool TryGetStepTargetPipeLayer(ConstructionActionStep step, out int targetPipeLayer)
    {
        targetPipeLayer = 0;
        if (step.Kind != ConstructionActionKind.UseTool)
            return false;

        if (step.TargetPipeLayer is { } layer)
        {
            targetPipeLayer = layer;
            return true;
        }

        if (!step.Description.StartsWith("Set pipe layer", StringComparison.OrdinalIgnoreCase))
            return false;

        if (step.Description.Contains("Alt1", StringComparison.OrdinalIgnoreCase) ||
            step.Description.Contains("Secondary", StringComparison.OrdinalIgnoreCase))
        {
            targetPipeLayer = 1;
            return true;
        }

        if (step.Description.Contains("Alt2", StringComparison.OrdinalIgnoreCase) ||
            step.Description.Contains("Tertiary", StringComparison.OrdinalIgnoreCase))
        {
            targetPipeLayer = 2;
            return true;
        }

        return true;
    }

    public bool IsPlayerMoving(EntityUid player)
    {
        if (!Exists(player))
            return false;

        var curMapCoords = _transform.GetMapCoordinates(player);
        bool movedPosition = _lastPlayerMapCoords.MapId == curMapCoords.MapId &&
                             (curMapCoords.Position - _lastPlayerMapCoords.Position).LengthSquared() > 0.00015f;
        _lastPlayerMapCoords = curMapCoords;

        // 1. Check InputMoverComponent held movement keys (WASD / Walk)
        var moverComp = FindComponentByName(player, "InputMoverComponent");
        if (moverComp != null)
        {
            var buttonsObj = GetMemberValue(moverComp, "HeldMoveButtons") ?? GetMemberValue(moverComp, "_heldMoveButtons");
            if (buttonsObj is Enum eVal)
            {
                // MoveButtons: None = 0, Up = 1, Down = 2, Left = 4, Right = 8 (directional bits 1..15)
                int bits = Convert.ToInt32(eVal) & 0xF;
                if (bits != 0)
                    return true;
            }
        }

        // 2. Check PhysicsComponent linear velocity
        var physicsComp = FindComponentByName(player, "PhysicsComponent");
        if (physicsComp != null && GetMemberValue(physicsComp, "LinearVelocity") is Vector2 vel)
        {
            if (vel.LengthSquared() > 0.0025f)
                return true;
        }

        return movedPosition;
    }
}
