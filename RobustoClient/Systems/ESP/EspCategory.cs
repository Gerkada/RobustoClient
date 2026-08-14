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

public sealed class EspCategoryDefinition
{
    public string Id { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public Color Color { get; set; } = Color.White;
    public string TextFormat { get; set; } = string.Empty;
    public EspDock Dock { get; set; } = EspDock.Top;
    
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
