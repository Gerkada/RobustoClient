using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly ItemUtilitySystem _itemUtil = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    private bool _cacheInitialized;

    private readonly Dictionary<string, IndexedConstructionGraph> _indexedGraphs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<(IndexedConstructionGraph Graph, IndexedGraphNode Node)>> _entityToGraphNodes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<IndexedConstructionRecipe>> _graphToConstructionProtos =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IndexedConstructionRecipe> _recipesById =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _entityToConstructionProto =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string BaseProtoId, int PipeLayer), string> _entityAltLayerProtos = new();

    private readonly Dictionary<string, (string BaseProtoId, int PipeLayer)> _altEntityToBaseAndLayer =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string BaseRecipeId, int PipeLayer), string> _constructionAltLayerProtos = new();

    private static readonly string[] PrimaryDeconstructTargets =
    {
        "start",
        "destroyed",
        "deconstructed",
        "destroyedMachineFrame"
    };

    private static readonly string[] FallbackDeconstructTargets =
    {
        "frame",
        "machineFrame",
        "girder",
        "frameUnsecured",
        "assembly",
        "missingWires"
    };

    private static readonly HashSet<string> NonTerminalBuildNodeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start",
        "frame",
        "machineFrame",
        "missingWires",
        "computerFrame",
        "frameUnsecured",
        "boardUnsecured",
        "monitorMissing",
        "monitorUnsecured",
        "girder",
        "reinforcedGirder",
        "wallFrame",
        "windowFrame",
        "assembly",
        "wired",
        "electronics",
        "glassElectronics",
        "apcFrame"
    };

    private static readonly HashSet<string> KnownAnchorableFrameNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "girder",
        "reinforcedGirder",
        "assembly",
        "frameUnsecured",
        "missingWires",
        "frame",
        "machineFrame",
        "computerFrame",
        "apcFrame",
        "wallFrame",
        "windowFrame",
        "pipe",
        "binary",
        "trinary",
        "unary",
        "disposal",
        "disposal_pipe",
        "trunk"
    };

    private static readonly string[] PipeAndAtmosKeywords =
    {
        "pipe",
        "gaspipe",
        "gasbinary",
        "gastrinary",
        "gasunary",
        "disposal",
        "manifold",
        "valve",
        "pump",
        "mixer",
        "filter",
        "vent",
        "scrubber",
        "injector",
        "trunk",
        "router",
        "junction",
        "tagger",
        "radiator",
        "heatexchanger",
        "connectorport",
        "gasport"
    };

    private static readonly string[] BaseConsolePrototypeIds =
    {
        "BaseComputer",
        "BaseStructureComputer",
        "BaseComputerAiAccess",
        "BaseComputerTabletop",
        "BaseComputerShuttle",
        "ComputerTabletop"
    };

    private static readonly string[] BaseMachinePrototypeIds =
    {
        "BaseMachine",
        "ConstructibleMachine",
        "BaseMachinePowered",
        "BaseLathe",
        "BaseLatheLube",
        "BaseHyperlathe",
        "BaseResearchAndDevelopmentPointSource",
        "BasePartMachine",
        "BaseHeaterMachine",
        "BaseThruster",
        "BaseSubstation",
        "BaseSMES",
        "BaseGenerator",
        "BasePowerCharger",
        "BaseRecharger"
    };

    private static readonly string[] BaseAirlockPrototypeIds =
    {
        "BaseAirlock",
        "Airlock",
        "AirlockGlass",
        "AirlockShuttle",
        "AirlockGlassShuttle",
        "AirlockExternal",
        "AirlockExternalGlass",
        "AirlockExternalShuttle",
        "AirlockExternalGlassShuttle",
        "HighSecDoor"
    };

    private string? _lastKnownMachineBoardProtoId;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        ClearCache();
    }

    private void ClearCache()
    {
        _cacheInitialized = false;
        _indexedGraphs.Clear();
        _entityToGraphNodes.Clear();
        _graphToConstructionProtos.Clear();
        _recipesById.Clear();
        _entityToConstructionProto.Clear();
        _entityAltLayerProtos.Clear();
        _altEntityToBaseAndLayer.Clear();
        _constructionAltLayerProtos.Clear();
    }
}
