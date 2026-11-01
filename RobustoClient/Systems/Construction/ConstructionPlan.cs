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
