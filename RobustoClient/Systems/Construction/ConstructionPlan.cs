using System.Collections.Generic;

namespace RobustoClient.Systems.Construction;

public enum ConstructionActionKind
{
    StartGhostConstruction,
    AddMaterial,
    UseTool,
    CutWires,
    InsertPrototype,
    InsertComponent,
    InsertTag
}

public sealed record ConstructionActionStep(
    ConstructionActionKind Kind,
    string Description,
    string? PrototypeOrQualityOrTag = null,
    int Amount = 1,
    float DoAfterDelay = 0f,
    string? ExpectedNextNode = null,
    int? TargetPipeLayer = null
);

public sealed record SolvedConstructionPlan(
    string GraphId,
    string StartNode,
    string TargetNode,
    bool IsDeconstruct,
    List<ConstructionActionStep> Steps
);

public sealed record IndexedGraphEdge(
    string TargetNode,
    IReadOnlyList<ConstructionActionStep> Steps,
    bool? RequiredAnchored = null,
    bool? RequiredWirePanelOpen = null,
    bool? RequiredDoorWelded = null,
    bool? RequiredAllWiresCut = null
);

public sealed record IndexedGraphNode(
    string Name,
    string? EntityId,
    IReadOnlyList<IndexedGraphEdge> Edges,
    bool IsBoardNodeEntity = false,
    string? SecurityExamine = null,
    bool IsAnchorable = false
);

public sealed record IndexedConstructionGraph(
    string Id,
    string StartNode,
    IReadOnlyDictionary<string, IndexedGraphNode> Nodes
)
{
    public string ID => Id;
}

public sealed record IndexedConstructionRecipe(
    string Id,
    string GraphId,
    string TargetNode,
    string StartNode = "start",
    IReadOnlyDictionary<int, string>? AlternativePrototypes = null
);

public sealed record MachineBoardRequirements(
    string BoardPrototypeId,
    IReadOnlyDictionary<string, int>? StackRequirements = null,
    IReadOnlyDictionary<string, int>? PartRequirements = null,
    IReadOnlyDictionary<string, int>? ComponentRequirements = null,
    IReadOnlyDictionary<string, int>? TagRequirements = null
);

public sealed record ConstructionTargetSnapshot(
    string? PrototypeId = null,
    string? GraphId = null,
    string? CurrentNode = null,
    string? ExplicitTargetNode = null,
    string? DeconstructionNode = null,
    string? GhostRecipeId = null,
    bool IsAnchored = true,
    int PipeLayer = 0,
    bool? IsDoorWelded = null,
    bool HasWirePanel = false,
    bool IsWirePanelOpen = false,
    string WirePanelOpeningTool = "Screwing",
    bool HasWiresUi = false,
    bool IsSecurityWiresAccessible = true,
    string? SecurityExamine = null,
    bool HasMachineFrameBoard = false,
    MachineBoardRequirements? MachineBoard = null
);
