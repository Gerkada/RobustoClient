using Content.Client.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Client.Placement;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction.Blueprints;

public sealed class BlueprintPlacementHijack : PlacementHijack
{
    public static BlueprintPlacementHijack? ActiveInstance { get; private set; }

    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;

    private readonly ConstructionSystem _constructionSystem;
    private readonly SharedMapSystem _mapSystem;
    private readonly RobustaAutoConstructSystem _autoConstruct;

    public RobustaBlueprint Blueprint { get; }
    public BlueprintZPhase? ActivePhaseFilter { get; set; }
    public bool AutoBuildOnPlace { get; set; }

    public override bool CanRotate => true;

    public int RotationSteps => DirectionToSteps(Manager?.Direction ?? Direction.South);

    public BlueprintPlacementHijack(
        RobustaBlueprint blueprint,
        BlueprintZPhase? activePhaseFilter = null,
        bool autoBuildOnPlace = true)
    {
        IoCManager.InjectDependencies(this);

        Blueprint = blueprint;
        ActivePhaseFilter = activePhaseFilter;
        AutoBuildOnPlace = autoBuildOnPlace;

        _constructionSystem = _entMan.System<ConstructionSystem>();
        _mapSystem = _entMan.System<SharedMapSystem>();
        _autoConstruct = _entMan.System<RobustaAutoConstructSystem>();
    }

    public override void StartHijack(PlacementManager manager)
    {
        base.StartHijack(manager);
        ActiveInstance = this;
    }

    public static void ClearActiveInstance()
    {
        ActiveInstance = null;
    }

    public override bool HijackPlacementRequest(EntityCoordinates coordinates)
        => HijackPlacement(coordinates);

    public bool HijackPlacement(EntityCoordinates coordinates)
    {
        var placementDir = Manager?.Direction ?? Direction.South;
        int rotSteps = DirectionToSteps(placementDir);

        if (!_entMan.TryGetComponent<MapGridComponent>(coordinates.EntityId, out var grid))
        {
            var xformSys = _entMan.System<SharedTransformSystem>();
            var gridUid = xformSys.GetGrid(coordinates);
            if (gridUid == null || !_entMan.TryGetComponent(gridUid.Value, out grid))
                return true;

            coordinates = new EntityCoordinates(gridUid.Value, xformSys.ToMapCoordinates(coordinates).Position);
        }

        var targetGridUid = coordinates.EntityId;
        var baseTile = _mapSystem.TileIndicesFor(targetGridUid, grid, coordinates);
        var originTileCoords = _mapSystem.GridTileToLocal(targetGridUid, grid, baseTile);

        if (AutoBuildOnPlace)
        {
            _autoConstruct.StartPhasedBlueprintBuild(
                Blueprint,
                originTileCoords,
                placementDir,
                ActivePhaseFilter);
            return true;
        }

        foreach (var item in GetFilteredAndOrderedItems())
        {
            var (rx, ry) = RotateOffset(item.OffsetX, item.OffsetY, Blueprint.Width, Blueprint.Height, rotSteps);
            var itemDir = ParseDirection(item.Direction);
            var rotatedDir = RotateDirection(itemDir, rotSteps);
            var targetCoords = _mapSystem.GridTileToLocal(targetGridUid, grid, baseTile + new Vector2i(rx, ry));

            var recipeId = item.ConstructionPrototypeId;
            if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
                recipeId = _entMan.System<RobustaConstructionSolver>().ResolveConstructionPrototypeForEntity(item.EntityPrototypeId);

            if (!string.IsNullOrEmpty(recipeId) &&
                _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto) &&
                cProto.Type == ConstructionType.Structure)
            {
                _constructionSystem.SpawnGhost(cProto, targetCoords, rotatedDir);
            }
        }

        return true;
    }

    public override bool HijackDeletion(EntityUid entity)
    {
        if (_entMan.HasComponent<ConstructionGhostComponent>(entity))
            _constructionSystem.ClearGhost(entity.GetHashCode());
        return true;
    }

    public IEnumerable<BlueprintItemEntry> GetFilteredAndOrderedItems()
    {
        return Blueprint.Items
            .Where(i => ActivePhaseFilter == null || i.Phase == ActivePhaseFilter.Value)
            .OrderBy(i => i.Phase)
            .ThenBy(i => i.LayerOrder)
            .ThenBy(i => i.OffsetY)
            .ThenBy(i => i.OffsetX);
    }

    public static int DirectionToSteps(Direction dir)
    {
        return dir switch
        {
            Direction.South => 0,
            Direction.West => 1,
            Direction.North => 2,
            Direction.East => 3,
            _ => 0
        };
    }

    public static (int X, int Y) RotateOffset(int x, int y, int width, int height, int steps)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        return (steps & 3) switch
        {
            1 => (y, w - 1 - x),
            2 => (w - 1 - x, h - 1 - y),
            3 => (h - 1 - y, x),
            _ => (x, y)
        };
    }

    public static Direction ParseDirection(string? dirStr)
    {
        if (!string.IsNullOrWhiteSpace(dirStr) && Enum.TryParse<Direction>(dirStr, true, out var dir))
            return dir;
        return Direction.South;
    }

    public static Direction RotateDirection(Direction itemDir, int steps)
    {
        Direction[] cardinals = { Direction.South, Direction.West, Direction.North, Direction.East };
        int idx = Array.IndexOf(cardinals, itemDir);
        if (idx < 0)
            idx = 0;
        return cardinals[(idx + (steps & 3)) & 3];
    }
}
