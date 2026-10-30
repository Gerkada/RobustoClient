using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Content.Client.Construction;
using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Content.Shared.Wires;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Containers;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.Construction.Blueprints;
using RobustoClient.Systems.Hacking;

namespace RobustoClient.Systems.Construction;

public sealed class RobustaAutoConstructSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedToolSystem _toolSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;
    [Dependency] private readonly RobustaConstructionSolver _solver = default!;
    [Dependency] private readonly ConstructionSystem _clientConstruction = default!;
    [Dependency] private readonly RobustaWiresSolver _wiresSolver = default!;

    private ISawmill _log = default!;

    private const int CheckFrameInterval = 5;
    private const float JobTimeoutSeconds = 60f;
    private const float SettleDelay = 0.25f;
    private const float TargetRescanTimeout = 60f;
    private const float DoubleTapOrHoldWindow = 0.25f;
    private const float StepRetryInterval = 1.15f;

    private bool _pendingSingleTap;
    private float _singleTapTimer;
    private bool _wasConstructKeyDown;
    private bool _wasDeconstructKeyDown;

    private int _frameCheckCounter;
    private float _frameCheckAccumulator;
    private MapCoordinates _lastPlayerMapCoords;
    private float _movementSettleTimer;

    private enum StepExecutionPhase
    {
        PrepareAndEquip,
        EnsureWelderLit,
        PerformInteraction,
        WaitForCompletion
    }

    private SolvedConstructionPlan? _activePlan;
    private int _stepIndex;
    private StepExecutionPhase _phase;
    private EntityUid _currentTarget;
    private string _currentTargetLabel = string.Empty;
    private EntityCoordinates _targetCoordinates;
    private MapCoordinates _targetMapCoords;
    private Direction _targetDirection = Direction.South;
    private string? _desiredBlueprintEntityProtoId;
    private float _waitTimer;
    private float _rescanTimer;
    private float _stepTotalElapsed;
    private float _currentTaskElapsed;
    private bool _wasInDoAfter;
    private EntityUid? _litWelderUid;
    private EntityUid _stepStartTarget;
    private string? _stepStartNode;
    private EntityUid? _stepStartHeldItem;
    private int _stepStartHeldStackCount;
    private int _stepRetryCount;
    private int _equipRetryCount;
    private int _welderIgniteRetries;
    private int _multiToolCycleRetries;
    private bool _sawDoAfterForStep;
    private bool _doAfterCancelledForStep;
    private float _stepWaitElapsed;
    private float _postDoAfterTimer;
    private bool _delegatedWireSolveStarted;
    private readonly HashSet<EntityUid> _preExistingTileEntities = new();

    private string? _activeBlueprintName;
    private int _totalBlueprintTasksQueued;
    private int _completedBlueprintTasksCount;
    private (EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)? _activeBlueprintTask;
    private string? _currentDiagnosticStatus;

    private string? _hudBannerHeader;
    private string? _hudBannerDetail;
    private bool _hudBannerIsError;
    private float _hudBannerTimer;

    private readonly List<(EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)> _blueprintQueue = new();
    public IReadOnlyList<(EntityUid GridUid, Vector2i Tile, Direction Dir, BlueprintItemEntry Item)> PendingBlueprintTasks => _blueprintQueue;
    public BlueprintZPhase? CurrentActiveBlueprintPhase => _blueprintQueue.Count > 0 ? _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item)) : null;
    public BlueprintZPhase? ActivePhaseFilter { get; set; }
    public bool AutoBuildPlacedBlueprints { get; set; } = true;

    private static readonly string[] UnfinishedFrameKeywords =
    {
        "girder",
        "machineframe",
        "computerframe",
        "unfinished",
        "assembly",
        "apcframe",
        "wallframe",
        "windowframe",
        "frame"
    };

    public bool IsBusy => _activePlan != null || _blueprintQueue.Count > 0;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("robusta.autoconstruct");
        if (!_overlay.HasOverlay<RobustaConstructHackHudOverlay>())
            _overlay.AddOverlay(new RobustaConstructHackHudOverlay());
    }

    public override void Shutdown()
    {
        base.Shutdown();
        if (_overlay.HasOverlay<RobustaConstructHackHudOverlay>())
            _overlay.RemoveOverlay<RobustaConstructHackHudOverlay>();
    }

    public void HandleConstructKeyTap()
    {
        // Ignore OS key-repeat events while the construct key is held down
        if (_wasConstructKeyDown)
            return;
        _wasConstructKeyDown = true;

        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            _log.Info("Aborting active AutoConstruct job.");
            StopJob();
            return;
        }

        // A held deconstruct combination (whatever AutoDeconstructKey is configured to, e.g. N or Shift+V)
        // immediately triggers Deconstruct instead of starting the build tap window.
        if (RobustaConfig.AutoDeconstructKey.IsDown(_input))
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            TriggerAtCursor(forceDeconstruct: true);
            return;
        }

        // Second tap within window -> Double-tap Deconstruct!
        if (_pendingSingleTap)
        {
            _pendingSingleTap = false;
            _singleTapTimer = 0f;
            TriggerAtCursor(forceDeconstruct: true);
            return;
        }

        // Start short window to distinguish 1 short tap (Build/Auto) vs double-tap / long-hold (Deconstruct)
        _pendingSingleTap = true;
        _singleTapTimer = DoubleTapOrHoldWindow;
    }

    public void HandleDeconstructKeyTap()
    {
        // Ignore OS key-repeat events while the deconstruct key is held down
        if (_wasDeconstructKeyDown)
            return;
        _wasDeconstructKeyDown = true;

        TriggerAtCursor(forceDeconstruct: true);
    }

    public void TriggerAtCursor(bool? forceDeconstruct = null)
    {
        _pendingSingleTap = false;
        _singleTapTimer = 0f;

        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            _log.Info("Aborting active AutoConstruct job.");
            StopJob("Aborted by user.");
            return;
        }

        if (_player.LocalEntity is not { } player || !Exists(player))
            return;

        _solver.NoteHeldEntity(_hands.GetActiveItem(player));

        var mouseScreenPos = _input.MouseScreenPosition;
        var cursorMapCoords = _eye.PixelToMap(mouseScreenPos);
        if (cursorMapCoords.MapId == MapId.Nullspace)
            return;

        var playerMapCoords = _transform.GetMapCoordinates(player);
        if (playerMapCoords.MapId != cursorMapCoords.MapId)
            return;

        var target = FindTargetAtCursor(player, cursorMapCoords, out var outOfRangeTarget, out float outOfRangeDist);
        if (target == null || !Exists(target.Value))
        {
            if (outOfRangeTarget.HasValue && Exists(outOfRangeTarget.Value))
            {
                var rangeMsg = $"Target '{GetEntityDisplayName(outOfRangeTarget.Value)}' is out of range ({outOfRangeDist:0.1}m > {SharedInteractionSystem.InteractionRange:0.1}m) — move closer.";
                _log.Info(rangeMsg);
                SetHudBanner("[AutoConstruct]", rangeMsg, isError: true, duration: 4.5f);
            }
            else
            {
                _log.Info("No constructible or deconstructible entity found at cursor.");
                SetHudBanner("[AutoConstruct]", "No constructible or deconstructible entity found at cursor.", isError: true, duration: 3.5f);
            }
            return;
        }

        SolvedConstructionPlan? plan = null;
        if (forceDeconstruct == true)
        {
            plan = _solver.SolveDeconstructPlan(target.Value);
        }
        else if (forceDeconstruct == false)
        {
            plan = _solver.SolveBuildPlan(target.Value) ?? _solver.SolveDeconstructPlan(target.Value);
        }
        else
        {
            if (IsGhostOrUnfinishedFrame(target.Value))
            {
                plan = _solver.SolveBuildPlan(target.Value) ?? _solver.SolveDeconstructPlan(target.Value);
            }
            else
            {
                plan = _solver.SolveDeconstructPlan(target.Value) ?? _solver.SolveBuildPlan(target.Value);
            }
        }

        if (plan == null || plan.Steps.Count == 0)
        {
            var modeLabel = forceDeconstruct == true ? "deconstruction" : "construction";
            var targetLabel = GetEntityDisplayName(target.Value);
            _log.Warning($"Could not solve construction plan for {ToPrettyString(target.Value)}.");
            SetHudBanner("[AutoConstruct Failed]", $"Could not solve {modeLabel} plan for '{targetLabel}'.", isError: true, duration: 5f);
            return;
        }

        StartJob(target.Value, plan);
    }

    public void StartJob(
        EntityUid target,
        SolvedConstructionPlan plan,
        bool resetTaskTimer = true,
        string? desiredEntityProtoId = null,
        Direction? targetDir = null)
    {
        if (!Exists(target) || plan.Steps.Count == 0)
            return;

        _activePlan = plan;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = target;
        _currentTargetLabel = !string.IsNullOrEmpty(desiredEntityProtoId)
            ? desiredEntityProtoId
            : GetEntityDisplayName(target);
        _desiredBlueprintEntityProtoId = desiredEntityProtoId;
        _targetCoordinates = Transform(target).Coordinates;
        _targetMapCoords = _transform.GetMapCoordinates(target);
        _targetDirection = targetDir ?? Transform(target).LocalRotation.GetCardinalDir();
        _preExistingTileEntities.Clear();
        if (_targetMapCoords.MapId != MapId.Nullspace)
        {
            foreach (var uid in _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained))
            {
                if (uid != target && Exists(uid) && !Deleted(uid) && !HasGhostComponent(uid))
                    _preExistingTileEntities.Add(uid);
            }
        }
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _stepTotalElapsed = 0f;
        if (resetTaskTimer)
            _currentTaskElapsed = 0f;
        _currentDiagnosticStatus = null;
        _hudBannerTimer = 0f;
        _wasInDoAfter = false;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
        _delegatedWireSolveStarted = false;

        _log.Info($"Started {(plan.IsDeconstruct ? "Deconstruct" : "Build")} plan on {ToPrettyString(target)} " +
                  $"({plan.GraphId}: {plan.StartNode} -> {plan.TargetNode}, {plan.Steps.Count} steps).");
    }

    public void StopJob(string? reason = null)
    {
        bool hadActiveWork = _activePlan != null || _blueprintQueue.Count > 0;
        _blueprintQueue.Clear();
        _activeBlueprintTask = null;
        _activeBlueprintName = null;
        _totalBlueprintTasksQueued = 0;
        _completedBlueprintTasksCount = 0;
        _currentTaskElapsed = 0f;
        FinishCurrentPlan(cancelled: true, clearGhost: true);
        if (hadActiveWork && !string.IsNullOrEmpty(reason))
            SetHudBanner("[AutoConstruct Stopped]", reason, isError: false, duration: 3f);
    }

    private void FailBlueprintOrJob(string reason)
    {
        bool wasBlueprint = _blueprintQueue.Count > 0 || _activeBlueprintTask != null;
        var modeTitle = wasBlueprint
            ? $"[Blueprint Build Failed] {_activeBlueprintName ?? "Blueprint"}"
            : (_activePlan?.IsDeconstruct == true ? "[AutoDeconstruct Failed]" : $"[AutoBuild Failed] {_currentTargetLabel}");

        _log.Warning($"{modeTitle}: {reason}");
        _blueprintQueue.Clear();
        _activeBlueprintTask = null;
        _activeBlueprintName = null;
        _totalBlueprintTasksQueued = 0;
        _completedBlueprintTasksCount = 0;
        _currentTaskElapsed = 0f;
        FinishCurrentPlan(cancelled: true, clearGhost: true);
        SetHudBanner(modeTitle, reason, isError: true, duration: 6.5f);
    }

    public void SetHudBanner(string header, string detail, bool isError, float duration = 5f)
    {
        _hudBannerHeader = header;
        _hudBannerDetail = detail;
        _hudBannerIsError = isError;
        _hudBannerTimer = duration;
    }

    private void FinishCurrentPlan(bool cancelled = false, bool clearGhost = false)
    {
        _itemUtil.CancelAll();
        ExtinguishActiveWelderIfNeeded();
        if (!cancelled && _blueprintQueue.Count == 0 && !_wiresSolver.IsSolving)
        {
            _itemUtil.StowActiveTrackedItem();
        }

        if ((cancelled || clearGhost || _blueprintQueue.Count == 0) &&
            Exists(_currentTarget) && HasGhostComponent(_currentTarget))
        {
            ClearGhostEntity(_currentTarget);
        }

        _preExistingTileEntities.Clear();
        _activePlan = null;
        _stepIndex = 0;
        _phase = StepExecutionPhase.PrepareAndEquip;
        _currentTarget = EntityUid.Invalid;
        _currentDiagnosticStatus = null;
        _waitTimer = 0f;
        _rescanTimer = 0f;
        _stepTotalElapsed = 0f;
        _wasInDoAfter = false;
        _stepStartTarget = EntityUid.Invalid;
        _stepStartNode = null;
        _stepStartHeldItem = null;
        _stepStartHeldStackCount = 0;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _welderIgniteRetries = 0;
        _sawDoAfterForStep = false;
        _doAfterCancelledForStep = false;
        _stepWaitElapsed = 0f;
        _postDoAfterTimer = 0f;
        if (_delegatedWireSolveStarted)
        {
            _wiresSolver.StopSolving();
            _delegatedWireSolveStarted = false;
        }
    }

    public void EnqueuePhasedBlueprintBuild(
        RobustaBlueprint blueprint,
        EntityCoordinates origin,
        Direction rotation,
        BlueprintZPhase? onlyPhase = null)
        => StartPhasedBlueprintBuild(blueprint, origin, rotation, onlyPhase);

    public void StartPhasedBlueprintBuild(
        RobustaBlueprint blueprint,
        EntityCoordinates origin,
        Direction rotation,
        BlueprintZPhase? onlyPhase = null)
    {
        var gridUid = origin.EntityId;
        if (!TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var foundGrid = _transform.GetGrid(origin);
            if (foundGrid == null || !TryComp(foundGrid.Value, out grid))
            {
                _log.Warning("Cannot start phased blueprint build: target coordinates are not on a valid grid.");
                SetHudBanner("[Blueprint Build Failed]", "Target coordinates are not on a valid grid.", isError: true, duration: 5f);
                return;
            }
            gridUid = foundGrid.Value;
        }

        var baseTile = _mapSystem.TileIndicesFor(gridUid, grid, origin);
        int rotSteps = BlueprintPlacementHijack.DirectionToSteps(rotation);

        var items = blueprint.Items
            .Where(i => onlyPhase == null || i.Phase == onlyPhase.Value || GetEffectiveBlueprintPhase(i) == onlyPhase.Value)
            .OrderBy(i => GetEffectiveBlueprintPhase(i))
            .ThenBy(i => GetEffectiveBlueprintSubPriority(i))
            .ThenBy(i => i.LayerOrder)
            .ThenBy(i => i.OffsetY)
            .ThenBy(i => i.OffsetX)
            .ToList();

        if (items.Count == 0)
            return;

        int added = 0;
        foreach (var item in items)
        {
            var (rx, ry) = BlueprintPlacementHijack.RotateOffset(
                item.OffsetX,
                item.OffsetY,
                blueprint.Width,
                blueprint.Height,
                rotSteps);
            var tile = baseTile + new Vector2i(rx, ry);
            var dir = BlueprintPlacementHijack.RotateDirection(
                BlueprintPlacementHijack.ParseDirection(item.Direction),
                rotSteps);

            _blueprintQueue.Add((gridUid, tile, dir, item));
            added++;
        }

        _blueprintQueue.Sort((a, b) =>
        {
            int c = GetEffectiveBlueprintPhase(a.Item).CompareTo(GetEffectiveBlueprintPhase(b.Item));
            if (c != 0) return c;
            c = GetEffectiveBlueprintSubPriority(a.Item).CompareTo(GetEffectiveBlueprintSubPriority(b.Item));
            if (c != 0) return c;
            return a.Item.LayerOrder.CompareTo(b.Item.LayerOrder);
        });

        _activeBlueprintName = blueprint.Name;
        _totalBlueprintTasksQueued = _blueprintQueue.Count;
        _completedBlueprintTasksCount = 0;
        _activeBlueprintTask = null;
        _currentTaskElapsed = 0f;
        _currentDiagnosticStatus = null;
        _hudBannerTimer = 0f;

        _log.Info($"Queued {added} phased blueprint build tasks for '{blueprint.Name}' (Phase filter: {onlyPhase?.ToString() ?? "All"}).");
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_hudBannerTimer > 0f)
            _hudBannerTimer = Math.Max(0f, _hudBannerTimer - frameTime);

        if (!RobustaConfig.AutoConstructKey.IsDown(_input))
            _wasConstructKeyDown = false;
        if (!RobustaConfig.AutoDeconstructKey.IsDown(_input))
            _wasDeconstructKeyDown = false;

        if (_pendingSingleTap)
        {
            _singleTapTimer -= frameTime;
            if (_singleTapTimer <= 0f)
            {
                _pendingSingleTap = false;
                _singleTapTimer = 0f;
                // If user is still holding the key at the end of the window -> Long-press Deconstruct;
                // otherwise -> Auto-detect (builds ghosts/unfinished frames, deconstructs completed structures)
                bool isHolding = RobustaConfig.AutoConstructKey.IsDown(_input);
                TriggerAtCursor(forceDeconstruct: isHolding ? true : null);
                return;
            }
        }

        if (_activePlan == null && _blueprintQueue.Count == 0)
        {
            _frameCheckCounter = 0;
            _frameCheckAccumulator = 0f;
            return;
        }

        if (_player.LocalEntity is not { } player || !Exists(player))
        {
            StopJob();
            return;
        }

        _solver.NoteHeldEntity(_hands.GetActiveItem(player));

        // Observe DoAfter state every frame so fast DoAfters starting/finishing between 5-frame ticks are never missed
        if (IsPlayerInActiveDoAfter(player))
        {
            _wasInDoAfter = true;
            _sawDoAfterForStep = true;
            _lastPlayerMapCoords = _transform.GetMapCoordinates(player);
            return;
        }

        // Check every frame if the player is actively moving so we never start a DoAfter or split/drop materials mid-walk
        if (IsPlayerMoving(player))
        {
            _movementSettleTimer = 0.22f;
            _currentDiagnosticStatus = "Paused: Waiting for player to stop moving...";
            return;
        }

        if (_movementSettleTimer > 0f)
        {
            _movementSettleTimer = Math.Max(0f, _movementSettleTimer - frameTime);
            return;
        }

        _frameCheckAccumulator += frameTime;
        _frameCheckCounter++;
        if (_frameCheckCounter < CheckFrameInterval)
            return;

        float stepDt = _frameCheckAccumulator;
        _frameCheckAccumulator = 0f;
        _frameCheckCounter = 0;

        if (_activePlan != null)
        {
            _stepTotalElapsed += stepDt;
            _currentTaskElapsed += stepDt;
        }

        if (_wasInDoAfter)
        {
            _wasInDoAfter = false;
            _waitTimer = Math.Max(_waitTimer, 0.3f);
        }

        if (_itemUtil.IsBusy)
            return;

        if (_waitTimer > 0f)
        {
            _waitTimer -= stepDt;
            return;
        }

        if (_activePlan == null)
        {
            ProcessNextQueuedBlueprintTask(player, stepDt);
            return;
        }

        ExecuteCurrentStep(player, stepDt);
    }

    private void ProcessNextQueuedBlueprintTask(EntityUid player, float dt)
    {
        var playerMapCoords = _transform.GetMapCoordinates(player);

        // 1. Prune invalid grids or verified-completed items in the current earliest effective phase
        if (_blueprintQueue.Count > 0)
        {
            var minPhase = _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item));

            for (int i = _blueprintQueue.Count - 1; i >= 0; i--)
            {
                var candidate = _blueprintQueue[i];
                if (GetEffectiveBlueprintPhase(candidate.Item) != minPhase)
                    continue;

                if (!Exists(candidate.GridUid) || !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                {
                    _blueprintQueue.RemoveAt(i);
                    continue;
                }

                var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
                var cMap = _transform.ToMapCoordinates(cCoords);
                if (cMap.MapId != playerMapCoords.MapId)
                    continue;

                if (IsBlueprintItemAlreadySatisfied(cMap, candidate.Item, candidate.Dir, candidate.GridUid, cGrid, candidate.Tile))
                {
                    _blueprintQueue.RemoveAt(i);
                    _completedBlueprintTasksCount++;
                    if (_activeBlueprintTask is { } active &&
                        active.GridUid == candidate.GridUid &&
                        active.Tile == candidate.Tile &&
                        string.Equals(active.Item.EntityPrototypeId, candidate.Item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeBlueprintTask = null;
                        _currentTaskElapsed = 0f;
                        _currentDiagnosticStatus = null;
                    }
                }
            }
        }

        if (_blueprintQueue.Count == 0)
        {
            var bpName = _activeBlueprintName ?? "Blueprint";
            _log.Info($"Completed all blueprint build tasks for '{bpName}'.");
            SetHudBanner(
                "[Blueprint Build Complete]",
                $"Finished building '{bpName}' ({_completedBlueprintTasksCount}/{Math.Max(_completedBlueprintTasksCount, _totalBlueprintTasksQueued)} items).",
                isError: false,
                duration: 3.5f);
            _activeBlueprintName = null;
            _activeBlueprintTask = null;
            _currentTaskElapsed = 0f;
            _currentDiagnosticStatus = null;
            _itemUtil.StowActiveTrackedItem();
            return;
        }

        var currentPhase = _blueprintQueue.Min(t => GetEffectiveBlueprintPhase(t.Item));

        // Determine the earliest sub-priority still queued on each tile so a pipe (SubPriority 2)
        // is NEVER built on a tile while a filter/mixer/pump/port/vent (SubPriority 1) is still queued on that tile.
        var minSubPriorityPerTile = new Dictionary<(EntityUid GridUid, Vector2i Tile), int>();
        foreach (var entry in _blueprintQueue)
        {
            if (GetEffectiveBlueprintPhase(entry.Item) != currentPhase)
                continue;

            var key = (entry.GridUid, entry.Tile);
            int sub = GetEffectiveBlueprintSubPriority(entry.Item);
            if (!minSubPriorityPerTile.TryGetValue(key, out int curMin) || sub < curMin)
                minSubPriorityPerTile[key] = sub;
        }

        int bestIdx = -1;
        int bestSubPriority = int.MaxValue;
        float bestDist = float.MaxValue;
        int bestLayerOrder = int.MaxValue;

        int nearestAnyIdx = -1;
        float nearestAnyDist = float.MaxValue;

        for (int i = 0; i < _blueprintQueue.Count; i++)
        {
            var candidate = _blueprintQueue[i];
            if (GetEffectiveBlueprintPhase(candidate.Item) != currentPhase ||
                !Exists(candidate.GridUid) ||
                !TryComp<MapGridComponent>(candidate.GridUid, out var cGrid))
                continue;

            int subPriority = GetEffectiveBlueprintSubPriority(candidate.Item);
            if (minSubPriorityPerTile.TryGetValue((candidate.GridUid, candidate.Tile), out int tileMinSub) &&
                subPriority > tileMinSub)
            {
                // Hold off on pipes on this tile until the contraption(s) on this tile are built first
                continue;
            }

            var cCoords = _mapSystem.GridTileToLocal(candidate.GridUid, cGrid, candidate.Tile);
            var cMap = _transform.ToMapCoordinates(cCoords);
            if (cMap.MapId != playerMapCoords.MapId)
                continue;

            float d = (cMap.Position - playerMapCoords.Position).Length();
            if (d < nearestAnyDist)
            {
                nearestAnyDist = d;
                nearestAnyIdx = i;
            }

            if (d > SharedInteractionSystem.InteractionRange)
                continue;

            if (subPriority < bestSubPriority ||
                (subPriority == bestSubPriority && d < bestDist - 0.05f) ||
                (subPriority == bestSubPriority && MathF.Abs(d - bestDist) <= 0.05f && candidate.Item.LayerOrder < bestLayerOrder))
            {
                bestSubPriority = subPriority;
                bestDist = d;
                bestLayerOrder = candidate.Item.LayerOrder;
                bestIdx = i;
            }
        }

        // If no eligible task in this phase is within interaction range, inform the user on the HUD and keep rechecking up to 60s
        if (bestIdx < 0)
        {
            _currentTaskElapsed += dt;
            if (nearestAnyIdx >= 0)
            {
                var nearest = _blueprintQueue[nearestAnyIdx];
                var nGrid = Comp<MapGridComponent>(nearest.GridUid);
                _targetCoordinates = _mapSystem.GridTileToLocal(nearest.GridUid, nGrid, nearest.Tile);
                _targetMapCoords = _transform.ToMapCoordinates(_targetCoordinates);
                _currentDiagnosticStatus =
                    $"Paused: Step closer to '{nearest.Item.EntityPrototypeId}' at ({nearest.Tile.X}, {nearest.Tile.Y}) ({nearestAnyDist:0.1}m > {SharedInteractionSystem.InteractionRange:0.2}m) [{_currentTaskElapsed:0}s / {JobTimeoutSeconds:0}s]";

                if (_currentTaskElapsed >= JobTimeoutSeconds)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s waiting to get in range of '{nearest.Item.EntityPrototypeId}' at tile ({nearest.Tile.X}, {nearest.Tile.Y}).");
                }
            }
            return;
        }

        // Keep task in _blueprintQueue until IsBlueprintItemAlreadySatisfied confirms the structure is actually built!
        var task = _blueprintQueue[bestIdx];
        if (_activeBlueprintTask == null ||
            _activeBlueprintTask.Value.GridUid != task.GridUid ||
            _activeBlueprintTask.Value.Tile != task.Tile ||
            !string.Equals(_activeBlueprintTask.Value.Item.EntityPrototypeId, task.Item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
        {
            _activeBlueprintTask = task;
            _currentTaskElapsed = 0f;
            _currentDiagnosticStatus = null;
        }
        else
        {
            _currentTaskElapsed += dt;
            if (_currentTaskElapsed >= JobTimeoutSeconds)
            {
                FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s trying to build '{task.Item.EntityPrototypeId}' at tile ({task.Tile.X}, {task.Tile.Y}).");
                return;
            }
        }

        var grid = Comp<MapGridComponent>(task.GridUid);
        var tileCoords = _mapSystem.GridTileToLocal(task.GridUid, grid, task.Tile);
        var tileMapCoords = _transform.ToMapCoordinates(tileCoords);
        _targetCoordinates = tileCoords;
        _targetMapCoords = tileMapCoords;

        // 1. Direct stack placement (Cables, Floor Tiles) — stays in _blueprintQueue until verified placed on next recheck
        if (IsDirectStackBlueprintItem(task.Item, out var stackProtoOrType, out var matId))
        {
            if (TryExecuteDirectStackBlueprintItem(task.Item, tileCoords))
            {
                _currentDiagnosticStatus = $"Placing '{task.Item.EntityPrototypeId}' at ({task.Tile.X}, {task.Tile.Y}) [{_currentTaskElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
                _waitTimer = 0.45f;
                return;
            }

            if (_currentTaskElapsed < 0.6f)
                TryOpenWornStorageSlots(player);

            _currentDiagnosticStatus = $"Waiting for required material '{matId}' ({stackProtoOrType}) [{_currentTaskElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
            if (_currentTaskElapsed >= 4.0f)
            {
                FailBlueprintOrJob($"Missing required stack item '{stackProtoOrType}' ({matId}) to place '{task.Item.EntityPrototypeId}'.");
                return;
            }

            _waitTimer = 0.45f;
            return;
        }

        var recipeId = task.Item.ConstructionPrototypeId;
        var resolvedFromEntity = _solver.ResolveConstructionPrototypeForEntity(task.Item.EntityPrototypeId);
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
        {
            recipeId = resolvedFromEntity;
        }
        else if (!string.IsNullOrEmpty(resolvedFromEntity) &&
                 !string.Equals(recipeId, resolvedFromEntity, StringComparison.OrdinalIgnoreCase) &&
                 _solver.GetDesiredPipeLayer(task.Item.EntityPrototypeId, null) > 0 &&
                 RobustaConstructionSolver.GetPipeLayerForIdOrNode(resolvedFromEntity) > 0)
        {
            recipeId = resolvedFromEntity;
        }

        // 2. Existing unfinished frame, unanchored/unconfigured structure, or matching ghost on the tile
        var existingFrameOrGhost = FindMatchingFrameOrGhostOnTile(tileMapCoords, task.Item, task.Dir, recipeId);
        if (existingFrameOrGhost != null && Exists(existingFrameOrGhost.Value))
        {
            var existingPlan = _solver.SolveBuildPlan(existingFrameOrGhost.Value, task.Item.EntityPrototypeId);
            if (existingPlan != null && existingPlan.Steps.Count > 0)
            {
                StartJob(
                    existingFrameOrGhost.Value,
                    existingPlan,
                    resetTaskTimer: false,
                    desiredEntityProtoId: task.Item.EntityPrototypeId,
                    targetDir: task.Dir);
                return;
            }
        }

        // 3. Ensure no mismatched ghost is occupying the tile, then spawn this item's construction ghost on-demand
        ClearGhostsOnTile(tileMapCoords);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto) &&
            cProto.Type == ConstructionType.Structure)
        {
            if (_clientConstruction.TrySpawnGhost(cProto, tileCoords, task.Dir, out var ghostUid) &&
                ghostUid != null && Exists(ghostUid.Value))
            {
                var ghostPlan = _solver.SolveBuildPlan(cProto, includeGhostStart: true, desiredEntityProtoId: task.Item.EntityPrototypeId)
                                ?? _solver.SolveBuildPlan(ghostUid.Value, task.Item.EntityPrototypeId);
                if (ghostPlan != null && ghostPlan.Steps.Count > 0)
                {
                    StartJob(
                        ghostUid.Value,
                        ghostPlan,
                        resetTaskTimer: false,
                        desiredEntityProtoId: task.Item.EntityPrototypeId,
                        targetDir: task.Dir);
                    return;
                }
            }

            _currentDiagnosticStatus =
                $"Waiting to place ghost for '{task.Item.EntityPrototypeId}' at ({task.Tile.X}, {task.Tile.Y}) — rechecking tile [{_currentTaskElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
            _waitTimer = 0.5f;
            return;
        }

        FailBlueprintOrJob($"Could not resolve a valid construction recipe for '{task.Item.EntityPrototypeId}'.");
    }

    private BlueprintZPhase GetEffectiveBlueprintPhase(BlueprintItemEntry item)
    {
        if (IsAtmosPipeContraption(item))
            return BlueprintZPhase.Phase1_UnderfloorUtilities;

        return item.Phase;
    }

    private int GetEffectiveBlueprintSubPriority(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        if (idLower.Contains("cablehv") || idLower.Contains("cablemv") || idLower.Contains("cableapc"))
            return 0;

        // Filters, mixers, pumps, connector ports, valves, vents, scrubbers, injectors, and disposal trunks
        // MUST be built and anchored on the tile BEFORE overlapping pipes (GasPipe / DisposalPipe).
        if (IsAtmosPipeContraption(item))
            return 1;

        if (idLower.Contains("gaspipe") || idLower.Contains("disposalpipe"))
            return 2;

        return 3 + item.LayerOrder;
    }

    private bool IsAtmosPipeContraption(BlueprintItemEntry item)
    {
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();
        var recipeLower = (item.ConstructionPrototypeId ?? string.Empty).ToLowerInvariant();

        if (idLower.Contains("gaspipe") || idLower.Contains("disposalpipe"))
            return false;

        string[] contraptionKeywords =
        {
            "gasfilter",
            "gasmixer",
            "gasport",
            "connectorport",
            "volumepump",
            "pressurepump",
            "passivegate",
            "gasvalve",
            "signalvalve",
            "pneumaticvalve",
            "passivevent",
            "ventpump",
            "ventscrubber",
            "outletinjector",
            "thermomachine",
            "heatexchanger",
            "gasradiator",
            "gasrecycler",
            "canisterport",
            "gastagger",
            "disposaltrunk",
            "disposalrouter",
            "disposaljunction",
            "disposaltagger"
        };

        foreach (var kw in contraptionKeywords)
        {
            if (idLower.Contains(kw, StringComparison.Ordinal) ||
                recipeLower.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var recipeId = item.ConstructionPrototypeId;
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
            recipeId = _solver.ResolveConstructionPrototypeForEntity(item.EntityPrototypeId);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto))
        {
            var graphId = GetStringMember(cProto, "Graph") ?? string.Empty;
            if (graphId.Equals("GasBinary", StringComparison.OrdinalIgnoreCase) ||
                graphId.Equals("GasTrinary", StringComparison.OrdinalIgnoreCase) ||
                graphId.Equals("GasUnary", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsBlueprintItemAlreadySatisfied(
        MapCoordinates tileMapCoords,
        BlueprintItemEntry item,
        Direction expectedDir,
        EntityUid? gridUid = null,
        MapGridComponent? grid = null,
        Vector2i? tileIndices = null)
    {
        if (string.IsNullOrEmpty(item.EntityPrototypeId))
            return false;

        var idLower = item.EntityPrototypeId.ToLowerInvariant();
        if ((idLower.Contains("floortile") || idLower == "tileitemsteel") &&
            gridUid.HasValue && grid != null && tileIndices.HasValue)
        {
            return _mapSystem.TryGetTileRef(gridUid.Value, grid, tileIndices.Value, out var tileRef) &&
                   !tileRef.Tile.IsEmpty;
        }

        var desiredBaseId = _solver.NormalizeBaseEntityPrototypeId(item.EntityPrototypeId);
        int desiredLayer = _solver.GetDesiredPipeLayer(item.EntityPrototypeId, item.ConstructionPrototypeId);
        bool isAtmosOrPipe = desiredLayer > 0 ||
                             IsAtmosPipeContraption(item) ||
                             idLower.Contains("pipe") ||
                             idLower.Contains("manifold");

        bool ignoreDirection = !isAtmosOrPipe &&
                               (desiredBaseId.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Wall", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Girder", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Grille", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Airlock", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Firelock", StringComparison.OrdinalIgnoreCase) ||
                                desiredBaseId.Contains("Shutter", StringComparison.OrdinalIgnoreCase) ||
                                (desiredBaseId.Contains("Window", StringComparison.OrdinalIgnoreCase) && !desiredBaseId.Contains("Directional", StringComparison.OrdinalIgnoreCase)) ||
                                desiredBaseId.Contains("Table", StringComparison.OrdinalIgnoreCase));

        string? expectedGraphId = null;
        string? expectedTargetNode = null;
        var recipeId = item.ConstructionPrototypeId;
        if (string.IsNullOrEmpty(recipeId) || !_protoMan.HasIndex<ConstructionPrototype>(recipeId))
            recipeId = _solver.ResolveConstructionPrototypeForEntity(item.EntityPrototypeId);

        if (!string.IsNullOrEmpty(recipeId) &&
            _protoMan.TryIndex<ConstructionPrototype>(recipeId, out var cProto))
        {
            expectedGraphId = GetStringMember(cProto, "Graph");
            expectedTargetNode = GetStringMember(cProto, "TargetNode");
            if (GetMemberValue(cProto, "CanRotate") is false)
                ignoreDirection = true;
        }

        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained);
        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            var xform = Transform(uid);
            if (!xform.Anchored)
                continue;

            if (GetBoolMember(xform, "NoRot"))
                ignoreDirection = true;

            var protoId = MetaData(uid).EntityPrototype?.ID;
            if (string.IsNullOrEmpty(protoId))
                continue;

            var entBaseId = _solver.NormalizeBaseEntityPrototypeId(protoId);
            bool protoMatched = string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(entBaseId, desiredBaseId, StringComparison.OrdinalIgnoreCase);

            // Also match board-constructed Airlocks (e.g. Airlock built from any DoorElectronics) or completed graph target nodes
            if (!protoMatched &&
                !isAtmosOrPipe &&
                !IsGhostOrUnfinishedFrame(uid) &&
                !string.IsNullOrEmpty(expectedGraphId) &&
                _solver.TryResolveGraphAndNode(uid, out var uGraph, out var uNode, out _) &&
                uGraph != null && uNode != null &&
                string.Equals(uGraph.ID, expectedGraphId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(expectedGraphId, "Airlock", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(expectedTargetNode) &&
                     string.Equals(uNode.Name, expectedTargetNode, StringComparison.OrdinalIgnoreCase)))
                {
                    protoMatched = true;
                }
            }

            if (!protoMatched)
                continue;

            if (!ignoreDirection && xform.LocalRotation.GetCardinalDir() != expectedDir)
                continue;

            if (isAtmosOrPipe)
            {
                int actualLayer = _solver.DetectEntityPipeLayer(uid, out _);
                if (actualLayer != desiredLayer)
                    continue;
            }

            return true;
        }

        return false;
    }

    private EntityUid? FindMatchingFrameOrGhostOnTile(
        MapCoordinates tileMapCoords,
        BlueprintItemEntry item,
        Direction expectedDir,
        string? expectedRecipeId)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained).ToList();
        EntityUid? matchedGhost = null;
        EntityUid? unanchoredOrWrongLayerMatch = null;

        var desiredBaseId = _solver.NormalizeBaseEntityPrototypeId(item.EntityPrototypeId ?? string.Empty);
        int desiredLayer = _solver.GetDesiredPipeLayer(item.EntityPrototypeId, item.ConstructionPrototypeId);

        foreach (var uid in entities)
        {
            if (!Exists(uid) || Deleted(uid))
                continue;

            if (HasGhostComponent(uid))
            {
                var ghostComp = FindComponentByName(uid, "ConstructionGhostComponent");
                var ghostProto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
                var ghostProtoId = ghostProto?.ID ?? GetStringMember(ghostComp, "Prototype");
                var ghostDir = Transform(uid).LocalRotation.GetCardinalDir();

                if (!string.IsNullOrEmpty(expectedRecipeId) &&
                    string.Equals(ghostProtoId, expectedRecipeId, StringComparison.OrdinalIgnoreCase) &&
                    ghostDir == expectedDir &&
                    matchedGhost == null)
                {
                    matchedGhost = uid;
                }
                else
                {
                    // Clear mismatched ghost so it doesn't block multi-layer placement on this tile
                    ClearGhostEntity(uid);
                }
                continue;
            }

            // Only match an existing non-ghost pipe/contraption if it is UNANCHORED (still being built on this tile).
            // An already-anchored structure on another layer belongs to that other layer's blueprint entry and must NEVER be reused!
            var xform = Transform(uid);
            if (!xform.Anchored)
            {
                var protoId = MetaData(uid).EntityPrototype?.ID ?? string.Empty;
                if (!string.IsNullOrEmpty(protoId) && !string.IsNullOrEmpty(desiredBaseId))
                {
                    var entBaseId = _solver.NormalizeBaseEntityPrototypeId(protoId);
                    bool isContraption = IsAtmosPipeContraption(item);
                    bool protoMatches = isContraption
                        ? (string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(entBaseId, desiredBaseId, StringComparison.OrdinalIgnoreCase))
                        : string.Equals(protoId, item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase);

                    if (protoMatches && xform.LocalRotation.GetCardinalDir() == expectedDir)
                    {
                        int actualLayer = _solver.DetectEntityPipeLayer(uid, out _);
                        if (actualLayer == desiredLayer || isContraption)
                        {
                            unanchoredOrWrongLayerMatch = uid;
                        }
                    }
                }
            }

            if (IsGhostOrUnfinishedFrame(uid) &&
                _solver.TryResolveGraphAndNode(uid, out var frameGraph, out _, out _) &&
                frameGraph != null)
            {
                if (!string.IsNullOrEmpty(expectedRecipeId) &&
                    _protoMan.TryIndex<ConstructionPrototype>(expectedRecipeId, out var cProto))
                {
                    var expectedGraphId = GetStringMember(cProto, "Graph");
                    if (!string.Equals(frameGraph.ID, expectedGraphId, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                return uid;
            }
        }

        return unanchoredOrWrongLayerMatch ?? matchedGhost;
    }

    private void ClearGhostsOnTile(MapCoordinates tileMapCoords)
    {
        var entities = _lookup.GetEntitiesInRange(tileMapCoords, 0.35f, LookupFlags.Uncontained).ToList();
        foreach (var uid in entities)
        {
            if (Exists(uid) && HasGhostComponent(uid))
                ClearGhostEntity(uid);
        }
    }

    private void ClearGhostEntity(EntityUid ghostUid)
    {
        if (!Exists(ghostUid) || !HasGhostComponent(ghostUid))
            return;

        try
        {
            var ghostComp = FindComponentByName(ghostUid, "ConstructionGhostComponent");
            int ghostId = ghostComp != null && GetMemberValue(ghostComp, "GhostId") is int gid
                ? gid
                : ghostUid.GetHashCode();
            _clientConstruction.ClearGhost(ghostId);
        }
        catch
        {
            // Ignore if ClearGhost fails
        }

        if (Exists(ghostUid) && IsClientSide(ghostUid))
        {
            EntityManager.QueueDeleteEntity(ghostUid);
        }
    }

    private static bool IsDirectStackBlueprintItem(
        BlueprintItemEntry item,
        out string? stackProtoOrType,
        out string? matId)
    {
        stackProtoOrType = null;
        matId = null;
        var idLower = (item.EntityPrototypeId ?? string.Empty).ToLowerInvariant();

        if (idLower.Contains("cablehv"))
        {
            stackProtoOrType = "CableHVStack";
            matId = "CableHV";
            return true;
        }
        if (idLower.Contains("cablemv"))
        {
            stackProtoOrType = "CableMVStack";
            matId = "CableMV";
            return true;
        }
        if (idLower.Contains("cableapc"))
        {
            stackProtoOrType = "CableApcExtensionStack";
            matId = "Cable";
            return true;
        }
        if (idLower.Contains("floortile") || idLower == "tileitemsteel")
        {
            stackProtoOrType = "FloorTileItemSteel";
            matId = "FloorTile";
            return true;
        }

        return false;
    }

    private bool TryExecuteDirectStackBlueprintItem(BlueprintItemEntry item, EntityCoordinates tileCoords)
    {
        if (!IsDirectStackBlueprintItem(item, out var stackProtoOrType, out var matId) ||
            string.IsNullOrEmpty(stackProtoOrType) || string.IsNullOrEmpty(matId))
        {
            return false;
        }

        bool equipped = _itemUtil.TryEquipItem(stackProtoOrType, 1) ||
                        _itemUtil.TryEquipByPredicate(
                            uid => MatchesMaterialOrStack(uid, matId),
                            $"DirectStack:{stackProtoOrType}",
                            1) ||
                        (_player.LocalEntity is { } player &&
                         TryEquipFromDeepInventoryOrVicinity(player, uid => MatchesMaterialOrStack(uid, matId), 1));

        if (!equipped)
            return false;

        _itemUtil.EnqueueClickCoordinates(tileCoords);
        return true;
    }

    private void CompleteActivePlan()
    {
        if (_activePlan == null)
            return;

        bool wasDeconstruct = _activePlan.IsDeconstruct;
        var graphId = _activePlan.GraphId;
        var startNode = _activePlan.StartNode;
        var targetNode = _activePlan.TargetNode;
        var label = _currentTargetLabel;

        if (_blueprintQueue.Count > 0 && _activeBlueprintTask is { } bpTask &&
            Exists(bpTask.GridUid) && TryComp<MapGridComponent>(bpTask.GridUid, out var bpGrid))
        {
            var bpMap = _transform.ToMapCoordinates(_mapSystem.GridTileToLocal(bpTask.GridUid, bpGrid, bpTask.Tile));
            if (IsBlueprintItemAlreadySatisfied(bpMap, bpTask.Item, bpTask.Dir, bpTask.GridUid, bpGrid, bpTask.Tile))
            {
                for (int i = _blueprintQueue.Count - 1; i >= 0; i--)
                {
                    var q = _blueprintQueue[i];
                    if (q.GridUid == bpTask.GridUid &&
                        q.Tile == bpTask.Tile &&
                        string.Equals(q.Item.EntityPrototypeId, bpTask.Item.EntityPrototypeId, StringComparison.OrdinalIgnoreCase))
                    {
                        _blueprintQueue.RemoveAt(i);
                        _completedBlueprintTasksCount++;
                        break;
                    }
                }
                _activeBlueprintTask = null;
                _currentTaskElapsed = 0f;
            }
        }

        _log.Info("AutoConstruct plan completed.");
        FinishCurrentPlan(cancelled: false, clearGhost: false);

        if (_blueprintQueue.Count == 0 && !_wiresSolver.IsSolving)
        {
            SetHudBanner(
                wasDeconstruct ? "[AutoDeconstruct Complete]" : "[AutoBuild Complete]",
                $"Finished {(wasDeconstruct ? "deconstructing" : "building")} {label} ({graphId}: {startNode} -> {targetNode}).",
                isError: false,
                duration: 2.5f);
        }
    }

    private void AdvanceToNextStep()
    {
        _stepIndex++;
        _rescanTimer = 0f;
        _stepTotalElapsed = 0f;
        _stepRetryCount = 0;
        _equipRetryCount = 0;
        _currentDiagnosticStatus = null;
        _phase = StepExecutionPhase.PrepareAndEquip;
    }

    private void ExecuteCurrentStep(EntityUid player, float frameTime)
    {
        if (_activePlan == null)
            return;

        if (_stepIndex >= _activePlan.Steps.Count)
        {
            CompleteActivePlan();
            return;
        }

        // Track target across entity replacements (e.g. Ghost -> Girder -> WallSolid, Console -> ComputerFrame, or WallSolid -> Girder)
        if (!Exists(_currentTarget) || Deleted(_currentTarget))
        {
            if (TryRelocateTargetEntity(out var replacement))
            {
                _currentTarget = replacement;
                _targetCoordinates = Transform(replacement).Coordinates;
                _targetMapCoords = _transform.GetMapCoordinates(replacement);
                _rescanTimer = 0f;
                _stepRetryCount = 0;
                _equipRetryCount = 0;
                _currentDiagnosticStatus = null;

                if (_phase == StepExecutionPhase.WaitForCompletion)
                {
                    AdvanceStepOnEntityReplacement();
                }

                if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
                {
                    _log.Info("AutoConstruct plan completed after entity replacement.");
                    CompleteActivePlan();
                    return;
                }

                _stepTotalElapsed = 0f;
                _currentTaskElapsed = 0f;
                _phase = StepExecutionPhase.PrepareAndEquip;
                _waitTimer = SettleDelay;
                return;
            }
            else
            {
                // If deconstructing to "start" / "destroyed" and the entity was deleted on the final step, we're done!
                if (_activePlan.IsDeconstruct && _stepIndex >= _activePlan.Steps.Count - 1)
                {
                    _log.Info("Target deconstructed and deleted.");
                    CompleteActivePlan();
                    return;
                }

                // If building a ghost step and the ghost was deleted, try re-spawning the ghost so we don't drop the build
                var curMissingStep = _activePlan.Steps[_stepIndex];
                if (curMissingStep.Kind == ConstructionActionKind.StartGhostConstruction &&
                    !string.IsNullOrEmpty(curMissingStep.PrototypeOrQualityOrTag) &&
                    _protoMan.TryIndex<ConstructionPrototype>(curMissingStep.PrototypeOrQualityOrTag, out var respawnCProto) &&
                    _stepTotalElapsed < JobTimeoutSeconds)
                {
                    if (_clientConstruction.TrySpawnGhost(respawnCProto, _targetCoordinates, _targetDirection, out var respawnedGhost) &&
                        respawnedGhost.HasValue && Exists(respawnedGhost.Value))
                    {
                        _currentTarget = respawnedGhost.Value;
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        _waitTimer = 0.35f;
                        return;
                    }
                }

                _rescanTimer += frameTime;
                _currentDiagnosticStatus = $"Waiting for replacement entity to spawn on tile ({_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                if (_stepTotalElapsed >= JobTimeoutSeconds || _rescanTimer >= TargetRescanTimeout)
                {
                    FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: target entity disappeared and no replacement spawned on tile.");
                }
                return;
            }
        }
        else
        {
            _targetCoordinates = Transform(_currentTarget).Coordinates;
            _targetMapCoords = _transform.GetMapCoordinates(_currentTarget);
        }

        var step = _activePlan.Steps[_stepIndex];

        // Skip redundant door-weld step if door is already welded shut
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsDoorWelded(_currentTarget))
        {
            AdvanceToNextStep();
            return;
        }

        // Skip redundant wire-panel open step if panel is already open
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            _wiresSolver.IsWirePanelOpen(_currentTarget))
        {
            AdvanceToNextStep();
            return;
        }

        // Skip redundant board-pry step on MachineFrame if no board is inside
        if (step.Kind == ConstructionActionKind.UseTool &&
            step.Description.StartsWith("Pry out board", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget) &&
            !_solver.HasMachineFrameBoard(_currentTarget))
        {
            AdvanceToNextStep();
            return;
        }

        // Delegate BUI wire-cutting step (or Cutting step when open wire panel still has uncut BUI wires) to RobustaWiresSolver
        if (step.Kind == ConstructionActionKind.CutWires ||
            (step.Kind == ConstructionActionKind.UseTool &&
             string.Equals(step.PrototypeOrQualityOrTag, "Cutting", StringComparison.OrdinalIgnoreCase) &&
             Exists(_currentTarget) &&
             _wiresSolver.HasOpenWiresPanelWithUncutBuiWires(_currentTarget)))
        {
            ExecuteDelegatedCutWiresStep(step);
            return;
        }

        // Skip redundant anchor/unanchor step if entity is already in the desired anchored state
        if (step.Kind == ConstructionActionKind.UseTool &&
            string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
            Exists(_currentTarget))
        {
            bool isAnchored = Transform(_currentTarget).Anchored;
            if (step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase) && !isAnchored)
            {
                AdvanceToNextStep();
                return;
            }
            if (step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase) && isAnchored)
            {
                AdvanceToNextStep();
                return;
            }
        }

        // Skip redundant pipe-layer screwdriver step if entity is already on that pipe layer
        if (TryGetStepTargetPipeLayer(step, out int desiredStepLayer) &&
            Exists(_currentTarget) &&
            _solver.DetectEntityPipeLayer(_currentTarget, out _) == desiredStepLayer)
        {
            AdvanceToNextStep();
            return;
        }

        switch (_phase)
        {
            case StepExecutionPhase.PrepareAndEquip:
            {
                if (!PrepareAndEquipForStep(player, step))
                {
                    if (_equipRetryCount == 0)
                        TryOpenWornStorageSlots(player);

                    _equipRetryCount++;
                    var reqDesc = FormatStepRequirement(step);
                    _currentDiagnosticStatus = $"Searching inventory/nearby for {reqDesc} (Attempt {_equipRetryCount}, {_stepTotalElapsed:0.0}s)...";

                    if (_stepTotalElapsed < 3.5f)
                    {
                        _waitTimer = 0.45f;
                        return;
                    }

                    FailBlueprintOrJob($"Missing required {reqDesc} for step {_stepIndex + 1}/{_activePlan.Steps.Count} ('{step.Description}').");
                    return;
                }

                _equipRetryCount = 0;

                if (step.Kind is ConstructionActionKind.UseTool or ConstructionActionKind.CutWires)
                {
                    _welderIgniteRetries = 0;
                    _multiToolCycleRetries = 0;
                    _phase = StepExecutionPhase.EnsureWelderLit;
                }
                else
                {
                    _phase = StepExecutionPhase.PerformInteraction;
                }
                break;
            }

            case StepExecutionPhase.EnsureWelderLit:
            {
                var held = _hands.GetActiveItem(player);
                if (held == null || !Exists(held.Value))
                {
                    _phase = StepExecutionPhase.PrepareAndEquip;
                    return;
                }

                var requiredQuality = step.PrototypeOrQualityOrTag ?? "Cutting";

                // 1. Automatically cycle multi-tools (e.g. Jaws of Life or Power Drill) to the required tool quality
                if (!string.IsNullOrEmpty(requiredQuality) &&
                    !IsToolReadyForQuality(held.Value, requiredQuality))
                {
                    if (!HasOrCanCycleToolQuality(held.Value, requiredQuality))
                    {
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    _welderIgniteRetries++;
                    _currentDiagnosticStatus = $"Cycling {GetEntityDisplayName(held.Value)} to '{requiredQuality}' mode (Attempt {_welderIgniteRetries})...";
                    if (_welderIgniteRetries > 10 && _stepTotalElapsed >= 6f)
                    {
                        FailBlueprintOrJob($"Could not cycle multi-tool {GetEntityDisplayName(held.Value)} to '{requiredQuality}' mode for '{step.Description}'.");
                        return;
                    }

                    RaisePredictiveEvent(new RequestUseInHandEvent());
                    _waitTimer = 0.3f;
                    return;
                }

                // 2. Ignite welder if this is a Welding step
                if (string.Equals(requiredQuality, "Welding", StringComparison.OrdinalIgnoreCase) &&
                    RequiresWelderIgnition(held.Value))
                {
                    if (IsWelderLit(held.Value))
                    {
                        _litWelderUid = held.Value;
                        _welderIgniteRetries = 0;
                        _phase = StepExecutionPhase.PerformInteraction;
                        break;
                    }

                    _welderIgniteRetries++;
                    _currentDiagnosticStatus = $"Igniting {GetEntityDisplayName(held.Value)} (Attempt {_welderIgniteRetries})...";
                    if (_welderIgniteRetries > 10 && _stepTotalElapsed >= 6f)
                    {
                        FailBlueprintOrJob($"Could not ignite welder {GetEntityDisplayName(held.Value)} for '{step.Description}' (check welding fuel).");
                        return;
                    }

                    RaisePredictiveEvent(new RequestUseInHandEvent());
                    _litWelderUid = held.Value;
                    _waitTimer = 0.35f;
                    return;
                }

                _welderIgniteRetries = 0;
                _phase = StepExecutionPhase.PerformInteraction;
                break;
            }

            case StepExecutionPhase.PerformInteraction:
            {
                _sawDoAfterForStep = false;
                _doAfterCancelledForStep = false;
                _stepWaitElapsed = 0f;
                _postDoAfterTimer = 0f;

                if (step.Kind == ConstructionActionKind.StartGhostConstruction)
                {
                    if (!Exists(_currentTarget) || !HasGhostComponent(_currentTarget))
                    {
                        if (!string.IsNullOrEmpty(step.PrototypeOrQualityOrTag) &&
                            _protoMan.TryIndex<ConstructionPrototype>(step.PrototypeOrQualityOrTag, out var ghostCProto) &&
                            _clientConstruction.TrySpawnGhost(ghostCProto, _targetCoordinates, _targetDirection, out var newGhost) &&
                            newGhost.HasValue && Exists(newGhost.Value))
                        {
                            _currentTarget = newGhost.Value;
                        }
                    }

                    if (Exists(_currentTarget) && HasGhostComponent(_currentTarget))
                    {
                        StartGhostConstruction(_currentTarget);
                    }
                    _waitTimer = SettleDelay;
                    _phase = StepExecutionPhase.WaitForCompletion;
                    return;
                }

                _stepStartTarget = _currentTarget;
                _stepStartNode = _solver.TryResolveGraphAndNode(_currentTarget, out _, out var sNode, out _) && sNode != null
                    ? sNode.Name
                    : null;
                var activeHeldAtStart = _hands.GetActiveItem(player);
                _stepStartHeldItem = activeHeldAtStart;
                _stepStartHeldStackCount = activeHeldAtStart.HasValue && TryComp<StackComponent>(activeHeldAtStart.Value, out var startStack)
                    ? startStack.Count
                    : (activeHeldAtStart.HasValue ? 1 : 0);

                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _wiresSolver.IsDoorOpen(_currentTarget))
                {
                    _currentDiagnosticStatus = "Closing open door before welding shut...";
                    _itemUtil.EnqueueClickEntity(_currentTarget);
                    _waitTimer = 0.6f;
                    _phase = StepExecutionPhase.WaitForCompletion;
                    return;
                }

                _itemUtil.EnqueueClickEntity(_currentTarget);
                _waitTimer = Math.Max(SettleDelay, step.DoAfterDelay > 0f ? 0.35f : 0.2f);
                _phase = StepExecutionPhase.WaitForCompletion;
                break;
            }

            case StepExecutionPhase.WaitForCompletion:
            {
                // 1. If we started a ghost construction, keep rechecking and retrying until the ghost is replaced by the real entity (up to 60s)
                if (step.Kind == ConstructionActionKind.StartGhostConstruction)
                {
                    if (TryRelocateNonGhostTarget(out var spawnedStructure))
                    {
                        _currentTarget = spawnedStructure;
                        _targetCoordinates = Transform(spawnedStructure).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(spawnedStructure);
                        _rescanTimer = 0f;
                        _stepTotalElapsed = 0f;
                        _currentTaskElapsed = 0f;
                        _stepRetryCount = 0;
                        _equipRetryCount = 0;
                        _currentDiagnosticStatus = null;
                        AdvancePastGhostStartSteps();
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    _rescanTimer += frameTime;
                    if (_stepTotalElapsed >= JobTimeoutSeconds)
                    {
                        FailBlueprintOrJob(
                            $"Timed out after {JobTimeoutSeconds:0}s waiting for construction ghost ({step.PrototypeOrQualityOrTag}) to spawn structure (tile obstructed, out of range, or missing material).");
                        return;
                    }

                    if (!_sawDoAfterForStep && _rescanTimer >= StepRetryInterval)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Waiting for ghost ({step.PrototypeOrQualityOrTag}) to spawn structure (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    if (_sawDoAfterForStep && _rescanTimer >= StepRetryInterval * 1.5f)
                    {
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Re-checking ghost structure spawn ({step.PrototypeOrQualityOrTag}) (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                    }
                    return;
                }

                // 2. For steps with a DoAfter delay (e.g. Welding, Wrenching, Prying, Screwing), ensure the DoAfter
                // actually started and finished on the server before swapping tools or advancing to the next step.
                if (step.DoAfterDelay > 0f)
                {
                    _stepWaitElapsed += frameTime;

                    if (_doAfterCancelledForStep)
                    {
                        if (_stepTotalElapsed >= JobTimeoutSeconds)
                        {
                            FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s because DoAfter for '{step.Description}' kept getting interrupted.");
                            return;
                        }

                        _doAfterCancelledForStep = false;
                        _sawDoAfterForStep = false;
                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"DoAfter interrupted on '{step.Description}' — retrying (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _waitTimer = 0.45f;
                        _phase = StepExecutionPhase.PrepareAndEquip;
                        return;
                    }

                    if (!_sawDoAfterForStep)
                    {
                        bool stepAlreadyProgressed = !Exists(_currentTarget) ||
                                                     Deleted(_currentTarget) ||
                                                     _currentTarget != _stepStartTarget;

                        if (!stepAlreadyProgressed &&
                            step.Kind == ConstructionActionKind.UseTool &&
                            string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
                            Exists(_currentTarget))
                        {
                            bool isAnchored = Transform(_currentTarget).Anchored;
                            bool wantsUnanchor = step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase);
                            bool wantsAnchor = step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase);
                            if ((wantsUnanchor && !isAnchored) || (wantsAnchor && isAnchored))
                                stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed &&
                            step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
                            Exists(_currentTarget) &&
                            _wiresSolver.IsWirePanelOpen(_currentTarget))
                        {
                            stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed &&
                            step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                            Exists(_currentTarget) &&
                            _wiresSolver.IsDoorWelded(_currentTarget))
                        {
                            stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed &&
                            TryGetStepTargetPipeLayer(step, out int checkPipeLayer))
                        {
                            if (Exists(_currentTarget) &&
                                _solver.DetectEntityPipeLayer(_currentTarget, out _) == checkPipeLayer)
                            {
                                stepAlreadyProgressed = true;
                            }
                            else if (TryRelocateReplacementEntity(_stepStartTarget, out var repPipeEnt) &&
                                     _solver.DetectEntityPipeLayer(repPipeEnt, out _) == checkPipeLayer)
                            {
                                _currentTarget = repPipeEnt;
                                _targetCoordinates = Transform(repPipeEnt).Coordinates;
                                _targetMapCoords = _transform.GetMapCoordinates(repPipeEnt);
                                stepAlreadyProgressed = true;
                            }
                        }

                        if (!stepAlreadyProgressed &&
                            Exists(_currentTarget) &&
                            _solver.TryResolveGraphAndNode(_currentTarget, out _, out var checkNode, out _) &&
                            checkNode != null &&
                            !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                            !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(checkNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                        {
                            stepAlreadyProgressed = true;
                        }

                        if (!stepAlreadyProgressed && _stepWaitElapsed < Math.Min(0.95f, step.DoAfterDelay + 0.45f))
                        {
                            return;
                        }

                        if (!stepAlreadyProgressed && step.DoAfterDelay >= 0.45f)
                        {
                            if (_stepTotalElapsed >= JobTimeoutSeconds)
                            {
                                FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' never started its DoAfter.");
                                return;
                            }

                            _stepRetryCount++;
                            _rescanTimer = 0f;
                            _currentDiagnosticStatus =
                                $"Retrying '{step.Description}' — waiting for DoAfter to start (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                            _phase = StepExecutionPhase.PrepareAndEquip;
                            return;
                        }
                    }
                    else
                    {
                        // Give server-side DoAfter completion and node/entity transition a brief settle buffer
                        // after the client DoAfter finishes so tool swapping never cancels the server's final tick.
                        _postDoAfterTimer += frameTime;
                        if (_postDoAfterTimer < 0.25f)
                            return;
                    }
                }

                // Verify DoorWelded transitions actually took effect before advancing
                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Weld door shut", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    !_wiresSolver.IsDoorWelded(_currentTarget))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= StepRetryInterval)
                    {
                        if (_stepTotalElapsed >= JobTimeoutSeconds)
                        {
                            FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not weld the door shut.");
                            return;
                        }

                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                    }
                    return;
                }

                // Verify WirePanel open transitions actually took effect before advancing
                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Open wire panel", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    !_wiresSolver.IsWirePanelOpen(_currentTarget))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= StepRetryInterval)
                    {
                        if (_stepTotalElapsed >= JobTimeoutSeconds)
                        {
                            FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not open the wire panel.");
                            return;
                        }

                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                    }
                    return;
                }

                // Verify MachineFrame board/parts pry-out actually took effect before cutting wires
                if (step.Kind == ConstructionActionKind.UseTool &&
                    step.Description.StartsWith("Pry out board", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _solver.HasMachineFrameBoard(_currentTarget))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= StepRetryInterval)
                    {
                        if (_stepTotalElapsed >= JobTimeoutSeconds)
                        {
                            FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not eject board from machine frame.");
                            return;
                        }

                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                    }
                    return;
                }

                // Verify item/material insertions actually consumed or inserted the held item before stowing/advancing
                if (step.Kind is ConstructionActionKind.InsertPrototype
                              or ConstructionActionKind.InsertComponent
                              or ConstructionActionKind.InsertTag
                              or ConstructionActionKind.AddMaterial &&
                    _stepStartHeldItem.HasValue &&
                    Exists(_stepStartHeldItem.Value) &&
                    _hands.GetActiveItem(player) == _stepStartHeldItem)
                {
                    int curCount = TryComp<StackComponent>(_stepStartHeldItem.Value, out var curSt) ? curSt.Count : 1;
                    bool consumed = curCount < _stepStartHeldStackCount;
                    if (!consumed &&
                        (string.Equals(step.PrototypeOrQualityOrTag, "MachineBoard", StringComparison.OrdinalIgnoreCase) ||
                         _solver.IsMachineBoardEntity(_stepStartHeldItem.Value)) &&
                        Exists(_currentTarget) &&
                        _solver.HasMachineFrameBoard(_currentTarget))
                    {
                        consumed = true;
                    }

                    if (!consumed &&
                        Exists(_currentTarget) &&
                        !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                        !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                        _solver.TryResolveGraphAndNode(_currentTarget, out _, out var postInsertNode, out _) &&
                        postInsertNode != null &&
                        string.Equals(postInsertNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                    {
                        consumed = true;
                    }

                    if (!consumed)
                    {
                        _rescanTimer += frameTime;
                        if (_rescanTimer >= 0.85f)
                        {
                            _rescanTimer = 0f;
                            _stepRetryCount++;

                            // If inserting a part/material into a MachineFrame that already has its board,
                            // that specific requirement slot may already be satisfied after 2 attempts; advance to the next step.
                            if (_stepRetryCount > 2 &&
                                string.Equals(_activePlan.GraphId, "Machine", StringComparison.OrdinalIgnoreCase) &&
                                Exists(_currentTarget) &&
                                _solver.HasMachineFrameBoard(_currentTarget) &&
                                !_solver.IsMachineBoardEntity(_stepStartHeldItem.Value))
                            {
                                AdvanceToNextStep();
                                return;
                            }

                            if (_stepTotalElapsed >= JobTimeoutSeconds)
                            {
                                FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' did not consume or insert the held item.");
                                return;
                            }

                            _currentDiagnosticStatus =
                                $"Retrying '{step.Description}' — item not yet inserted (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                            _phase = StepExecutionPhase.PrepareAndEquip;
                        }
                        return;
                    }
                }

                // Verify Unanchor / Anchor transitions actually took effect before advancing
                if (step.Kind == ConstructionActionKind.UseTool &&
                    string.Equals(step.PrototypeOrQualityOrTag, "Anchoring", StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget))
                {
                    bool isAnchored = Transform(_currentTarget).Anchored;
                    bool wantsUnanchor = step.Description.StartsWith("Unanchor", StringComparison.OrdinalIgnoreCase);
                    bool wantsAnchor = step.Description.StartsWith("Anchor frame", StringComparison.OrdinalIgnoreCase);

                    if ((wantsUnanchor && isAnchored) || (wantsAnchor && !isAnchored))
                    {
                        _rescanTimer += frameTime;
                        if (_rescanTimer >= StepRetryInterval)
                        {
                            // If anchoring on Primary failed and the next step(s) adjust pipe layer via Screwdriver,
                            // swap the pipe-layer Screwdriver step(s) before Anchor frame so it switches layer while unanchored first.
                            if (wantsAnchor && !isAnchored &&
                                _stepIndex + 1 < _activePlan.Steps.Count &&
                                TryGetStepTargetPipeLayer(_activePlan.Steps[_stepIndex + 1], out _))
                            {
                                var anchorStep = _activePlan.Steps[_stepIndex];
                                int insertIdx = _stepIndex;
                                while (insertIdx + 1 < _activePlan.Steps.Count &&
                                       TryGetStepTargetPipeLayer(_activePlan.Steps[insertIdx + 1], out _))
                                {
                                    _activePlan.Steps[insertIdx] = _activePlan.Steps[insertIdx + 1];
                                    insertIdx++;
                                }
                                _activePlan.Steps[insertIdx] = anchorStep;
                                _rescanTimer = 0f;
                                _stepRetryCount = 0;
                                _equipRetryCount = 0;
                                _currentDiagnosticStatus = "Switching pipe layer via Screwdriver before anchoring...";
                                _phase = StepExecutionPhase.PrepareAndEquip;
                                return;
                            }

                            if (_stepTotalElapsed >= JobTimeoutSeconds)
                            {
                                FailBlueprintOrJob(
                                    $"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' could not {(wantsAnchor ? "anchor" : "unanchor")} {_currentTargetLabel} (tile layer occupied or obstructed).");
                                return;
                            }

                            _rescanTimer = 0f;
                            _stepRetryCount++;
                            _currentDiagnosticStatus =
                                $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                            _phase = StepExecutionPhase.PrepareAndEquip;
                        }
                        return;
                    }
                }

                // Verify pipe-layer Screwdriver transitions (both in-place and entity-replacement)
                if (TryGetStepTargetPipeLayer(step, out int verifyPipeLayer))
                {
                    if ((!Exists(_currentTarget) || Deleted(_currentTarget)) &&
                        TryRelocateTargetEntity(out var relocatedPipeEnt))
                    {
                        _currentTarget = relocatedPipeEnt;
                        _targetCoordinates = Transform(relocatedPipeEnt).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(relocatedPipeEnt);
                    }
                    else if (Exists(_currentTarget) &&
                             _currentTarget == _stepStartTarget &&
                             _solver.DetectEntityPipeLayer(_currentTarget, out _) != verifyPipeLayer &&
                             TryRelocateReplacementEntity(_stepStartTarget, out var replacedPipeEnt))
                    {
                        _currentTarget = replacedPipeEnt;
                        _targetCoordinates = Transform(replacedPipeEnt).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(replacedPipeEnt);
                    }

                    if (Exists(_currentTarget) &&
                        _solver.DetectEntityPipeLayer(_currentTarget, out _) != verifyPipeLayer)
                    {
                        _rescanTimer += frameTime;
                        if (_rescanTimer >= StepRetryInterval)
                        {
                            if (_stepTotalElapsed >= JobTimeoutSeconds)
                            {
                                FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: '{step.Description}' did not change pipe layer on {_currentTargetLabel}.");
                                return;
                            }

                            _rescanTimer = 0f;
                            _stepRetryCount++;
                            _currentDiagnosticStatus =
                                $"Retrying '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                            _phase = StepExecutionPhase.PrepareAndEquip;
                        }
                        return;
                    }
                }

                // If this step is the final step of an edge that replaces/deletes the entity
                // (e.g. Console -> ComputerFrame, Machine -> MachineFrame, Wall -> Girder, or -> start),
                // wait for the entity replacement before advancing.
                bool isFinalStepOfEdge =
                    !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                    (_stepIndex + 1 >= _activePlan.Steps.Count ||
                     !string.Equals(_activePlan.Steps[_stepIndex + 1].ExpectedNextNode, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase));

                bool expectsEntityReplacement =
                    isFinalStepOfEdge &&
                    Exists(_currentTarget) &&
                    _solver.DoesEdgeReplaceOrDeleteEntity(_currentTarget, _activePlan.GraphId, step.ExpectedNextNode);

                if (expectsEntityReplacement && Exists(_currentTarget) && _currentTarget == _stepStartTarget)
                {
                    if (TryRelocateReplacementEntity(_stepStartTarget, out var spawnedReplacement))
                    {
                        _currentTarget = spawnedReplacement;
                        _targetCoordinates = Transform(spawnedReplacement).Coordinates;
                        _targetMapCoords = _transform.GetMapCoordinates(spawnedReplacement);
                        AdvanceToNextStep();
                        return;
                    }

                    _rescanTimer += frameTime;
                    if (_rescanTimer >= StepRetryInterval)
                    {
                        if (_stepTotalElapsed >= JobTimeoutSeconds)
                        {
                            FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' did not transition to node '{step.ExpectedNextNode}'.");
                            return;
                        }

                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Waiting for '{step.Description}' -> '{step.ExpectedNextNode}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                    }
                    return;
                }

                // If this step transitions an in-place frame node (e.g. MachineFrame missingWires <-> machineFrame,
                // ComputerFrame stages 0..4, or Airlock panel security nodes) without replacing the entity, verify the node changed before advancing.
                if (isFinalStepOfEdge &&
                    !expectsEntityReplacement &&
                    !string.IsNullOrEmpty(step.ExpectedNextNode) &&
                    !string.Equals(step.ExpectedNextNode, _stepStartNode, StringComparison.OrdinalIgnoreCase) &&
                    Exists(_currentTarget) &&
                    _solver.CanVerifyInPlaceNodeTransition(_currentTarget, _activePlan.GraphId) &&
                    _solver.TryResolveGraphAndNode(_currentTarget, out _, out var currentFrameNode, out _) &&
                    currentFrameNode != null &&
                    !string.Equals(currentFrameNode.Name, step.ExpectedNextNode, StringComparison.OrdinalIgnoreCase))
                {
                    _rescanTimer += frameTime;
                    if (_rescanTimer >= StepRetryInterval)
                    {
                        if (_stepTotalElapsed >= JobTimeoutSeconds)
                        {
                            FailBlueprintOrJob($"Timed out after {JobTimeoutSeconds:0}s: step '{step.Description}' did not reach node '{step.ExpectedNextNode}'.");
                            return;
                        }

                        _rescanTimer = 0f;
                        _stepRetryCount++;
                        _currentDiagnosticStatus =
                            $"Waiting for node '{step.ExpectedNextNode}' on '{step.Description}' (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
                        _phase = StepExecutionPhase.PrepareAndEquip;
                    }
                    return;
                }

                AdvanceToNextStep();
                break;
            }
        }
    }

    private void AdvancePastGhostStartSteps()
    {
        if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
            return;

        var ghostStep = _activePlan.Steps[_stepIndex];
        var firstNode = ghostStep.ExpectedNextNode;
        _stepIndex++;

        if (string.IsNullOrEmpty(firstNode))
            return;

        while (_stepIndex < _activePlan.Steps.Count &&
               string.Equals(_activePlan.Steps[_stepIndex].ExpectedNextNode, firstNode, StringComparison.OrdinalIgnoreCase))
        {
            _stepIndex++;
        }
    }

    private void AdvanceStepOnEntityReplacement()
    {
        if (_activePlan == null || _stepIndex >= _activePlan.Steps.Count)
            return;

        var curStep = _activePlan.Steps[_stepIndex];
        if (curStep.Kind == ConstructionActionKind.StartGhostConstruction)
        {
            AdvancePastGhostStartSteps();
            return;
        }

        // Advance past the current step that triggered the entity replacement.
        _stepIndex++;
    }

    private void ExecuteDelegatedCutWiresStep(ConstructionActionStep step)
    {
        if (!Exists(_currentTarget))
            return;

        if (!_solver.HasWiresUserInterface(_currentTarget))
        {
            if (step.Kind == ConstructionActionKind.CutWires)
                _stepIndex++;
            _phase = StepExecutionPhase.PrepareAndEquip;
            return;
        }

        if (!_delegatedWireSolveStarted)
        {
            if (_wiresSolver.AreAllWiresCut(_currentTarget) && !_wiresSolver.IsDoorBolted(_currentTarget))
            {
                if (step.Kind == ConstructionActionKind.CutWires)
                    _stepIndex++;
                _phase = StepExecutionPhase.PrepareAndEquip;
                _waitTimer = SettleDelay;
                return;
            }

            if (!_wiresSolver.TryStartSolve(
                    _currentTarget,
                    RobustaWiresSolver.CutAllIntent,
                    out var failureReason,
                    allowNestedFromConstruction: true))
            {
                _log.Warning($"Could not start wire-cut solve on {ToPrettyString(_currentTarget)}: {failureReason}.");
                FinishCurrentPlan();
                return;
            }

            _delegatedWireSolveStarted = true;
            return;
        }

        if (_wiresSolver.IsSolving)
            return;

        _delegatedWireSolveStarted = false;
        if (_wiresSolver.AreAllWiresCut(_currentTarget) && !_wiresSolver.IsDoorBolted(_currentTarget))
        {
            if (step.Kind == ConstructionActionKind.CutWires)
                _stepIndex++;
            _rescanTimer = 0f;
            _stepRetryCount = 0;
        _equipRetryCount = 0;
            _phase = StepExecutionPhase.PrepareAndEquip;
            _waitTimer = SettleDelay;
            return;
        }

        _stepRetryCount++;
        if (_stepTotalElapsed < JobTimeoutSeconds && _stepRetryCount <= 12)
        {
            _currentDiagnosticStatus = $"Retrying internal wire cutting (Attempt {_stepRetryCount + 1}, {_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s)...";
            _waitTimer = 0.5f;
            return;
        }

        FailBlueprintOrJob($"Could not cut all internal wires on {GetEntityDisplayName(_currentTarget)} (check Wirecutters/Multitool or insulated gloves).");
    }

    private bool PrepareAndEquipForStep(EntityUid player, ConstructionActionStep step)
    {
        switch (step.Kind)
        {
            case ConstructionActionKind.StartGhostConstruction:
            {
                // Equip the first required material/item from the next step so the server accepts TryStartStructureConstructionMessage
                if (_activePlan != null && _stepIndex + 1 < _activePlan.Steps.Count)
                {
                    var firstStep = _activePlan.Steps[_stepIndex + 1];
                    if (firstStep.Kind != ConstructionActionKind.StartGhostConstruction)
                        return PrepareAndEquipForStep(player, firstStep);
                }
                return true;
            }

            case ConstructionActionKind.AddMaterial:
            {
                var matId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(matId))
                    return false;

                // 1. Unified material/stack matcher (handles StackTypeId, prototype Stack, StackPrototype.Spawn, CableCoil/LV/MV/HV cables, etc.)
                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesMaterialOrStack(uid, matId),
                        $"Material:{matId}",
                        step.Amount))
                {
                    return true;
                }

                // 2. MachinePart / StockPart fallback
                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesMachinePart(uid, matId),
                        $"MaterialOrPart:{matId}",
                        step.Amount))
                {
                    return true;
                }

                // 3. Deep container (e.g. toolbox/box inside backpack, secondary containers) & interaction-range vicinity fallback
                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => MatchesMaterialOrStack(uid, matId) || MatchesMachinePart(uid, matId),
                    step.Amount);
            }

            case ConstructionActionKind.UseTool:
            case ConstructionActionKind.CutWires:
            {
                var quality = step.PrototypeOrQualityOrTag ?? "Cutting";
                if (string.IsNullOrEmpty(quality))
                    return false;

                return TryEquipToolOrMultiTool(quality, $"Tool:{quality}");
            }

            case ConstructionActionKind.InsertPrototype:
            {
                var protoId = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(protoId))
                    return false;

                if (_itemUtil.TryEquipItem(new EntProtoId(protoId), step.Amount, includeChildren: true))
                    return true;

                if (_itemUtil.TryEquipByPredicate(
                        uid => MatchesPrototypeOrParentId(uid, protoId, allowPrefixMatch: false) || HasValidTag(uid, protoId),
                        $"Prototype:{protoId}",
                        step.Amount))
                {
                    return true;
                }

                return TryEquipFromDeepInventoryOrVicinity(
                    player,
                    uid => MatchesPrototypeOrParentId(uid, protoId, allowPrefixMatch: false) || HasValidTag(uid, protoId),
                    step.Amount);
            }

            case ConstructionActionKind.InsertComponent:
            {
                var compName = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(compName))
                    return false;

                if (string.Equals(compName, "MachineBoard", StringComparison.OrdinalIgnoreCase))
                {
                    return _itemUtil.TryEquipByPredicate(
                               uid => _solver.IsMachineBoardEntity(uid),
                               "Component:MachineBoard",
                               step.Amount) ||
                           TryEquipFromDeepInventoryOrVicinity(
                               player,
                               uid => _solver.IsMachineBoardEntity(uid),
                               step.Amount);
                }

                if (compName.StartsWith("MachinePart:", StringComparison.OrdinalIgnoreCase))
                {
                    var partId = compName["MachinePart:".Length..];
                    return _itemUtil.TryEquipByPredicate(
                               uid => MatchesMachinePart(uid, partId),
                               $"MachinePart:{partId}",
                               step.Amount) ||
                           TryEquipFromDeepInventoryOrVicinity(
                               player,
                               uid => MatchesMachinePart(uid, partId),
                               step.Amount);
                }

                bool MatchesComponentRequirement(EntityUid uid)
                {
                    if (!Exists(uid))
                        return false;

                    if (EntityManager.GetComponents(uid).Any(c =>
                            c.GetType().Name.Equals(compName, StringComparison.OrdinalIgnoreCase) ||
                            c.GetType().Name.Equals(compName + "Component", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }

                    var proto = MetaData(uid).EntityPrototype;
                    if (proto != null && _itemUtil.HasPrototypeComponent(proto, compName))
                        return true;

                    return MatchesTagOrPrototype(uid, compName);
                }

                return _itemUtil.TryEquipByPredicate(
                           MatchesComponentRequirement,
                           $"Component:{compName}",
                           step.Amount) ||
                       TryEquipFromDeepInventoryOrVicinity(
                           player,
                           MatchesComponentRequirement,
                           step.Amount);
            }

            case ConstructionActionKind.InsertTag:
            {
                var tag = step.PrototypeOrQualityOrTag;
                if (string.IsNullOrEmpty(tag))
                    return false;

                return _itemUtil.TryEquipByPredicate(
                           uid => MatchesTagOrPrototype(uid, tag),
                           $"Tag:{tag}",
                           step.Amount) ||
                       TryEquipFromDeepInventoryOrVicinity(
                           player,
                           uid => MatchesTagOrPrototype(uid, tag),
                           step.Amount);
            }

            default:
                return false;
        }
    }

    private bool HasValidTag(EntityUid uid, string? tag)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(tag))
            return false;

        try
        {
            if (!_protoMan.HasIndex<TagPrototype>(tag))
                return false;

            return _tagSystem.HasTag(uid, tag);
        }
        catch
        {
            return false;
        }
    }

    private bool MatchesTagOrPrototype(EntityUid uid, string tag)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(tag))
            return false;

        if (HasValidTag(uid, tag))
            return true;

        if (MatchesPrototypeOrParentId(uid, tag, allowPrefixMatch: false))
            return true;

        if (EntityManager.GetComponents(uid).Any(c =>
                c.GetType().Name.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                c.GetType().Name.Equals(tag + "Component", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null && _itemUtil.HasPrototypeComponent(proto, tag))
            return true;

        return false;
    }

    private bool MatchesMaterialOrStack(EntityUid uid, string matId)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(matId))
            return false;

        string? liveStackType = null;
        if (TryComp<StackComponent>(uid, out var stack))
        {
            liveStackType = stack.StackTypeId.Id;
            if (string.IsNullOrEmpty(liveStackType))
                liveStackType = GetStringMember(stack, "StackTypeId") ?? GetStringMember(stack, "StackType");
        }

        if (string.IsNullOrEmpty(liveStackType) && FindComponentByName(uid, "StackComponent") is { } rawStack)
        {
            liveStackType = GetStringMember(rawStack, "StackTypeId") ?? GetStringMember(rawStack, "StackType");
        }

        if (!string.IsNullOrEmpty(liveStackType) &&
            string.Equals(liveStackType, matId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetPrototypeStackType(uid, out var protoStackType))
        {
            if (string.Equals(protoStackType, matId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrEmpty(liveStackType))
                liveStackType = protoStackType;
        }

        // Check if StackPrototype's Spawn entity prototype matches this entity or its parent prototypes
        if (_protoMan.TryIndex<StackPrototype>(matId, out var stackProto))
        {
            var spawnProtoId = GetStringMember(stackProto, "Spawn");
            if (!string.IsNullOrEmpty(spawnProtoId) &&
                MatchesPrototypeOrParentId(uid, spawnProtoId, allowPrefixMatch: true))
            {
                return true;
            }
        }

        // Handle LV / MV / HV Cable family matching
        bool wantsLvCable = string.Equals(matId, "Cable", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableLV", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableApc", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableApcExtension", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matId, "CableCoil", StringComparison.OrdinalIgnoreCase);

        if (wantsLvCable)
        {
            // Exclude MV/HV cables and non-coil items like cable cuffs, terminals, zipties, bolas
            if (!string.IsNullOrEmpty(liveStackType) &&
                (liveStackType.Contains("MV", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("HV", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (MatchesPrototypeOrParentSubstring(uid, "CableMV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableHV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableTerminal") ||
                MatchesPrototypeOrParentSubstring(uid, "Cuffs") ||
                MatchesPrototypeOrParentSubstring(uid, "Restraint") ||
                MatchesPrototypeOrParentSubstring(uid, "Ziptie") ||
                MatchesPrototypeOrParentSubstring(uid, "Bola"))
            {
                return false;
            }

            if (HasValidTag(uid, "CableCoil") ||
                HasValidTag(uid, "Cable") ||
                HasValidTag(uid, "CableLV") ||
                HasValidTag(uid, "CableApc"))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(liveStackType) &&
                (liveStackType.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("Apc", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("LV", StringComparison.OrdinalIgnoreCase) ||
                 liveStackType.Contains("Wire", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (MatchesPrototypeOrParentSubstring(uid, "CableApc") ||
                MatchesPrototypeOrParentSubstring(uid, "CableLV") ||
                MatchesPrototypeOrParentSubstring(uid, "CableStack"))
            {
                return true;
            }

            if (FindComponentByName(uid, "CablePlacerComponent") is { } cablePlacer)
            {
                var cableProto = GetStringMember(cablePlacer, "CablePrototypeId")
                                 ?? GetStringMember(cablePlacer, "CablePrototypeID")
                                 ?? GetStringMember(cablePlacer, "Cable")
                                 ?? string.Empty;
                if (!cableProto.Contains("MV", StringComparison.OrdinalIgnoreCase) &&
                    !cableProto.Contains("HV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var proto = MetaData(uid).EntityPrototype;
            if (proto != null &&
                _itemUtil.TryGetPrototypeComponent<object>(proto, "CablePlacer", out var protoPlacer) &&
                protoPlacer != null)
            {
                var cableProto = GetStringMember(protoPlacer, "CablePrototypeId")
                                 ?? GetStringMember(protoPlacer, "CablePrototypeID")
                                 ?? GetStringMember(protoPlacer, "Cable")
                                 ?? string.Empty;
                if (!cableProto.Contains("MV", StringComparison.OrdinalIgnoreCase) &&
                    !cableProto.Contains("HV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        else if (string.Equals(matId, "CableMV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(matId, "CableMVStack", StringComparison.OrdinalIgnoreCase))
        {
            if ((!string.IsNullOrEmpty(liveStackType) && liveStackType.Contains("CableMV", StringComparison.OrdinalIgnoreCase)) ||
                MatchesPrototypeOrParentSubstring(uid, "CableMV"))
            {
                return true;
            }
        }
        else if (string.Equals(matId, "CableHV", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(matId, "CableHVStack", StringComparison.OrdinalIgnoreCase))
        {
            if ((!string.IsNullOrEmpty(liveStackType) && liveStackType.Contains("CableHV", StringComparison.OrdinalIgnoreCase)) ||
                MatchesPrototypeOrParentSubstring(uid, "CableHV"))
            {
                return true;
            }
        }

        // General sheet/stack prototype naming conventions (e.g. SheetSteel, SheetGlass, MaterialPlasteel)
        if (HasComp<StackComponent>(uid) || !string.IsNullOrEmpty(liveStackType))
        {
            if (MatchesPrototypeOrParentId(uid, matId, allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Sheet{matId}", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Material{matId}", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"{matId}Stack", allowPrefixMatch: true) ||
                MatchesPrototypeOrParentId(uid, $"Part{matId}", allowPrefixMatch: true))
            {
                return true;
            }
        }

        return HasValidTag(uid, matId);
    }

    private bool TryGetPrototypeStackType(EntityUid uid, out string stackType)
    {
        stackType = string.Empty;
        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (_itemUtil.TryGetPrototypeComponent<object>(cur, "Stack", out var stackComp) && stackComp != null)
            {
                var st = GetStringMember(stackComp, "StackTypeId") ?? GetStringMember(stackComp, "StackType");
                if (!string.IsNullOrEmpty(st))
                {
                    stackType = st;
                    return true;
                }
            }

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesPrototypeOrParentId(EntityUid uid, string targetProtoId, bool allowPrefixMatch)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(targetProtoId))
            return false;

        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (string.Equals(cur.ID, targetProtoId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (allowPrefixMatch &&
                (cur.ID.StartsWith(targetProtoId, StringComparison.OrdinalIgnoreCase) ||
                 targetProtoId.StartsWith(cur.ID, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (string.Equals(parentId, targetProtoId, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool MatchesPrototypeOrParentSubstring(EntityUid uid, string substring)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(substring))
            return false;

        var proto = MetaData(uid).EntityPrototype;
        if (proto == null)
            return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<EntityPrototype>();
        queue.Enqueue(proto);
        visited.Add(proto.ID);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur.ID.Contains(substring, StringComparison.OrdinalIgnoreCase))
                return true;

            if (cur.Parents != null)
            {
                foreach (var parentId in cur.Parents)
                {
                    if (parentId.Contains(substring, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                        queue.Enqueue(parentProto);
                }
            }
        }

        return false;
    }

    private bool TryOpenWornStorageSlots(EntityUid player)
    {
        if (!Exists(player))
            return false;

        bool openedAny = false;
        string[] storageSlots = { "belt", "back", "suitstorage", "outerClothing", "pocket1", "pocket2" };
        foreach (var slotName in storageSlots)
        {
            if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) &&
                slotEnt.HasValue &&
                Exists(slotEnt.Value) &&
                HasComp<StorageComponent>(slotEnt.Value))
            {
                RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName));
                openedAny = true;
            }
        }

        return openedAny;
    }

    private bool TryEquipFromDeepInventoryOrVicinity(
        EntityUid player,
        Func<EntityUid, bool> predicate,
        int neededAmount)
    {
        if (!Exists(player) || !TryComp<HandsComponent>(player, out var hands))
            return false;

        int needed = Math.Max(1, neededAmount);

        // 1. Check within interaction range via ItemUtilitySystem's vicinity search
        var vicinityMatches = _itemUtil.FindItemsByPredicate(predicate, needed, SharedInteractionSystem.InteractionRange);
        int vicinityTotal = vicinityMatches.Sum(m => m.StackCount);
        if (vicinityMatches.Count > 0 && vicinityTotal >= needed)
        {
            var best = vicinityMatches[0];
            _itemUtil.StowActiveTrackedItem();
            if (best.Source == ItemSource.Storage && best.StorageEntity is { } storageUid)
            {
                if (!string.IsNullOrEmpty(best.SlotOrHand))
                {
                    var slotName = best.SlotOrHand;
                    _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(slotName)));
                }
                var itemUid = best.Entity;
                _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () =>
                {
                    if (!Exists(itemUid) || !Exists(storageUid))
                        return;
                    EnsureEmptyActiveHandImmediate(player);
                    RaisePredictiveEvent(new StorageInteractWithItemEvent(GetNetEntity(itemUid), GetNetEntity(storageUid)));
                });
                return true;
            }

            if (best.Source == ItemSource.Vicinity)
            {
                _itemUtil.EnqueueClickEntity(best.Entity);
                return true;
            }
        }

        // 2. Deep scan nested storage containers (e.g. toolbox/box inside backpack, secondary containers on worn items, or open containers nearby)
        var deepCandidates = new List<(EntityUid Item, EntityUid? StorageOwner, string? RootSlot, int Count)>();
        var visited = new HashSet<EntityUid>();

        void ScanContainerRecursive(EntityUid containerOwner, string? rootSlot, EntityUid? activeStorageOwner, int depth)
        {
            if (depth > 3 || !Exists(containerOwner))
                return;

            EntityUid? currentStorage = HasComp<StorageComponent>(containerOwner) ? containerOwner : activeStorageOwner;

            if (TryComp<StorageComponent>(containerOwner, out var stComp))
            {
                foreach (var ent in stComp.Container.ContainedEntities)
                {
                    if (!Exists(ent) || !visited.Add(ent))
                        continue;

                    if (predicate(ent))
                    {
                        int count = TryComp<StackComponent>(ent, out var s) ? s.Count : 1;
                        deepCandidates.Add((ent, currentStorage ?? containerOwner, rootSlot, count));
                    }

                    ScanContainerRecursive(ent, rootSlot, currentStorage ?? containerOwner, depth + 1);
                }
            }

            if (TryComp<ContainerManagerComponent>(containerOwner, out var containerMan))
            {
                foreach (var container in _container.GetAllContainers(containerOwner, containerMan))
                {
                    foreach (var ent in container.ContainedEntities)
                    {
                        if (!Exists(ent) || !visited.Add(ent))
                            continue;

                        if (predicate(ent))
                        {
                            int count = TryComp<StackComponent>(ent, out var s) ? s.Count : 1;
                            deepCandidates.Add((ent, currentStorage ?? containerOwner, rootSlot, count));
                        }

                        ScanContainerRecursive(ent, rootSlot, currentStorage ?? containerOwner, depth + 1);
                    }
                }
            }
        }

        // Scan hands recursively
        foreach (var handName in hands.Hands.Keys)
        {
            if (_container.TryGetContainer(player, handName, out var handCont) && handCont.ContainedEntities.Count > 0)
            {
                var heldEnt = handCont.ContainedEntities[0];
                ScanContainerRecursive(heldEnt, null, HasComp<StorageComponent>(heldEnt) ? heldEnt : null, 1);
            }
        }

        // Scan worn slots recursively
        string[] allSlots =
        {
            "belt", "back", "pocket1", "pocket2", "pocket3", "pocket4",
            "suitstorage", "id", "outerClothing", "gloves", "shoes",
            "head", "eyes", "ears", "mask", "neck", "jumpsuit"
        };
        foreach (var slotName in allSlots)
        {
            if (_inventory.TryGetSlotEntity(player, slotName, out var slotEnt) && slotEnt.HasValue)
            {
                ScanContainerRecursive(slotEnt.Value, slotName, HasComp<StorageComponent>(slotEnt.Value) ? slotEnt.Value : null, 1);
            }
        }

        // Also scan open StorageComponent entities within interaction range (e.g. open toolbox on the floor)
        var playerCoords = _transform.GetMapCoordinates(player);
        if (playerCoords.MapId != MapId.Nullspace)
        {
            foreach (var nearEnt in _lookup.GetEntitiesInRange(playerCoords, SharedInteractionSystem.InteractionRange, LookupFlags.Uncontained))
            {
                if (nearEnt == player || !Exists(nearEnt) || !HasComp<StorageComponent>(nearEnt))
                    continue;

                ScanContainerRecursive(nearEnt, null, nearEnt, 1);
            }
        }

        if (deepCandidates.Count == 0 || deepCandidates.Sum(c => c.Count) < needed)
            return false;

        var chosen = deepCandidates
            .OrderByDescending(c => c.Count >= needed)
            .ThenByDescending(c => c.Count)
            .First();

        _itemUtil.StowActiveTrackedItem();

        if (!string.IsNullOrEmpty(chosen.RootSlot))
        {
            var rootSlot = chosen.RootSlot;
            _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () => RaisePredictiveEvent(new OpenSlotStorageNetworkMessage(rootSlot)));
        }

        if (chosen.StorageOwner is { } storageOwner && Exists(storageOwner))
        {
            var itemToFetch = chosen.Item;
            _itemUtil.EnqueueStep(ItemUtilitySystem.StepDelay, () =>
            {
                if (!Exists(itemToFetch) || !Exists(storageOwner))
                    return;
                EnsureEmptyActiveHandImmediate(player);
                RaisePredictiveEvent(new StorageInteractWithItemEvent(GetNetEntity(itemToFetch), GetNetEntity(storageOwner)));
            });
            return true;
        }

        _itemUtil.EnqueueClickEntity(chosen.Item);
        return true;
    }

    private void EnsureEmptyActiveHandImmediate(EntityUid player)
    {
        if (!TryComp<HandsComponent>(player, out var hands))
            return;

        var activeHand = _hands.GetActiveHand(player);
        if (activeHand != null && _hands.GetActiveItem(player) == null)
            return;

        foreach (var handName in hands.Hands.Keys)
        {
            if (_container.TryGetContainer(player, handName, out var cont) && cont.ContainedEntities.Count == 0)
            {
                RaisePredictiveEvent(new RequestSetHandEvent(handName));
                return;
            }
        }
    }

    private bool MatchesMachinePart(EntityUid uid, string partId)
    {
        if (!Exists(uid) || string.IsNullOrEmpty(partId))
            return false;

        if (MatchesMaterialOrStack(uid, partId))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "MachinePartComponent", StringComparison.OrdinalIgnoreCase))
            {
                var partVal = GetMemberValue(comp, "Part") ?? GetMemberValue(comp, "PartType");
                var partStr = GetMemberValue(partVal, "Id") as string ?? partVal?.ToString();
                if (string.Equals(partStr, partId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null)
        {
            if (_itemUtil.TryGetPrototypeComponent<object>(proto, "MachinePart", out var mpComp) && mpComp != null)
            {
                var partVal = GetMemberValue(mpComp, "Part") ?? GetMemberValue(mpComp, "PartType");
                var partStr = GetMemberValue(partVal, "Id") as string ?? partVal?.ToString();
                if (string.Equals(partStr, partId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (proto.ID.Contains(partId, StringComparison.OrdinalIgnoreCase) &&
                proto.ID.Contains("StockPart", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return HasValidTag(uid, partId);
    }

    private void StartGhostConstruction(EntityUid ghostUid)
    {
        try
        {
            _clientConstruction.TryStartConstruction(ghostUid);
            return;
        }
        catch
        {
            // Fallback reflection if signature differs on older fork
        }

        try
        {
            var method = _clientConstruction.GetType().GetMethod(
                "TryStartConstruction",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                var parameters = method.GetParameters();
                var args = new object?[parameters.Length];
                if (parameters.Length > 0)
                    args[0] = ghostUid;
                method.Invoke(_clientConstruction, args);
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Reflection fallback for TryStartConstruction failed: {ex.Message}");
        }
    }

    private EntityUid? FindTargetAtCursor(
        EntityUid player,
        MapCoordinates cursorMapCoords,
        out EntityUid? outOfRangeTarget,
        out float outOfRangeDist)
    {
        outOfRangeTarget = null;
        outOfRangeDist = 0f;

        var playerPos = _transform.GetMapCoordinates(player);
        var nearby = _lookup.GetEntitiesInRange(cursorMapCoords, 0.65f, LookupFlags.Uncontained);

        // 1. Check for a ConstructionGhostComponent first
        EntityUid? bestGhost = null;
        float bestGhostDist = float.MaxValue;
        foreach (var uid in nearby)
        {
            if (!Exists(uid) || !HasGhostComponent(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            float distToCursor = (pos.Position - cursorMapCoords.Position).LengthSquared();
            if (distToCursor < bestGhostDist)
            {
                bestGhostDist = distToCursor;
                bestGhost = uid;
            }
        }

        if (bestGhost.HasValue)
            return bestGhost.Value;

        // 2. Otherwise find the topmost constructible/deconstructible entity within interaction range
        EntityUid? bestEntity = null;
        float bestScore = float.MaxValue;
        float bestOutOfRangeScore = float.MaxValue;

        foreach (var uid in nearby)
        {
            if (uid == player || !Exists(uid) || IsHiddenSubfloorEntity(uid))
                continue;

            var pos = _transform.GetMapCoordinates(uid);
            if (pos.MapId != playerPos.MapId)
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out _, out _, out _))
                continue;

            float score = (pos.Position - cursorMapCoords.Position).LengthSquared();
            if (IsSubfloorEntity(uid))
                score += 0.02f;
            else if (IsGhostOrUnfinishedFrame(uid))
                score -= 0.01f;

            float playerDist = (pos.Position - playerPos.Position).Length();
            if (playerDist > SharedInteractionSystem.InteractionRange)
            {
                if (score < bestOutOfRangeScore)
                {
                    bestOutOfRangeScore = score;
                    outOfRangeTarget = uid;
                    outOfRangeDist = playerDist;
                }
                continue;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestEntity = uid;
            }
        }

        return bestEntity;
    }

    private bool IsSubfloorEntity(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "SubFloorHideComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsHiddenSubfloorEntity(EntityUid uid)
    {
        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "SubFloorHideComponent", StringComparison.OrdinalIgnoreCase))
            {
                if (GetBoolMember(comp, "IsUnderCover"))
                    return true;
            }
        }
        return false;
    }

    private bool TryRelocateTargetEntity(out EntityUid found)
        => TryRelocateReplacementEntity(EntityUid.Invalid, out found, ignorePreExisting: true);

    private bool TryRelocateReplacementEntity(EntityUid excludeEntity, out EntityUid found, bool ignorePreExisting = true)
    {
        found = EntityUid.Invalid;
        if (_targetMapCoords.MapId == MapId.Nullspace)
            return false;

        var candidates = _lookup.GetEntitiesInRange(_targetMapCoords, 0.45f, LookupFlags.Uncontained);
        float bestDist = float.MaxValue;

        foreach (var uid in candidates)
        {
            if (uid == excludeEntity || !Exists(uid) || Deleted(uid) || HasGhostComponent(uid))
                continue;

            if (ignorePreExisting && _preExistingTileEntities.Contains(uid))
                continue;

            if (!_solver.TryResolveGraphAndNode(uid, out var graph, out _, out _) || graph == null)
                continue;

            if (_activePlan != null &&
                !string.Equals(graph.ID, _activePlan.GraphId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pos = _transform.GetMapCoordinates(uid);
            float dist = (pos.Position - _targetMapCoords.Position).LengthSquared();
            if (dist < bestDist)
            {
                bestDist = dist;
                found = uid;
            }
        }

        return found.IsValid();
    }

    private bool TryRelocateNonGhostTarget(out EntityUid found)
        => TryRelocateReplacementEntity(_currentTarget, out found, ignorePreExisting: true);

    private bool IsGhostOrUnfinishedFrame(EntityUid uid)
    {
        if (HasGhostComponent(uid))
            return true;

        var meta = MetaData(uid);
        var protoId = meta.EntityPrototype?.ID ?? string.Empty;
        var name = meta.EntityName;

        foreach (var kw in UnfinishedFrameKeywords)
        {
            if (protoId.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (_solver.TryResolveGraphAndNode(uid, out _, out var node, out _) && node != null)
        {
            var nodeName = node.Name;
            foreach (var kw in UnfinishedFrameKeywords)
            {
                if (nodeName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool HasGhostComponent(EntityUid uid)
    {
        if (HasComp<ConstructionGhostComponent>(uid))
            return true;

        foreach (var comp in EntityManager.GetComponents(uid))
        {
            if (string.Equals(comp.GetType().Name, "ConstructionGhostComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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

    public bool IsToolReadyForQuality(EntityUid uid, string? quality)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(quality))
            return false;

        if (!_toolSystem.HasQuality(uid, quality))
            return false;

        if (FindComponentByName(uid, "MultipleToolComponent") is { } multiComp &&
            TryGetCurrentMultipleToolEntry(multiComp, out var curEntry) &&
            curEntry != null &&
            !EntryProvidesQuality(curEntry, quality))
        {
            return false;
        }

        if (string.Equals(quality, "Prying", StringComparison.OrdinalIgnoreCase) &&
            FindComponentByName(uid, "PryingComponent") is { } pryingComp &&
            GetMemberValue(pryingComp, "Enabled") is false)
        {
            return false;
        }

        return true;
    }

    public bool HasOrCanCycleToolQuality(EntityUid uid, string? quality)
    {
        if (!Exists(uid) || string.IsNullOrWhiteSpace(quality))
            return false;

        if (IsToolReadyForQuality(uid, quality))
            return true;

        if (FindComponentByName(uid, "MultipleToolComponent") is { } multiComp &&
            EntriesProvideQuality(GetMemberValue(multiComp, "Entries"), quality))
        {
            return true;
        }

        var proto = MetaData(uid).EntityPrototype;
        if (proto != null)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<EntityPrototype>();
            queue.Enqueue(proto);
            visited.Add(proto.ID);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (_itemUtil.TryGetPrototypeComponent<object>(cur, "MultipleTool", out var protoMulti) &&
                    protoMulti != null &&
                    EntriesProvideQuality(GetMemberValue(protoMulti, "Entries"), quality))
                {
                    return true;
                }

                if (cur.Parents != null)
                {
                    foreach (var parentId in cur.Parents)
                    {
                        if (visited.Add(parentId) && _protoMan.TryIndex<EntityPrototype>(parentId, out var parentProto))
                            queue.Enqueue(parentProto);
                    }
                }
            }
        }

        if (MatchesPrototypeOrParentSubstring(uid, "JawsOfLife"))
        {
            if (string.Equals(quality, "Prying", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quality, "Cutting", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (MatchesPrototypeOrParentSubstring(uid, "PowerDrill") ||
            MatchesPrototypeOrParentSubstring(uid, "HandDrill"))
        {
            if (string.Equals(quality, "Screwing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(quality, "Anchoring", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCurrentMultipleToolEntry(object multiComp, out object? entry)
    {
        entry = null;
        if (GetMemberValue(multiComp, "Entries") is not IEnumerable entriesEnum)
            return false;

        var list = new List<object>();
        foreach (var item in entriesEnum)
        {
            if (item != null)
                list.Add(item);
        }

        if (list.Count == 0)
            return false;

        var curObj = GetMemberValue(multiComp, "CurrentEntry");
        int idx = curObj switch
        {
            uint u => (int)u,
            int i => i,
            byte b => b,
            short s => s,
            _ => 0
        };

        if (idx < 0 || idx >= list.Count)
            return false;

        entry = list[idx];
        return true;
    }

    private static bool EntriesProvideQuality(object? entriesObj, string quality)
    {
        if (entriesObj is not IEnumerable entries)
            return false;

        foreach (var entry in entries)
        {
            if (entry != null && EntryProvidesQuality(entry, quality))
                return true;
        }

        return false;
    }

    private static bool EntryProvidesQuality(object entry, string quality)
    {
        var behaviorObj = GetMemberValue(entry, "Behavior")
                          ?? GetMemberValue(entry, "Qualities")
                          ?? GetMemberValue(entry, "Quality");
        if (behaviorObj == null)
            return false;

        if (behaviorObj is string s)
            return string.Equals(s, quality, StringComparison.OrdinalIgnoreCase);

        if (behaviorObj is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                var qStr = item as string ?? GetStringMember(item, "Id") ?? item.ToString();
                if (!string.IsNullOrEmpty(qStr) &&
                    string.Equals(qStr, quality, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool TryEquipToolOrMultiTool(string quality, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(quality))
            return false;

        _multiToolCycleRetries = 0;
        var label = description ?? $"Tool:{quality}";

        // 1. If the active hand already holds a tool or multi-tool that provides (or can cycle to) `quality`,
        //    keep it in hand so we can cycle it in-place without stowing/swapping.
        if (_player.LocalEntity is { } player &&
            Exists(player) &&
            _hands.GetActiveItem(player) is { } activeHeld &&
            Exists(activeHeld) &&
            HasOrCanCycleToolQuality(activeHeld, quality))
        {
            return true;
        }

        // 2. Prefer equipping a tool that is already in the required mode
        if (_itemUtil.TryEquipByPredicate(
                uid => IsToolReadyForQuality(uid, quality),
                label))
        {
            return true;
        }

        // 3. Equip any multi-tool (e.g. PowerDrill or JawsOfLife) that can cycle to `quality`
        if (_itemUtil.TryEquipByPredicate(
                uid => HasOrCanCycleToolQuality(uid, quality),
                $"Multi{label}"))
        {
            return true;
        }

        // 4. Deep container / interaction-range vicinity fallback
        if (_player.LocalEntity is { } p && Exists(p))
        {
            return TryEquipFromDeepInventoryOrVicinity(
                p,
                uid => HasOrCanCycleToolQuality(uid, quality),
                1);
        }

        return false;
    }

    public bool EnsureHeldToolModeReady(string quality, out bool failed)
    {
        failed = false;
        if (string.IsNullOrWhiteSpace(quality) ||
            _player.LocalEntity is not { } player ||
            !Exists(player) ||
            _hands.GetActiveItem(player) is not { } held ||
            !Exists(held))
        {
            failed = true;
            return false;
        }

        if (IsToolReadyForQuality(held, quality))
        {
            _multiToolCycleRetries = 0;
            return true;
        }

        if (!HasOrCanCycleToolQuality(held, quality))
        {
            failed = true;
            return false;
        }

        _multiToolCycleRetries++;
        if (_multiToolCycleRetries > 6)
        {
            _multiToolCycleRetries = 0;
            failed = true;
            return false;
        }

        RaisePredictiveEvent(new RequestUseInHandEvent());
        return false;
    }

    public bool EnsureHeldWireUiToolReady(out bool failed)
    {
        failed = false;
        if (_player.LocalEntity is not { } player ||
            !Exists(player) ||
            _hands.GetActiveItem(player) is not { } held ||
            !Exists(held))
        {
            failed = true;
            return false;
        }

        if (IsToolReadyForQuality(held, SharedToolSystem.CutQuality) ||
            IsToolReadyForQuality(held, SharedToolSystem.PulseQuality))
        {
            _multiToolCycleRetries = 0;
            return true;
        }

        if (HasOrCanCycleToolQuality(held, SharedToolSystem.CutQuality))
            return EnsureHeldToolModeReady(SharedToolSystem.CutQuality, out failed);

        if (HasOrCanCycleToolQuality(held, SharedToolSystem.PulseQuality))
            return EnsureHeldToolModeReady(SharedToolSystem.PulseQuality, out failed);

        failed = true;
        return false;
    }

    private bool RequiresWelderIgnition(EntityUid toolUid)
    {
        foreach (var comp in EntityManager.GetComponents(toolUid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsWelderLit(EntityUid toolUid)
    {
        foreach (var comp in EntityManager.GetComponents(toolUid))
        {
            var name = comp.GetType().Name;
            if (name.Equals("ItemToggleComponent", StringComparison.OrdinalIgnoreCase))
                return GetBoolMember(comp, "Activated");
            if (name.Equals("WelderComponent", StringComparison.OrdinalIgnoreCase) &&
                GetMemberValue(comp, "Enabled") is bool enabled)
            {
                return enabled;
            }
        }
        return false;
    }

    private void ExtinguishActiveWelderIfNeeded()
    {
        if (_litWelderUid is not { } welder || !Exists(welder))
        {
            _litWelderUid = null;
            return;
        }

        if (IsWelderLit(welder) && _player.LocalEntity is { } player && Exists(player))
        {
            if (_hands.GetActiveItem(player) == welder)
                RaisePredictiveEvent(new RequestUseInHandEvent());
        }

        _litWelderUid = null;
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

    private string GetEntityDisplayName(EntityUid uid)
    {
        if (!Exists(uid))
            return "target";

        if (HasGhostComponent(uid))
        {
            var ghostComp = FindComponentByName(uid, "ConstructionGhostComponent");
            var proto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
            var protoId = proto?.ID ?? GetStringMember(ghostComp, "Prototype");
            if (!string.IsNullOrEmpty(protoId))
                return $"ghost:{protoId}";
        }

        var meta = MetaData(uid);
        var name = meta.EntityName;
        var pId = meta.EntityPrototype?.ID;
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(pId) && !string.Equals(name, pId, StringComparison.OrdinalIgnoreCase))
            return $"{name} ({pId})";

        return !string.IsNullOrEmpty(pId) ? pId : (!string.IsNullOrEmpty(name) ? name : uid.ToString());
    }

    private static string FormatStepRequirement(ConstructionActionStep step)
    {
        return step.Kind switch
        {
            ConstructionActionKind.AddMaterial => $"material '{step.Amount}x {step.PrototypeOrQualityOrTag}'",
            ConstructionActionKind.UseTool => $"tool with '{step.PrototypeOrQualityOrTag}' quality",
            ConstructionActionKind.CutWires => "Wirecutters / Multitool",
            ConstructionActionKind.InsertPrototype => $"item '{step.Amount}x {step.PrototypeOrQualityOrTag}'",
            ConstructionActionKind.InsertComponent => $"component item '{step.PrototypeOrQualityOrTag}'",
            ConstructionActionKind.InsertTag => $"tagged item '[{step.PrototypeOrQualityOrTag}]'",
            ConstructionActionKind.StartGhostConstruction => $"starting material/tool for '{step.PrototypeOrQualityOrTag}'",
            _ => step.Description
        };
    }

    public string? GetActiveStepHudSummary()
    {
        if (_activePlan == null || _stepIndex < 0 || _stepIndex >= _activePlan.Steps.Count)
            return null;

        var step = _activePlan.Steps[_stepIndex];
        return $"Step {_stepIndex + 1}/{_activePlan.Steps.Count}: {step.Description}";
    }

    public float GetActiveStepProgress()
    {
        if (_activePlan == null || _activePlan.Steps.Count == 0)
            return 0f;

        return Math.Clamp((float)(_stepIndex + 1) / _activePlan.Steps.Count, 0.05f, 0.98f);
    }

    public bool TryGetHudDisplay(
        out string header,
        out string detail,
        out string? subDetail,
        out float progress,
        out Color accentColor,
        out MapCoordinates? highlightMapCoords)
    {
        header = string.Empty;
        detail = string.Empty;
        subDetail = null;
        progress = 0f;
        accentColor = Color.Cyan;
        highlightMapCoords = null;

        // 1. Standalone AutoHack active solve takes priority when solving
        if (_wiresSolver.IsSolving &&
            _wiresSolver.TryGetHudStatus(out var hackHeader, out var hackDetail, out var hackSub, out var hackProg, out var hackErr, out _, out var hackTarget))
        {
            header = hackHeader;
            detail = hackDetail;
            subDetail = hackSub;
            progress = hackProg;
            accentColor = hackErr ? Color.OrangeRed : Color.Gold;
            if (hackTarget.HasValue && Exists(hackTarget.Value))
                highlightMapCoords = _transform.GetMapCoordinates(hackTarget.Value);
            return true;
        }

        // 2. Active AutoConstruct / AutoDeconstruct / Blueprint Build
        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            bool isBlueprint = _blueprintQueue.Count > 0 || _activeBlueprintTask != null;
            bool isDeconstruct = _activePlan?.IsDeconstruct == true;

            if (Exists(_currentTarget))
                highlightMapCoords = _transform.GetMapCoordinates(_currentTarget);
            else if (_targetMapCoords.MapId != MapId.Nullspace)
                highlightMapCoords = _targetMapCoords;

            if (isBlueprint)
            {
                int total = Math.Max(_totalBlueprintTasksQueued, _completedBlueprintTasksCount + _blueprintQueue.Count);
                int currentItemNum = Math.Min(total, _completedBlueprintTasksCount + 1);
                var bpName = _activeBlueprintName ?? "Blueprint";
                var phase = CurrentActiveBlueprintPhase;
                var itemProto = _activeBlueprintTask?.Item.EntityPrototypeId ?? _desiredBlueprintEntityProtoId ?? _currentTargetLabel;

                header = $"[Blueprint '{bpName}' ({currentItemNum}/{Math.Max(1, total)})] Building {itemProto}";

                if (_activePlan != null && _stepIndex >= 0 && _stepIndex < _activePlan.Steps.Count)
                {
                    var step = _activePlan.Steps[_stepIndex];
                    detail = $"Step {_stepIndex + 1}/{_activePlan.Steps.Count}: {step.Description} [{_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
                    subDetail = _currentDiagnosticStatus ?? $"Phase {(int)(phase ?? 0)} ({phase}) — {_blueprintQueue.Count} remaining";
                    float itemFrac = (float)_stepIndex / Math.Max(1, _activePlan.Steps.Count);
                    progress = Math.Clamp((_completedBlueprintTasksCount + itemFrac) / Math.Max(1, total), 0.04f, 0.99f);
                }
                else
                {
                    detail = _currentDiagnosticStatus ?? $"Preparing next item in Phase {(int)(phase ?? 0)} ({phase})...";
                    subDetail = $"{_blueprintQueue.Count} item(s) remaining in queue";
                    progress = Math.Clamp((float)_completedBlueprintTasksCount / Math.Max(1, total), 0.04f, 0.99f);
                }

                accentColor = _stepRetryCount > 0 || !string.IsNullOrEmpty(_currentDiagnosticStatus)
                    ? Color.Gold
                    : Color.Cyan;
                return true;
            }

            // Single-target AutoBuild or AutoDeconstruct
            var plan = _activePlan!;
            var stepIdx = Math.Clamp(_stepIndex, 0, Math.Max(0, plan.Steps.Count - 1));
            var curStep = plan.Steps[stepIdx];
            var modeTag = isDeconstruct ? "AutoDeconstruct" : "AutoBuild";

            header = $"[{modeTag}] {_currentTargetLabel} ({plan.StartNode} -> {plan.TargetNode})";
            detail = $"Step {stepIdx + 1}/{plan.Steps.Count}: {curStep.Description} [{_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
            subDetail = _currentDiagnosticStatus;
            progress = Math.Clamp((float)(stepIdx + 1) / Math.Max(1, plan.Steps.Count), 0.08f, 0.98f);
            accentColor = _stepRetryCount > 0
                ? Color.Gold
                : (isDeconstruct ? Color.Orange : Color.Cyan);
            return true;
        }

        // 3. Recent failure or completion banner from AutoConstruct
        if (_hudBannerTimer > 0f && !string.IsNullOrEmpty(_hudBannerHeader))
        {
            header = _hudBannerHeader!;
            detail = _hudBannerDetail ?? string.Empty;
            progress = 1f;
            accentColor = _hudBannerIsError ? Color.OrangeRed : Color.LimeGreen;
            return true;
        }

        // 4. Recent failure or completion banner from AutoHack
        if (_wiresSolver.TryGetHudStatus(out var hBanner, out var dBanner, out var sBanner, out var pBanner, out var errBanner, out var compBanner, out _))
        {
            header = hBanner;
            detail = dBanner;
            subDetail = sBanner;
            progress = pBanner;
            accentColor = errBanner ? Color.OrangeRed : (compBanner ? Color.LimeGreen : Color.Gold);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Renders an AutoChem-style top-center HUD status box and world-space target highlight
/// for AutoBuild, Blueprint Build, AutoDeconstruct, and AutoHack.
/// </summary>
public sealed class RobustaConstructHackHudOverlay : Overlay
{
    private readonly IEntityManager _entMan;
    private readonly Font _font;
    private readonly Font _subFont;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace | OverlaySpace.WorldSpace;

    public RobustaConstructHackHudOverlay()
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        var cache = IoCManager.Resolve<IResourceCache>();
        var fontRes = cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf");
        _font = new VectorFont(fontRes, 12);
        _subFont = new VectorFont(fontRes, 11);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoConstruct))
            return;

        if (!autoConstruct.TryGetHudDisplay(
                out var header,
                out var detail,
                out var subDetail,
                out float progress,
                out var accentColor,
                out var highlightMapCoords))
        {
            return;
        }

        if ((args.Space & OverlaySpace.WorldSpace) != 0 &&
            highlightMapCoords.HasValue &&
            highlightMapCoords.Value.MapId == args.MapId)
        {
            DrawDistinctActiveTargetMarker(args.WorldHandle, highlightMapCoords.Value.Position);
        }

        if ((args.Space & OverlaySpace.ScreenSpace) != 0)
        {
            DrawScreenHud(args.ScreenHandle, args.Viewport.Size, header, detail, subDetail, progress, accentColor);
        }
    }

    private static void DrawDistinctActiveTargetMarker(DrawingHandleWorld worldHandle, Vector2 center)
    {
        // High-contrast Magenta/HotPink + Gold diamond & target ring so the active build item is
        // immediately distinguishable from the cyan/orange square blueprint tile previews.
        var primaryColor = Color.FromHex("#FF2A85");
        var secondaryColor = Color.Gold;

        // 1. Soft filled circle & outer ring
        worldHandle.DrawCircle(center, 0.38f, primaryColor.WithAlpha(0.18f), filled: true);
        worldHandle.DrawCircle(center, 0.38f, primaryColor.WithAlpha(0.95f), filled: false);
        worldHandle.DrawCircle(center, 0.12f, secondaryColor.WithAlpha(0.9f), filled: false);

        // 2. Rotated outer diamond (rhombus)
        float rOuter = 0.48f;
        var top = center + new Vector2(0f, rOuter);
        var right = center + new Vector2(rOuter, 0f);
        var bottom = center + new Vector2(0f, -rOuter);
        var left = center + new Vector2(-rOuter, 0f);

        worldHandle.DrawLine(top, right, primaryColor);
        worldHandle.DrawLine(right, bottom, primaryColor);
        worldHandle.DrawLine(bottom, left, primaryColor);
        worldHandle.DrawLine(left, top, primaryColor);

        // 3. Inner Gold diamond
        float rInner = 0.28f;
        var iTop = center + new Vector2(0f, rInner);
        var iRight = center + new Vector2(rInner, 0f);
        var iBottom = center + new Vector2(0f, -rInner);
        var iLeft = center + new Vector2(-rInner, 0f);

        worldHandle.DrawLine(iTop, iRight, secondaryColor);
        worldHandle.DrawLine(iRight, iBottom, secondaryColor);
        worldHandle.DrawLine(iBottom, iLeft, secondaryColor);
        worldHandle.DrawLine(iLeft, iTop, secondaryColor);

        // 4. Diagonal crosshair ticks pointing toward center
        float diagStart = 0.42f;
        float diagEnd = 0.20f;
        Vector2[] dirs =
        {
            new(0.7071f, 0.7071f),
            new(0.7071f, -0.7071f),
            new(-0.7071f, 0.7071f),
            new(-0.7071f, -0.7071f)
        };
        foreach (var d in dirs)
        {
            worldHandle.DrawLine(center + d * diagStart, center + d * diagEnd, secondaryColor);
        }
    }

    private void DrawScreenHud(
        DrawingHandleScreen screenHandle,
        Vector2i viewportSize,
        string header,
        string detail,
        string? subDetail,
        float progress,
        Color accentColor)
    {
        var headerDim = screenHandle.GetDimensions(_font, header, 1f);
        var detailDim = screenHandle.GetDimensions(_font, detail, 1f);
        var subDim = !string.IsNullOrEmpty(subDetail)
            ? screenHandle.GetDimensions(_subFont, subDetail, 1f)
            : Vector2.Zero;

        float padding = 14f;
        float verticalGap = 8f;
        float barHeight = 4f;

        float maxTextWidth = Math.Max(headerDim.X, Math.Max(detailDim.X, subDim.X));
        float boxWidth = Math.Clamp(maxTextWidth + padding * 2.2f, 300f, 640f);

        float boxHeight = padding + headerDim.Y + verticalGap + detailDim.Y + verticalGap + barHeight + padding;
        if (!string.IsNullOrEmpty(subDetail))
            boxHeight += subDim.Y + verticalGap;

        float topY = 76f;
        if (_entMan.EntitySysManager.TryGetEntitySystem<RobustoClient.Systems.AutoChem.RobustaAutoChemSystem>(out var autoChem) &&
            autoChem.CurrentState is not RobustoClient.Systems.AutoChem.IdleState)
        {
            topY = 150f;
        }

        var basePos = new Vector2(viewportSize.X / 2f - boxWidth / 2f, topY);
        var boxSize = new Vector2(boxWidth, boxHeight);

        // Background & border (matching RobustaAutoChemOverlay aesthetic)
        screenHandle.DrawRect(UIBox2.FromDimensions(basePos, boxSize), Color.Black.WithAlpha(0.85f));
        screenHandle.DrawRect(UIBox2.FromDimensions(basePos, boxSize), accentColor.WithAlpha(0.45f), filled: false);

        float currentY = basePos.Y + padding;

        // 1. Header line
        screenHandle.DrawString(_font, new Vector2(basePos.X + padding, currentY), header, accentColor);
        currentY += headerDim.Y + verticalGap;

        // 2. Step / primary detail line
        screenHandle.DrawString(_font, new Vector2(basePos.X + padding, currentY), detail, Color.White);
        currentY += detailDim.Y + verticalGap;

        // 3. Optional diagnostic / retry / queue sub-detail line
        if (!string.IsNullOrEmpty(subDetail))
        {
            screenHandle.DrawString(_subFont, new Vector2(basePos.X + padding, currentY), subDetail, Color.LightGray);
            currentY += subDim.Y + verticalGap;
        }

        // 4. Progress bar
        var barPos = new Vector2(basePos.X + padding, currentY);
        var barFullWidth = boxWidth - (padding * 2f);
        var clampedProgress = Math.Clamp(progress, 0f, 1f);

        Color barColor = accentColor == Color.OrangeRed
            ? Color.Crimson
            : (clampedProgress < 1f ? Color.LimeGreen : Color.Gold);

        screenHandle.DrawRect(UIBox2.FromDimensions(barPos, new Vector2(barFullWidth, barHeight)), Color.DarkSlateGray);
        screenHandle.DrawRect(UIBox2.FromDimensions(barPos, new Vector2(barFullWidth * clampedProgress, barHeight)), barColor);
    }
}

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