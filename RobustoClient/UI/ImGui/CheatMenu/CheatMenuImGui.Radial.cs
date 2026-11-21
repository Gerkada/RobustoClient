using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Client.Input;
using RobustoClient.ImGui;
using RobustoClient.UI.ImGui.Radial;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    /// <summary>Index of the wheel the Delete button asked to remove, applied after the card's loop.</summary>
    private int _pendingRadialRemoval = -1;

    /// <summary>
    /// User-created wheels: create, rename, rebind, fill the sectors, delete. A wheel opens on hold and
    /// fires the sector under the cursor on release.
    /// </summary>
    private void DrawRadialsCard()
    {
        RobustaImGuiWidgets.BeginSectionCard("##RadialsCard", "RADIAL");
        RobustaImGuiWidgets.MutedTextWrapped("Allows multi select radials.");
        Spacing();

        if (RobustaImGuiWidgets.AccentButton("Create", new Vector2(-1f, RobustaImGuiScale.ButtonRow)))
        {
            var radial = new RobustaConfig.RadialMenuSettings { Name = $"Radial {RobustaConfig.Radials.Count + 1}" };
            radial.EnsureDefaults();
            RobustaConfig.Radials.Add(radial);
            RobustaConfig.Save();
        }

        for (int i = 0; i < RobustaConfig.Radials.Count; i++)
        {
            Spacing();
            DrawRadialEntry(i, RobustaConfig.Radials[i]);
        }

        // Splicing inside the loop would leave the child stack unbalanced.
        if (_pendingRadialRemoval >= 0 && _pendingRadialRemoval < RobustaConfig.Radials.Count)
        {
            // A capture session owned by the removed wheel would have nowhere to write.
            if (IsCapturing(RobustaConfig.Radials[_pendingRadialRemoval]))
                StopListening();

            RobustaConfig.Radials.RemoveAt(_pendingRadialRemoval);
            _pendingRadialRemoval = -1;
            RobustaConfig.Save();
        }

        RobustaImGuiWidgets.EndSectionCard();
    }

    private void DrawRadialEntry(int index, RobustaConfig.RadialMenuSettings radial)
    {
        PushID(index);

        RobustaImGuiWidgets.CardTitle($"RADIAL {index + 1}");

        // Name row: the label is the only identity a wheel has, so it is editable in place.
        string name = radial.Name ?? "";
        SetNextItemWidth(MathF.Max(40f, GetContentRegionAvail().X - DeleteButtonWidth() - RobustaImGuiTheme.Metrics.ItemSpacingX));
        if (InputTextWithHint("##radial_name", "Radial name", ref name, 48))
        {
            radial.Name = name;
            MarkConfigDirty();
        }

        SameLine();
        if (RobustaImGuiWidgets.DangerButton("Delete", new Vector2(DeleteButtonWidth(), RobustaImGuiScale.FrameHeight)))
            _pendingRadialRemoval = index;

        DrawRadialKeyRow(radial);

        if (radial.ActiveCount < 2)
            RobustaImGuiWidgets.MutedTextWrapped("Add a second action, with fewer than two the wheel does nothing.");

        for (int i = 0; i < radial.Actions.Count; i++)
            DrawRadialActionRow(radial, i);

        PopID();
    }

    /// <summary>
    /// Key row: the current combination plus the same capture button a direct bind uses. The key is
    /// captured, never typed, so there is no edit buffer to keep in sync.
    /// </summary>
    private void DrawRadialKeyRow(RobustaConfig.RadialMenuSettings radial)
    {
        bool isListening = IsCapturing(radial);

        AlignTextToFramePadding();
        TextUnformatted(radial.Key.IsValid ? radial.Key.ToString() : "Unassigned");

        SameLine();

        float height = RobustaImGuiScale.FrameHeight;
        float width = CaptureButtonWidth();

        PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize * CompactLabelScale);
        bool pressed = RobustaImGuiWidgets.AccentButton(
            isListening ? "[...]##radial_bind" : "Bind##radial_bind",
            new Vector2(width, height),
            isListening);
        PopFont();

        if (pressed)
        {
            if (isListening)
                StopListening();
            else
                BeginCapture(radial, bind => AssignRadialKey(radial, bind));
        }

        if (!radial.Key.IsValid)
            return;

        SameLine();

        PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize * CompactLabelScale);
        bool clear = RobustaImGuiWidgets.AccentButton("Clear##radial_clear", new Vector2(width, height));
        PopFont();

        if (clear)
            AssignRadialKey(radial, new RobustaKeybind(Keyboard.Key.Unknown));
    }

    private void DrawRadialActionRow(RobustaConfig.RadialMenuSettings radial, int index)
    {
        string action = radial.Actions[index] ?? "";

        TextUnformatted($"{index + 1}.");
        SameLine();

        float comboWidth = RobustaImGuiScale.FrameHeightWithSpacing * 4f;
        SetNextItemWidth(MathF.Max(40f, GetContentRegionAvail().X - comboWidth - RobustaImGuiTheme.Metrics.ItemSpacingX));

        if (InputTextWithHint($"##radial_action_{index}", "(empty)", ref action, 96))
        {
            radial.Actions[index] = action;
            MarkConfigDirty();
        }

        SameLine();
        SetNextItemWidth(comboWidth);

        string preview = string.IsNullOrWhiteSpace(action) ? "Pick" : RadialMenuActions.PickerLabel(action);
        if (!BeginCombo($"##radial_pick_{index}", preview))
            return;

        foreach (var (command, label) in RadialMenuActions.Available)
        {
            if (!Selectable($"{label}##{command}"))
                continue;

            radial.Actions[index] = command;
            MarkConfigDirty();
            CloseCurrentPopup();
        }

        EndCombo();
    }

    /// <summary>
    /// Gives a wheel its key. One combination has one owner, so a direct bind or another wheel
    /// holding the same key is unbound first.
    /// </summary>
    private void AssignRadialKey(RobustaConfig.RadialMenuSettings radial, RobustaKeybind bind)
    {
        radial.Key = bind;
        ClearOtherKeyOwners(bind, null, radial);

        // A key assignment is one deliberate action, so it persists right away like a direct bind.
        RobustaConfig.Save();
    }

    private static float DeleteButtonWidth() => RobustaImGuiScale.FrameHeightWithSpacing * 4f;

    private static float CaptureButtonWidth() => RobustaImGuiScale.FrameHeightWithSpacing * 1.7f;
}
