using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Client.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Mobs.Components;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction.Blueprints;

public sealed class RobustaBlueprintManager : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly Robust.Client.Placement.IPlacementManager _placement = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly RobustaConstructionSolver _solver = default!;
    [Dependency] private readonly ConstructionSystem _clientConstruction = default!;

    private ISawmill _log = default!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<RobustaBlueprint> _blueprints = new();
    public IReadOnlyList<RobustaBlueprint> Blueprints => _blueprints;

    public event Action? OnBlueprintsReloaded;

    // 2-click interactive area selection state
    private bool _isSelectingArea;
    private string _pendingExportName = string.Empty;
    private bool _pendingIncludeNonConstructible;
    private EntityUid? _selectionGridUid;
    private Vector2i? _firstSelectionCorner;

    public bool IsSelectingArea => _isSelectingArea;
    public EntityUid? SelectionGridUid => _selectionGridUid;
    public Vector2i? FirstSelectionCorner => _firstSelectionCorner;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("robusta.blueprints");
        _placement.PlacementChanged += OnPlacementChanged;
        if (!_overlay.HasOverlay<BlueprintPlacementOverlay>())
            _overlay.AddOverlay(new BlueprintPlacementOverlay());
        LoadAllBlueprints();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _placement.PlacementChanged -= OnPlacementChanged;
        if (_overlay.HasOverlay<BlueprintPlacementOverlay>())
            _overlay.RemoveOverlay<BlueprintPlacementOverlay>();
    }

    private void OnPlacementChanged(object? sender, EventArgs e)
    {
        BlueprintPlacementHijack.ClearActiveInstance();
    }

    public static string GetBlueprintsDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "RobustoClient", "Blueprints");
    }

    public void LoadAllBlueprints()
    {
        _blueprints.Clear();

        try
        {
            var dir = GetBlueprintsDirectory();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var files = Directory.GetFiles(dir)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
            {
                WriteSampleStarterBlueprint(dir);
                files = Directory.GetFiles(dir)
                    .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    RobustaBlueprint? bp = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? JsonSerializer.Deserialize<RobustaBlueprint>(text, JsonOptions)
                        : ParseSimpleYamlBlueprint(text, Path.GetFileNameWithoutExtension(file));

                    if (bp != null)
                    {
                        if (string.IsNullOrWhiteSpace(bp.Name))
                            bp.Name = Path.GetFileNameWithoutExtension(file);
                        _blueprints.Add(bp);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning($"Failed to parse blueprint file '{file}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to load blueprints directory: {ex.Message}");
        }

        OnBlueprintsReloaded?.Invoke();
    }

    public bool SaveBlueprint(RobustaBlueprint blueprint, string? fileNameOverride = null)
    {
        try
        {
            var dir = GetBlueprintsDirectory();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var rawName = string.IsNullOrWhiteSpace(fileNameOverride) ? blueprint.Name : fileNameOverride;
            var safeName = SanitizeFileName(rawName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "blueprint";

            if (!safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                safeName += ".json";

            var path = Path.Combine(dir, safeName);
            var json = JsonSerializer.Serialize(blueprint, JsonOptions);
            File.WriteAllText(path, json);

            _log.Info($"Saved blueprint '{blueprint.Name}' ({blueprint.Items.Count} items) to {path}.");
            LoadAllBlueprints();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to save blueprint '{blueprint.Name}': {ex.Message}");
            return false;
        }
    }

    private void WriteSampleStarterBlueprint(string directory)
    {
        var sample = new RobustaBlueprint
        {
            Name = "Airlock Chamber (3x3)",
            Description = "Starter 3x3 airlock chamber with reinforced walls and doors.",
            Width = 3,
            Height = 3,
            Items = new List<BlueprintItemEntry>
            {
                new() { OffsetX = 0, OffsetY = 0, Direction = "South", EntityPrototypeId = "Girder", ConstructionPrototypeId = "Girder", Phase = BlueprintZPhase.Phase2_FlooringAndFrames, LayerOrder = 0 },
                new() { OffsetX = 2, OffsetY = 0, Direction = "South", EntityPrototypeId = "Girder", ConstructionPrototypeId = "Girder", Phase = BlueprintZPhase.Phase2_FlooringAndFrames, LayerOrder = 0 },
                new() { OffsetX = 0, OffsetY = 1, Direction = "South", EntityPrototypeId = "Girder", ConstructionPrototypeId = "Girder", Phase = BlueprintZPhase.Phase2_FlooringAndFrames, LayerOrder = 0 },
                new() { OffsetX = 2, OffsetY = 1, Direction = "South", EntityPrototypeId = "Girder", ConstructionPrototypeId = "Girder", Phase = BlueprintZPhase.Phase2_FlooringAndFrames, LayerOrder = 0 },
                new() { OffsetX = 0, OffsetY = 2, Direction = "South", EntityPrototypeId = "Girder", ConstructionPrototypeId = "Girder", Phase = BlueprintZPhase.Phase2_FlooringAndFrames, LayerOrder = 0 },
                new() { OffsetX = 2, OffsetY = 2, Direction = "South", EntityPrototypeId = "Girder", ConstructionPrototypeId = "Girder", Phase = BlueprintZPhase.Phase2_FlooringAndFrames, LayerOrder = 0 },

                new() { OffsetX = 0, OffsetY = 0, Direction = "South", EntityPrototypeId = "WallSolid", ConstructionPrototypeId = "Wall", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 1, OffsetY = 0, Direction = "South", EntityPrototypeId = "Airlock", ConstructionPrototypeId = "Airlock", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 2, OffsetY = 0, Direction = "South", EntityPrototypeId = "WallSolid", ConstructionPrototypeId = "Wall", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 0, OffsetY = 1, Direction = "South", EntityPrototypeId = "WallSolid", ConstructionPrototypeId = "Wall", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 2, OffsetY = 1, Direction = "South", EntityPrototypeId = "WallSolid", ConstructionPrototypeId = "Wall", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 0, OffsetY = 2, Direction = "South", EntityPrototypeId = "WallSolid", ConstructionPrototypeId = "Wall", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 1, OffsetY = 2, Direction = "South", EntityPrototypeId = "Airlock", ConstructionPrototypeId = "Airlock", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 },
                new() { OffsetX = 2, OffsetY = 2, Direction = "South", EntityPrototypeId = "WallSolid", ConstructionPrototypeId = "Wall", Phase = BlueprintZPhase.Phase3_WallsWindowsDoors, LayerOrder = 1 }
            }
        };

        var path = Path.Combine(directory, "airlock_chamber.json");
        File.WriteAllText(path, JsonSerializer.Serialize(sample, JsonOptions));
    }

    public BlueprintZPhase ClassifyZPhase(EntityUid uid, string protoId)
    {
        var idLower = (protoId ?? string.Empty).ToLowerInvariant();

        // Phase 0: Subfloor & Lattice
        if (idLower.Contains("lattice") || idLower.Contains("catwalk"))
            return BlueprintZPhase.Phase0_SubfloorAndLattice;

        bool hasCable = false;
        bool hasNodeContainer = false;
        bool hasAirtight = false;
        bool hasDoor = false;
        bool hasMachine = false;

        if (Exists(uid))
        {
            foreach (var comp in _entMan.GetComponents(uid))
            {
                var compName = comp.GetType().Name;
                if (compName.Equals("CableComponent", StringComparison.OrdinalIgnoreCase))
                    hasCable = true;
                else if (compName.Equals("NodeContainerComponent", StringComparison.OrdinalIgnoreCase))
                    hasNodeContainer = true;
                else if (compName.Equals("AirtightComponent", StringComparison.OrdinalIgnoreCase))
                    hasAirtight = true;
                else if (compName.Equals("DoorComponent", StringComparison.OrdinalIgnoreCase) ||
                         compName.Equals("AirlockComponent", StringComparison.OrdinalIgnoreCase) ||
                         compName.Equals("FirelockComponent", StringComparison.OrdinalIgnoreCase))
                    hasDoor = true;
                else if (compName.Equals("MachineComponent", StringComparison.OrdinalIgnoreCase) ||
                         compName.Equals("ComputerComponent", StringComparison.OrdinalIgnoreCase))
                    hasMachine = true;
            }
        }

        // Phase 1: Underfloor Utilities (Cables, Gas Pipes, Manifolds, Disposal pipes/trunks)
        if (hasCable ||
            idLower.Contains("cablehv") ||
            idLower.Contains("cablemv") ||
            idLower.Contains("cableapc") ||
            idLower.Contains("gaspipe") ||
            idLower.Contains("disposalpipe") ||
            idLower.Contains("disposaltrunk") ||
            (hasNodeContainer && !hasAirtight && !hasMachine &&
             (idLower.Contains("pipe") || idLower.Contains("manifold") || idLower.Contains("valve") || idLower.Contains("cable"))))
        {
            return BlueprintZPhase.Phase1_UnderfloorUtilities;
        }

        // Phase 2: Flooring & Frames
        if (idLower.Contains("girder") ||
            idLower.Contains("machineframe") ||
            idLower.Contains("computerframe") ||
            idLower.Contains("wallframe") ||
            idLower.Contains("apcframe") ||
            idLower.Contains("windowframe"))
        {
            return BlueprintZPhase.Phase2_FlooringAndFrames;
        }

        // Phase 3: Walls, Windows, Grilles, Doors
        if (hasDoor ||
            hasAirtight ||
            idLower.Contains("wall") ||
            idLower.Contains("window") ||
            idLower.Contains("grille") ||
            idLower.Contains("airlock") ||
            idLower.Contains("windoor") ||
            idLower.Contains("firelock"))
        {
            return BlueprintZPhase.Phase3_WallsWindowsDoors;
        }

        // Phase 4: Machines & Fixtures
        return BlueprintZPhase.Phase4_MachinesAndFixtures;
    }

    public string? MapPrototypeToConstructionPrototype(string entityProtoId)
    {
        if (string.IsNullOrWhiteSpace(entityProtoId))
            return null;

        // 1. Check client ConstructionSystem recipe cache
        foreach (var cProto in _protoMan.EnumeratePrototypes<ConstructionPrototype>())
        {
            if (_clientConstruction.TryGetRecipePrototype(cProto.ID, out var targetProtoId) &&
                string.Equals(targetProtoId, entityProtoId, StringComparison.OrdinalIgnoreCase))
            {
                return cProto.ID;
            }
        }

        // 2. Fallback to RobustaConstructionSolver graph cache
        return _solver.ResolveConstructionPrototypeForEntity(entityProtoId);
    }

    public RobustaBlueprint? ExportAreaAsBlueprint(
        EntityUid gridUid,
        Vector2i corner1,
        Vector2i corner2,
        string blueprintName,
        bool includeNonConstructible = false)
    {
        if (!Exists(gridUid) || !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            _log.Warning("Cannot export blueprint: invalid grid entity.");
            return null;
        }

        int minX = Math.Min(corner1.X, corner2.X);
        int maxX = Math.Max(corner1.X, corner2.X);
        int minY = Math.Min(corner1.Y, corner2.Y);
        int maxY = Math.Max(corner1.Y, corner2.Y);

        int width = maxX - minX + 1;
        int height = maxY - minY + 1;

        var items = new List<BlueprintItemEntry>();
        var visited = new HashSet<EntityUid>();

        void ConsiderEntity(EntityUid uid)
        {
            if (!visited.Add(uid))
                return;

            if (!Exists(uid) || Deleted(uid) || IsClientSide(uid))
                return;

            if (HasComp<ConstructionGhostComponent>(uid) || HasComp<MobStateComponent>(uid))
                return;

            var xform = Transform(uid);
            if (!xform.Anchored)
                return;

            var meta = MetaData(uid);
            var protoId = meta.EntityPrototype?.ID;
            if (string.IsNullOrEmpty(protoId))
                return;

            var tile = _mapSystem.TileIndicesFor(gridUid, grid, xform.Coordinates);
            int tileX = tile.X;
            int tileY = tile.Y;

            if (tileX < minX || tileX > maxX || tileY < minY || tileY > maxY)
                return;

            _solver.ResolveExportBlueprintPrototypes(
                uid,
                protoId,
                out var exportedEntityProtoId,
                out var resolvedConstructionProtoId);

            var constructionProtoId = MapPrototypeToConstructionPrototype(exportedEntityProtoId)
                                      ?? resolvedConstructionProtoId
                                      ?? MapPrototypeToConstructionPrototype(protoId);
            var phase = ClassifyZPhase(uid, exportedEntityProtoId);
            var dir = xform.LocalRotation.GetCardinalDir().ToString();

            items.Add(new BlueprintItemEntry
            {
                OffsetX = tileX - minX,
                OffsetY = tileY - minY,
                Direction = dir,
                EntityPrototypeId = exportedEntityProtoId,
                ConstructionPrototypeId = constructionProtoId,
                Phase = phase,
                LayerOrder = (int)phase
            });
        }

        var gridXform = Transform(gridUid);
        var childEnumerator = gridXform.ChildEnumerator;
        while (childEnumerator.MoveNext(out var uid))
        {
            ConsiderEntity(uid);
        }

        items.Sort((a, b) =>
        {
            int c = a.Phase.CompareTo(b.Phase);
            if (c != 0) return c;
            c = a.LayerOrder.CompareTo(b.LayerOrder);
            if (c != 0) return c;
            c = a.OffsetY.CompareTo(b.OffsetY);
            return c != 0 ? c : a.OffsetX.CompareTo(b.OffsetX);
        });

        var blueprint = new RobustaBlueprint
        {
            Name = string.IsNullOrWhiteSpace(blueprintName) ? "Exported Blueprint" : blueprintName,
            Description = $"Exported {width}x{height} area ({items.Count} structures) on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.",
            Width = width,
            Height = height,
            Items = items
        };

        SaveBlueprint(blueprint, blueprintName);
        return blueprint;
    }

    public void StartAreaExportSelection(string blueprintName, bool includeNonConstructible = false)
    {
        _pendingExportName = string.IsNullOrWhiteSpace(blueprintName) ? $"blueprint_{DateTime.Now:HHmmss}" : blueprintName;
        _pendingIncludeNonConstructible = includeNonConstructible;
        _selectionGridUid = null;
        _firstSelectionCorner = null;
        _isSelectingArea = true;

        ArmSelectionPlacementHijack();

        _log.Info($"Started 2-click blueprint area export for '{_pendingExportName}'. Click first corner tile, then second corner tile.");
    }

    private void ArmSelectionPlacementHijack()
    {
        _placement.BeginPlacing(new Robust.Shared.Enums.PlacementInformation
        {
            IsTile = false,
            PlacementOption = "SnapgridCenter"
        }, new BlueprintAreaSelectionHijack(this));
    }

    public void CancelAreaExportSelection()
    {
        if (!_isSelectingArea)
            return;

        _isSelectingArea = false;
        _selectionGridUid = null;
        _firstSelectionCorner = null;
        _placement.Clear();
        _log.Info("Cancelled blueprint area export selection.");
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (!_isSelectingArea)
            return;

        if (_input.IsKeyDown(Keyboard.Key.Escape) || _input.IsKeyDown(Keyboard.Key.MouseRight))
        {
            CancelAreaExportSelection();
            return;
        }

        // PlacementManager clears itself after each click; re-arm for the 2nd corner click while selecting
        if (!_placement.IsActive)
        {
            ArmSelectionPlacementHijack();
        }
    }

    internal bool HandleAreaSelectionCoordinates(EntityCoordinates coordinates)
    {
        if (!_isSelectingArea)
            return false;

        var gridUid = coordinates.EntityId;
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var foundGrid = _transform.GetGrid(coordinates);
            if (foundGrid == null || !TryComp(foundGrid.Value, out grid))
            {
                if (!TryGetCursorGridTile(out gridUid, out var fallbackTile))
                    return true;
                CompleteCornerSelection(gridUid, fallbackTile);
                return true;
            }
            gridUid = foundGrid.Value;
        }

        var tileIndices = _mapSystem.TileIndicesFor(gridUid, grid, coordinates);
        CompleteCornerSelection(gridUid, tileIndices);
        return true;
    }

    private void HandleAreaSelectionClick()
    {
        if (!TryGetCursorGridTile(out var gridUid, out var tileIndices))
            return;

        CompleteCornerSelection(gridUid, tileIndices);
    }

    private void CompleteCornerSelection(EntityUid gridUid, Vector2i tileIndices)
    {
        if (_firstSelectionCorner == null || _selectionGridUid != gridUid)
        {
            _selectionGridUid = gridUid;
            _firstSelectionCorner = tileIndices;
            _log.Info($"Blueprint selection corner 1 set at tile {tileIndices}. Click second corner tile to export.");
            return;
        }

        var c1 = _firstSelectionCorner.Value;
        var c2 = tileIndices;

        _isSelectingArea = false;
        _selectionGridUid = null;
        _firstSelectionCorner = null;
        _placement.Clear();

        ExportAreaAsBlueprint(gridUid, c1, c2, _pendingExportName, _pendingIncludeNonConstructible);
    }

    public bool TryGetCursorGridTile(out EntityUid gridUid, out Vector2i tileIndices)
    {
        gridUid = EntityUid.Invalid;
        tileIndices = default;

        var cursorMapCoords = _eye.PixelToMap(_input.MouseScreenPosition);
        if (cursorMapCoords.MapId == MapId.Nullspace)
            return false;

        if (_player.LocalEntity is { } player && Exists(player))
        {
            var playerXform = Transform(player);
            if (playerXform.GridUid is { } pGrid && Exists(pGrid) && TryComp<MapGridComponent>(pGrid, out var gridComp))
            {
                gridUid = pGrid;
                tileIndices = _mapSystem.WorldToTile(pGrid, gridComp, cursorMapCoords.Position);
                return true;
            }
        }

        if (_transform.GetGrid(_transform.ToCoordinates(cursorMapCoords)) is { } cursorGrid &&
            Exists(cursorGrid) &&
            TryComp<MapGridComponent>(cursorGrid, out var cGridComp))
        {
            gridUid = cursorGrid;
            tileIndices = _mapSystem.WorldToTile(cursorGrid, cGridComp, cursorMapCoords.Position);
            return true;
        }

        return false;
    }

    private sealed class BlueprintAreaSelectionHijack : Robust.Client.Placement.PlacementHijack
    {
        private readonly RobustaBlueprintManager _manager;

        public BlueprintAreaSelectionHijack(RobustaBlueprintManager manager)
        {
            _manager = manager;
        }

        public override bool HijackPlacementRequest(EntityCoordinates coordinates)
        {
            return _manager.HandleAreaSelectionCoordinates(coordinates);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray();
        return new string(chars).ToLowerInvariant();
    }

    private static RobustaBlueprint? ParseSimpleYamlBlueprint(string yamlText, string fallbackName)
    {
        var bp = new RobustaBlueprint { Name = fallbackName };
        BlueprintItemEntry? currentItem = null;

        foreach (var rawLine in yamlText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            if (line.StartsWith("- "))
            {
                currentItem = new BlueprintItemEntry();
                bp.Items.Add(currentItem);
                line = line.Substring(2).Trim();
            }

            int colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                continue;

            var key = line.Substring(0, colonIdx).Trim().ToLowerInvariant();
            var val = line.Substring(colonIdx + 1).Trim().Trim('"', '\'');

            if (currentItem == null)
            {
                if (key == "name") bp.Name = val;
                else if (key == "description") bp.Description = val;
                else if (key == "width" && int.TryParse(val, out var w)) bp.Width = w;
                else if (key == "height" && int.TryParse(val, out var h)) bp.Height = h;
            }
            else
            {
                if (key == "offsetx" && int.TryParse(val, out var ox)) currentItem.OffsetX = ox;
                else if (key == "offsety" && int.TryParse(val, out var oy)) currentItem.OffsetY = oy;
                else if (key == "direction") currentItem.Direction = val;
                else if (key == "entityprototypeid" || key == "prototype") currentItem.EntityPrototypeId = val;
                else if (key == "constructionprototypeid" || key == "recipe") currentItem.ConstructionPrototypeId = val;
                else if (key == "phase" && Enum.TryParse<BlueprintZPhase>(val, true, out var ph)) currentItem.Phase = ph;
                else if (key == "layerorder" && int.TryParse(val, out var lo)) currentItem.LayerOrder = lo;
            }
        }

        return bp.Items.Count > 0 ? bp : null;
    }
}