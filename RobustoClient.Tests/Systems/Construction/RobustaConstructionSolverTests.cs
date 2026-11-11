using System.Collections.Generic;
using FluentAssertions;
using RobustoClient.Systems.Construction;
using Xunit;

namespace RobustoClient.Tests.Systems.Construction;

public class RobustaConstructionSolverTests
{
    private static IndexedConstructionGraph CreateWallGraph()
    {
        var nodes = new Dictionary<string, IndexedGraphNode>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = new(
                "start",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "girder",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 2x Steel",
                                "Steel",
                                Amount: 2,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "girder")
                        ])
                ]),
            ["girder"] = new(
                "girder",
                EntityId: "Girder",
                Edges:
                [
                    new IndexedGraphEdge(
                        "wall",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 2x Steel",
                                "Steel",
                                Amount: 2,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "wall")
                        ],
                        RequiredAnchored: true),
                    new IndexedGraphEdge(
                        "start",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Screwing tool",
                                "Screwing",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "start")
                        ],
                        RequiredAnchored: false)
                ],
                IsAnchorable: true),
            ["wall"] = new(
                "wall",
                EntityId: "WallSolid",
                Edges:
                [
                    new IndexedGraphEdge(
                        "girder",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Welding tool",
                                "Welding",
                                Amount: 1,
                                DoAfterDelay: 2f,
                                ExpectedNextNode: "girder")
                        ])
                ],
                IsAnchorable: true)
        };

        return new IndexedConstructionGraph("Wall", "start", nodes);
    }

    private static IndexedConstructionGraph CreateAirlockGraphWithSecurity()
    {
        var nodes = new Dictionary<string, IndexedGraphNode>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = new(
                "start",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "assembly",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 4x Steel",
                                "Steel",
                                Amount: 4,
                                DoAfterDelay: 2f,
                                ExpectedNextNode: "assembly")
                        ])
                ]),
            ["assembly"] = new(
                "assembly",
                EntityId: "AirlockAssembly",
                Edges:
                [
                    new IndexedGraphEdge(
                        "wired",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 5x CableApc",
                                "CableApc",
                                Amount: 5,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "wired")
                        ],
                        RequiredAnchored: true),
                    new IndexedGraphEdge(
                        "start",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Welding tool",
                                "Welding",
                                Amount: 1,
                                DoAfterDelay: 2f,
                                ExpectedNextNode: "start")
                        ],
                        RequiredAnchored: false)
                ],
                IsAnchorable: true),
            ["wired"] = new(
                "wired",
                EntityId: "AirlockAssembly",
                Edges:
                [
                    new IndexedGraphEdge(
                        "electronics",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.InsertTag,
                                "Insert 1x tag:DoorElectronics",
                                "DoorElectronics",
                                Amount: 1,
                                DoAfterDelay: 0.5f,
                                ExpectedNextNode: "electronics")
                        ]),
                    new IndexedGraphEdge(
                        "assembly",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Cutting tool",
                                "Cutting",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "assembly")
                        ])
                ]),
            ["electronics"] = new(
                "electronics",
                EntityId: "AirlockAssembly",
                Edges:
                [
                    new IndexedGraphEdge(
                        "airlock",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Screwing tool",
                                "Screwing",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "airlock")
                        ]),
                    new IndexedGraphEdge(
                        "wired",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Prying tool",
                                "Prying",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "wired")
                        ])
                ]),
            ["airlock"] = new(
                "airlock",
                EntityId: "Airlock",
                Edges:
                [
                    new IndexedGraphEdge(
                        "electronics",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Prying tool",
                                "Prying",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "electronics")
                        ],
                        RequiredDoorWelded: true,
                        RequiredWirePanelOpen: true,
                        RequiredAllWiresCut: true)
                ],
                IsBoardNodeEntity: true),
            ["medSecurityUnfinished"] = new(
                "medSecurityUnfinished",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "airlock",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Prying tool",
                                "Prying",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "airlock")
                        ],
                        RequiredWirePanelOpen: true)
                ],
                SecurityExamine: "wires-panel-component-on-examine-security-level1"),
            ["medSecurity"] = new(
                "medSecurity",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "medSecurityUnfinished",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Welding tool",
                                "Welding",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "medSecurityUnfinished")
                        ],
                        RequiredWirePanelOpen: true)
                ],
                SecurityExamine: "wires-panel-component-on-examine-security-level2")
        };

        return new IndexedConstructionGraph("Airlock", "start", nodes);
    }

    private static IndexedConstructionGraph CreateGasBinaryGraph()
    {
        var nodes = new Dictionary<string, IndexedGraphNode>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = new(
                "start",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "pressurepump",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 2x Steel",
                                "Steel",
                                Amount: 2,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "pressurepump")
                        ])
                ]),
            ["pressurepump"] = new(
                "pressurepump",
                EntityId: "GasPressurePump",
                Edges:
                [
                    new IndexedGraphEdge(
                        "start",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Welding tool",
                                "Welding",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "start")
                        ],
                        RequiredAnchored: false)
                ],
                IsAnchorable: true)
        };

        return new IndexedConstructionGraph("GasBinary", "start", nodes);
    }

    private static IndexedConstructionGraph CreateMachineGraph()
    {
        var nodes = new Dictionary<string, IndexedGraphNode>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = new(
                "start",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "missingWires",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 5x Steel",
                                "Steel",
                                Amount: 5,
                                DoAfterDelay: 2f,
                                ExpectedNextNode: "missingWires")
                        ])
                ]),
            ["missingWires"] = new(
                "missingWires",
                EntityId: "MachineFrame",
                Edges:
                [
                    new IndexedGraphEdge(
                        "machineFrame",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.AddMaterial,
                                "Add 5x CableApc",
                                "CableApc",
                                Amount: 5,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "machineFrame")
                        ],
                        RequiredAnchored: true),
                    new IndexedGraphEdge(
                        "start",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Screwing tool",
                                "Screwing",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "start")
                        ])
                ],
                IsAnchorable: true),
            ["machineFrame"] = new(
                "machineFrame",
                EntityId: "MachineFrame",
                Edges:
                [
                    new IndexedGraphEdge(
                        "machine",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Screwing tool",
                                "Screwing",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "machine")
                        ]),
                    new IndexedGraphEdge(
                        "missingWires",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Cutting tool",
                                "Cutting",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "missingWires")
                        ])
                ],
                IsAnchorable: true),
            ["machine"] = new(
                "machine",
                EntityId: null,
                Edges:
                [
                    new IndexedGraphEdge(
                        "machineFrame",
                        [
                            new ConstructionActionStep(
                                ConstructionActionKind.UseTool,
                                "Use Prying tool",
                                "Prying",
                                Amount: 1,
                                DoAfterDelay: 1f,
                                ExpectedNextNode: "machineFrame")
                        ])
                ],
                IsBoardNodeEntity: true)
        };

        return new IndexedConstructionGraph("Machine", "start", nodes);
    }

    [Fact]
    public void SolveBuildPlan_ForwardBuildFromFrameToTerminalStructure_SynthesizesExpectedSteps()
    {
        var solver = new RobustaConstructionSolver();
        solver.LoadIndexedGraphs(
            [CreateWallGraph()],
            [new IndexedConstructionRecipe("WallSolid", "Wall", "wall", "start")]);

        var girderSnapshot = new ConstructionTargetSnapshot(
            PrototypeId: "Girder",
            GraphId: "Wall",
            CurrentNode: "girder",
            IsAnchored: false);

        var plan = solver.SolveBuildPlan(in girderSnapshot);
        plan.Should().NotBeNull();
        plan!.GraphId.Should().Be("Wall");
        plan.StartNode.Should().Be("girder");
        plan.TargetNode.Should().Be("wall");
        plan.IsDeconstruct.Should().BeFalse();

        plan.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Anchor frame (Anchoring)", "Anchoring", 1, 1f, null),
            new ConstructionActionStep(ConstructionActionKind.AddMaterial, "Add 2x Steel", "Steel", 2, 1f, "wall"));
    }

    [Fact]
    public void SolveDeconstructPlan_ReversePathfindingToStart_SynthesizesExpectedSteps()
    {
        var solver = new RobustaConstructionSolver();
        solver.LoadIndexedGraphs(
            [CreateWallGraph()],
            [new IndexedConstructionRecipe("WallSolid", "Wall", "wall", "start")]);

        var wallSnapshot = new ConstructionTargetSnapshot(
            PrototypeId: "WallSolid",
            GraphId: "Wall",
            CurrentNode: "wall",
            IsAnchored: true);

        var plan = solver.SolveDeconstructPlan(in wallSnapshot);
        plan.Should().NotBeNull();
        plan!.GraphId.Should().Be("Wall");
        plan.StartNode.Should().Be("wall");
        plan.TargetNode.Should().Be("start");
        plan.IsDeconstruct.Should().BeTrue();

        plan.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Welding tool", "Welding", 1, 2f, "girder"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Unanchor structure (Anchoring)", "Anchoring", 1, 1f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Screwing tool", "Screwing", 1, 1f, "start"));
    }

    [Fact]
    public void SolveDeconstructPlan_OnAirlockWithActiveSecurity_RemovesOuterSecurityBeforeDoorDeconstruction()
    {
        var solver = new RobustaConstructionSolver();
        solver.LoadIndexedGraphs(
            [CreateAirlockGraphWithSecurity()],
            [new IndexedConstructionRecipe("Airlock", "Airlock", "airlock", "start")]);

        var shieldedAirlock = new ConstructionTargetSnapshot(
            PrototypeId: "Airlock",
            GraphId: "Airlock",
            CurrentNode: "airlock",
            IsAnchored: true,
            IsDoorWelded: false,
            HasWirePanel: true,
            IsWirePanelOpen: false,
            HasWiresUi: true,
            IsSecurityWiresAccessible: false,
            SecurityExamine: "wires-panel-component-on-examine-security-level2");

        var plan = solver.SolveDeconstructPlan(in shieldedAirlock);
        plan.Should().NotBeNull();
        plan!.GraphId.Should().Be("Airlock");
        plan.StartNode.Should().Be("medSecurity");
        plan.TargetNode.Should().Be("start");
        plan.IsDeconstruct.Should().BeTrue();

        plan.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Weld door shut (Welding)", "Welding", 1, 1f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Open wire panel (Screwing)", "Screwing", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Welding tool", "Welding", 1, 1f, "medSecurityUnfinished"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Prying tool", "Prying", 1, 1f, "airlock"),
            new ConstructionActionStep(ConstructionActionKind.CutWires, "Cut internal wires (Cutting)", "Cutting", 1, 0.5f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Prying tool", "Prying", 1, 1f, "electronics"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Prying tool", "Prying", 1, 1f, "wired"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Cutting tool", "Cutting", 1, 1f, "assembly"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Unanchor structure (Anchoring)", "Anchoring", 1, 1f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Welding tool", "Welding", 1, 2f, "start"));
    }

    [Fact]
    public void SolveBuildPlan_PipeLayerTransitionOnContraption_SequencesScrewdriverAdjustmentsBeforeAnchoring()
    {
        var solver = new RobustaConstructionSolver();
        var recipe = new IndexedConstructionRecipe("GasPressurePump", "GasBinary", "pressurepump", "start");
        solver.LoadIndexedGraphs([CreateGasBinaryGraph()], [recipe]);

        var buildTertiaryPlan = solver.SolveBuildPlan(
            recipe,
            includeGhostStart: false,
            desiredEntityProtoId: "GasPressurePumpAlt2");

        buildTertiaryPlan.Should().NotBeNull();
        buildTertiaryPlan!.TargetNode.Should().Be("pressurepumpAlt2");
        buildTertiaryPlan.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.AddMaterial, "Add 2x Steel", "Steel", 2, 1f, "pressurepump"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Set pipe layer to Alt1/Secondary (Screwing)", "Screwing", 1, 0.25f, null, TargetPipeLayer: 1),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Set pipe layer to Alt2/Tertiary (Screwing)", "Screwing", 1, 0.25f, null, TargetPipeLayer: 2),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Anchor frame (Anchoring)", "Anchoring", 1, 1f, null));

        var unanchoredPumpOnPrimary = new ConstructionTargetSnapshot(
            PrototypeId: "GasPressurePump",
            GraphId: "GasBinary",
            CurrentNode: "pressurepump",
            IsAnchored: false,
            PipeLayer: 0);

        var secondaryLayerPlan = solver.SolveBuildPlan(in unanchoredPumpOnPrimary, desiredEntityProtoId: "GasPressurePumpAlt1");
        secondaryLayerPlan.Should().NotBeNull();
        secondaryLayerPlan!.TargetNode.Should().Be("pressurepumpAlt1");
        secondaryLayerPlan.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Set pipe layer to Alt1/Secondary (Screwing)", "Screwing", 1, 0.25f, null, TargetPipeLayer: 1),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Anchor frame (Anchoring)", "Anchoring", 1, 1f, null));
    }

    [Fact]
    public void SolveBuildAndDeconstructPlan_MachineFrameWithBoardRequirements_ResolvesBoardPartsAndPryOut()
    {
        var solver = new RobustaConstructionSolver();
        solver.LoadIndexedGraphs(
            [CreateMachineGraph()],
            [new IndexedConstructionRecipe("MachineFrame", "Machine", "machineFrame", "start")]);

        var boardReqs = new MachineBoardRequirements(
            "AutolatheMachineCircuitboard",
            StackRequirements: new Dictionary<string, int> { ["Glass"] = 5 },
            PartRequirements: new Dictionary<string, int> { ["Manipulator"] = 2, ["MatterBin"] = 1 },
            ComponentRequirements: new Dictionary<string, int> { ["PowerCell"] = 1 },
            TagRequirements: new Dictionary<string, int> { ["Igniter"] = 1 });

        var missingWiresFrame = new ConstructionTargetSnapshot(
            PrototypeId: "MachineFrame",
            GraphId: "Machine",
            CurrentNode: "missingWires",
            IsAnchored: false,
            HasMachineFrameBoard: false,
            MachineBoard: boardReqs);

        var buildPlan = solver.SolveBuildPlan(in missingWiresFrame, desiredEntityProtoId: "Autolathe");
        buildPlan.Should().NotBeNull();
        buildPlan!.StartNode.Should().Be("missingWires");
        buildPlan.TargetNode.Should().Be("machine");

        buildPlan.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Anchor frame (Anchoring)", "Anchoring", 1, 1f, null),
            new ConstructionActionStep(ConstructionActionKind.AddMaterial, "Add 5x CableApc", "CableApc", 5, 1f, "machineFrame"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Anchor frame (Anchoring)", "Anchoring", 1, 1f, null),
            new ConstructionActionStep(ConstructionActionKind.InsertPrototype, "Insert AutolatheMachineCircuitboard", "AutolatheMachineCircuitboard", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.AddMaterial, "Add 5x Glass", "Glass", 5, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.InsertComponent, "Insert Manipulator (1/2)", "MachinePart:Manipulator", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.InsertComponent, "Insert Manipulator (2/2)", "MachinePart:Manipulator", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.InsertComponent, "Insert MatterBin (1/1)", "MachinePart:MatterBin", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.InsertComponent, "Insert PowerCell (1/1)", "PowerCell", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.InsertTag, "Insert [Igniter] (1/1)", "Igniter", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Screwing tool", "Screwing", 1, 1f, "machine"));

        var completedMachine = new ConstructionTargetSnapshot(
            PrototypeId: "Autolathe",
            GraphId: "Machine",
            CurrentNode: "machine",
            IsAnchored: true,
            HasMachineFrameBoard: true);

        var deconstructPlan = solver.SolveDeconstructPlan(in completedMachine);
        deconstructPlan.Should().NotBeNull();
        deconstructPlan!.Steps.Should().Equal(
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Prying tool", "Prying", 1, 1f, "machineFrame"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Pry out board and parts (Prying)", "Prying", 1, 0f, null),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Cutting tool", "Cutting", 1, 1f, "missingWires"),
            new ConstructionActionStep(ConstructionActionKind.UseTool, "Use Screwing tool", "Screwing", 1, 1f, "start"));
    }
}
