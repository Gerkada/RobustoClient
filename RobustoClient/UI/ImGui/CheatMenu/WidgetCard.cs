using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

/// <summary>
/// A titled block of controls. Its height follows its content at draw time.
/// </summary>
/// <param name="collapsible">
/// When true the card gets a collapse control and can hide its body. Defaults to true: every card
/// can be folded away.
/// </param>
internal sealed class WidgetCard(string id, string title, Action body, bool collapsible = true)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public Action Body { get; } = body;
    public bool Collapsible { get; } = collapsible;
}

public sealed partial class CheatMenuImGui
{
    private void Toggle(string label, ref bool value)
    {
        if (RobustaImGuiWidgets.ToggleSwitch(label, ref value))
            MarkConfigDirty();
    }

    private void Slider(string label, string id, ref float value, float min, float max, string format)
    {
        if (RobustaImGuiWidgets.SliderRow(label, id, ref value, min, max, format))
            MarkConfigDirty();
    }
}
