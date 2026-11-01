using HarmonyLib;
using Content.Client.Construction;
using Robust.Shared.Map.Components;
using RobustoClient.Systems.Construction.Blueprints;

namespace RobustoClient.Systems.Construction;

/// <summary>
/// Ensures exported blueprints always capture pipe-layer positions (Primary / Alt1 / Alt2)
/// for connectors, mixers, filters, vents, pumps, and pipes on the grid.
/// </summary>
[HarmonyPatch(typeof(RobustaBlueprintManager), nameof(RobustaBlueprintManager.ExportAreaAsBlueprint))]
public static class BlueprintExportPipeLayerPatch
{
    [HarmonyPostfix]
    public static void Postfix(
        RobustaBlueprintManager __instance,
        EntityUid gridUid,
        Vector2i corner1,
        Vector2i corner2,
        string blueprintName,
        RobustaBlueprint? __result)
    {
        if (__result == null || __result.Items.Count == 0)
            return;

        try
        {
            var entMan = IoCManager.Resolve<IEntityManager>();
            if (!entMan.EntityExists(gridUid) ||
                !entMan.TryGetComponent<MapGridComponent>(gridUid, out var grid) ||
                !entMan.EntitySysManager.TryGetEntitySystem<SharedMapSystem>(out var mapSys) ||
                !entMan.EntitySysManager.TryGetEntitySystem<RobustaConstructionSolver>(out var solver))
            {
                return;
            }

            int minX = Math.Min(corner1.X, corner2.X);
            int maxX = Math.Max(corner1.X, corner2.X);
            int minY = Math.Min(corner1.Y, corner2.Y);
            int maxY = Math.Max(corner1.Y, corner2.Y);

            var matchedItems = new HashSet<BlueprintItemEntry>();
            bool anyModified = false;

            var gridXform = entMan.GetComponent<TransformComponent>(gridUid);
            var childEnumerator = gridXform.ChildEnumerator;
            while (childEnumerator.MoveNext(out var uid))
            {
                if (!entMan.EntityExists(uid) || entMan.Deleted(uid) || entMan.IsClientSide(uid))
                    continue;

                if (entMan.HasComponent<ConstructionGhostComponent>(uid))
                    continue;

                var xform = entMan.GetComponent<TransformComponent>(uid);
                if (!xform.Anchored)
                    continue;

                var meta = entMan.GetComponent<MetaDataComponent>(uid);
                var rawProtoId = meta.EntityPrototype?.ID;
                if (string.IsNullOrEmpty(rawProtoId))
                    continue;

                var tile = mapSys.TileIndicesFor(gridUid, grid, xform.Coordinates);
                if (tile.X < minX || tile.X > maxX || tile.Y < minY || tile.Y > maxY)
                    continue;

                int ox = tile.X - minX;
                int oy = tile.Y - minY;
                var dir = xform.LocalRotation.GetCardinalDir().ToString();

                solver.ResolveExportBlueprintPrototypes(
                    uid,
                    rawProtoId,
                    out var exportedEntityProtoId,
                    out var exportedConstructionProtoId);

                // Only needed if rawProtoId differs from exportedEntityProtoId (e.g. when RobustaBlueprintManager.cs wasn't updated)
                if (string.Equals(rawProtoId, exportedEntityProtoId, StringComparison.Ordinal))
                    continue;

                foreach (var item in __result.Items)
                {
                    if (matchedItems.Contains(item))
                        continue;

                    if (item.OffsetX != ox ||
                        item.OffsetY != oy ||
                        !string.Equals(item.Direction, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.Equals(item.EntityPrototypeId, rawProtoId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matchedItems.Add(item);
                    item.EntityPrototypeId = exportedEntityProtoId;
                    if (!string.IsNullOrEmpty(exportedConstructionProtoId))
                        item.ConstructionPrototypeId = exportedConstructionProtoId;
                    anyModified = true;
                    break;
                }
            }

            if (anyModified)
            {
                __instance.SaveBlueprint(__result, blueprintName);
            }
        }
        catch
        {
            // Ignore patch errors so blueprint export never throws
        }
    }
}
