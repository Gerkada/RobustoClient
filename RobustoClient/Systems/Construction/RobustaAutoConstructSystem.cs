using Content.Client.Construction;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.Construction.Blueprints;
using RobustoClient.Systems.Hacking;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem : EntitySystem
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
}
