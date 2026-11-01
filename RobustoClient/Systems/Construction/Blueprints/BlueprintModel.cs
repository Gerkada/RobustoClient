namespace RobustoClient.Systems.Construction.Blueprints;

public enum BlueprintZPhase
{
    Phase0_SubfloorAndLattice = 0,   // Lattice, Catwalk, Hull plating
    Phase1_UnderfloorUtilities = 1,  // HV/MV/LV Cables, Gas Pipes, Manifolds, Disposal trunks/pipes
    Phase2_FlooringAndFrames = 2,    // Floor tiles (sealing underfloor), Girders, Machine/Computer/APC frames
    Phase3_WallsWindowsDoors = 3,    // Solid/Reinforced Walls, Grilles, Windows, Airlocks, Windoors, Firelocks
    Phase4_MachinesAndFixtures = 4   // Completed Machines, Computers, Lights, Tables, Chairs, Vents/Scrubbers
}

public sealed class BlueprintItemEntry
{
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public string Direction { get; set; } = "South";
    public string EntityPrototypeId { get; set; } = string.Empty;
    public string? ConstructionPrototypeId { get; set; }
    public BlueprintZPhase Phase { get; set; } = BlueprintZPhase.Phase2_FlooringAndFrames;
    public int LayerOrder { get; set; } = 0;
}

public sealed class RobustaBlueprint
{
    public string Name { get; set; } = "Unnamed Blueprint";
    public string Description { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public List<BlueprintItemEntry> Items { get; set; } = new();
}
