using System.Collections.Generic;
using Robust.Shared.Maths;

namespace RobustoClient.Systems.ESP;

public enum EspDock
{
    Top,
    Bottom,
    Left,
    Right
}

/// <summary>
/// Which user-facing ESP switch a category belongs to. Several categories can share one feature, so
/// toggling a feature in the menu enables or disables every category mapped to it.
/// </summary>
public enum EspFeature
{
    Players,
    Mobs,
    HeldItems,
    Syndicate,
    Contraband,
    Health,
    Icons
}

public sealed class EspCategoryDefinition
{
    public string Id { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public Color Color { get; set; } = Color.White;
    public string TextFormat { get; set; } = string.Empty;
    public EspDock Dock { get; set; } = EspDock.Top;
    public string Group { get; set; } = string.Empty;

    /// <summary>User-facing switch this category answers to. Defaults to players.</summary>
    public EspFeature Feature { get; set; } = EspFeature.Players;

    public EspConditions Conditions { get; set; } = new();
}

public sealed class EspConditions
{
    public List<string> HasComponent { get; set; } = new();
    public List<string> HasNotComponent { get; set; } = new();
    public List<string> PrototypeContains { get; set; } = new();
    public List<string> JobContains { get; set; } = new();
    public List<string> CustomRule { get; set; } = new();
}
