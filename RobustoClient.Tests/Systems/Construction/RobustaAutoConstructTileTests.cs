using System.Collections;
using System.Collections.Generic;
using FluentAssertions;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Utility;
using RobustoClient.Systems.Construction;
using RobustoClient.Systems.Construction.Blueprints;
using Xunit;

namespace RobustoClient.Tests.Systems.Construction;

public class RobustaAutoConstructTileTests
{
    private sealed class FakeTileDefinition : ITileDefinition
    {
        public string ID { get; set; } = "FloorSteel";
        public ushort TileId { get; set; }
        public string Name { get; set; } = "steel floor";
        public ResPath? Sprite { get; set; }
        public Dictionary<Direction, ResPath> EdgeSprites { get; } = new();
        public int EdgeSpritePriority { get; set; }
        public float Friction { get; set; }
        public byte Variants { get; set; }
        public void AssignTileId(ushort id) => TileId = id;

        public bool IsSubFloor { get; set; }
        public object? BaseTurf { get; set; }
        public IEnumerable? DeconstructTools { get; set; }
    }

    [Fact]
    public void IsCrowbarableFloorCovering_TrueForPryableFloorTile()
    {
        var tile = new FakeTileDefinition
        {
            IsSubFloor = false,
            BaseTurf = "Plating",
            DeconstructTools = new List<string> { "Prying" }
        };

        RobustaAutoConstructSystem.IsCrowbarableFloorCovering(tile).Should().BeTrue();
    }

    [Fact]
    public void IsCrowbarableFloorCovering_FalseForSubfloorPlating()
    {
        var tile = new FakeTileDefinition
        {
            IsSubFloor = true,
            BaseTurf = "Lattice",
            DeconstructTools = new List<string> { "Prying" }
        };

        RobustaAutoConstructSystem.IsCrowbarableFloorCovering(tile).Should().BeFalse();
    }

    [Fact]
    public void IsCrowbarableFloorCovering_FalseWhenNoBaseTurf()
    {
        var tile = new FakeTileDefinition
        {
            IsSubFloor = false,
            BaseTurf = null,
            DeconstructTools = new List<string> { "Prying" }
        };

        RobustaAutoConstructSystem.IsCrowbarableFloorCovering(tile).Should().BeFalse();
    }

    [Fact]
    public void IsCrowbarableFloorCovering_FalseWhenOnlyCuttingIsAllowed()
    {
        var tile = new FakeTileDefinition
        {
            IsSubFloor = false,
            BaseTurf = "Plating",
            DeconstructTools = new List<string> { "Cutting" }
        };

        RobustaAutoConstructSystem.IsCrowbarableFloorCovering(tile).Should().BeFalse();
    }

    [Theory]
    [InlineData("GasPipe", true)]
    [InlineData("GasPipeAlt1", true)]
    [InlineData("GasPipeAlt2", true)]
    [InlineData("DisposalPipe", true)]
    [InlineData("GasBinary", true)]
    [InlineData("GasTrinary", true)]
    [InlineData("GasUnary", true)]
    [InlineData("Girder", false)]
    [InlineData("Machine", false)]
    [InlineData("Airlock", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUnderfloorPipeGraph_MatchesOnlySubfloorPipeGraphs(string? graphId, bool expected)
    {
        RobustaAutoConstructSystem.IsUnderfloorPipeGraph(graphId).Should().Be(expected);
    }

    [Theory]
    [InlineData("GasPipeStraight", true)]
    [InlineData("GasPipeBendAlt1", true)]
    [InlineData("DisposalPipeStraight", true)]
    [InlineData("GasFilter", false)]
    [InlineData("WallSolid", false)]
    [InlineData("CableApcStack", false)]
    public void IsPipeBlueprintItem_MatchesOnlyPipes(string protoId, bool expected)
    {
        var item = new BlueprintItemEntry { EntityPrototypeId = protoId };
        RobustaAutoConstructSystem.IsPipeBlueprintItem(item).Should().Be(expected);
    }
}