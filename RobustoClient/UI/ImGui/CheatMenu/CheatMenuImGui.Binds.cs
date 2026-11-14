using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Client.Input;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    /// <summary>
    /// Scale for the labels of the Binds table's narrow columns. At the default text size "Reset" is
    /// 3.0 x font-size wide (RobotoMono advances 0.6em) and would not fit the column, so the label is
    /// drawn smaller and the button trims its own padding.
    /// </summary>
    private const float CompactLabelScale = 0.85f;

    private void DiscoverKeybinds()
    {
        _discoveredBinds.Clear();

        foreach (var field in typeof(RobustaConfig).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(RobustaKeybind))
                continue;

            var attr = field.GetCustomAttribute<KeybindConfigAttribute>();
            if (attr == null)
                continue;

            var bind = new DiscoveredBind(field, attr);
            var current = (RobustaKeybind)field.GetValue(null)!;
            bind.EditBuffer = Format(current);
            bind.Synced = bind.EditBuffer;
            _discoveredBinds.Add(bind);
        }
    }

    private void DrawBindsTab()
    {
        // Reopening the menu must not resurrect a half-edited buffer from before it closed.
        if (ImGuiInputBridge.JustBecameVisible)
            SyncBindBuffers();

        RobustaImGuiWidgets.BeginSectionCard("##BindsCard", "KEYBINDINGS");
        RobustaImGuiWidgets.MutedText(
            "Click 'Bind' to capture keys (Backspace/Delete clears, Escape cancels), or edit the keybind text directly.");
        Spacing();

        if (BeginTable(
                "##BindsTable",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            // Action gets the bulk of the width so long names fit; the key, capture and reset columns
            // are pushed right and kept narrow, since their content is short.
            TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 2.6f);
            TableSetupColumn("Key Combination", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            TableSetupColumn("Capture", ImGuiTableColumnFlags.WidthFixed, RobustaImGuiScale.FrameHeightWithSpacing * 1.7f);
            TableSetupColumn("Reset", ImGuiTableColumnFlags.WidthFixed, RobustaImGuiScale.FrameHeightWithSpacing * 1.4f);
            TableHeadersRow();

            foreach (var item in _discoveredBinds)
                DrawBindRow(item);

            EndTable();
        }

        Spacing();
        if (RobustaImGuiWidgets.DangerButton(
                "Reset All Binds to Default",
                new Vector2(-1f, RobustaImGuiScale.ButtonRow)))
        {
            ResetAllBinds();
        }

        RobustaImGuiWidgets.EndSectionCard();
    }

    private void DrawBindRow(DiscoveredBind item)
    {
        var current = (RobustaKeybind)item.Field.GetValue(null)!;
        bool isListening = _activeListeningTarget == item;

        TableNextRow();

        TableNextColumn();
        AlignTextToFramePadding();
        TextUnformatted(item.Attribute.DisplayName);

        TableNextColumn();
        DrawBindTextInput(item, current);

        TableNextColumn();
        DrawBindCaptureButton(item, isListening);

        TableNextColumn();

        // The compact columns are narrow, so their labels are drawn smaller and given an explicit
        // positive width. A positive width lets AccentButton trim its padding to fit the label;
        // without it the label overflows into the neighbouring cell. The height is captured before
        // the font is pushed so the button still lines up with the rest of the row.
        float resetHeight = RobustaImGuiScale.FrameHeight;
        float resetWidth = GetContentRegionAvail().X;

        PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize * CompactLabelScale);
        bool reset = RobustaImGuiWidgets.AccentButton(
            $"Reset##reset_btn_{item.Field.Name}",
            new Vector2(resetWidth, resetHeight));
        PopFont();

        if (reset)
            ApplyBind(item, DefaultBind(item));
    }

    private void DrawBindTextInput(DiscoveredBind item, RobustaKeybind current)
    {
        // Short prefix: the column is narrow, and a long placeholder clips. The hint still shows what
        // the bind falls back to, so an empty box is never ambiguous.
        var hint = item.Attribute.DefaultKey != Keyboard.Key.Unknown
            ? $"Def: {Format(DefaultBind(item))}"
            : "Unassigned";

        SetNextItemWidth(-1f);
        InputTextWithHint($"##bind_in_{item.Field.Name}", hint, ref item.EditBuffer, 64);

        if (IsItemDeactivatedAfterEdit())
        {
            if (RobustaKeybind.TryParse(item.EditBuffer, out var parsed))
                ApplyBind(item, parsed);
            else
                item.EditBuffer = Format(current);

            return;
        }

        // Mirror the live bind, but only when it actually changed. Rewriting the box on every idle
        // frame overwrote text the user was still typing, because ImGui reports the item as inactive
        // on the frame the click lands.
        var live = Format(current);
        if (IsItemActive() || item.Synced == live)
            return;

        item.EditBuffer = live;
        item.Synced = live;
    }

    private void DrawBindCaptureButton(DiscoveredBind item, bool isListening)
    {
        // "[...]" rather than "[Press Key...]": the capture column is narrow, and the longer label was
        // clipped to a fragment that read as a different word.
        string label = isListening
            ? $"[...]##bind_btn_{item.Field.Name}"
            : $"Bind##bind_btn_{item.Field.Name}";

        // Same treatment as the Reset column: smaller label, explicit width so the padding trims to
        // fit rather than the text running into the next column.
        float height = RobustaImGuiScale.FrameHeight;
        float width = GetContentRegionAvail().X;

        PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize * CompactLabelScale);
        bool pressed = RobustaImGuiWidgets.AccentButton(label, new Vector2(width, height), isListening);
        PopFont();

        if (!pressed)
            return;

        if (isListening)
            StopListening();
        else
            _activeListeningTarget = item;
    }

    private static RobustaKeybind DefaultBind(DiscoveredBind item) =>
        new(item.Attribute.DefaultKey, item.Attribute.DefaultMod1);

    private static string Format(RobustaKeybind bind) => bind.IsValid ? bind.ToString() : "";

    /// <summary>Resets every edit buffer to the live config value, discarding uncommitted text.</summary>
    private void SyncBindBuffers()
    {
        foreach (var item in _discoveredBinds)
        {
            item.EditBuffer = Format((RobustaKeybind)item.Field.GetValue(null)!);
            item.Synced = item.EditBuffer;
        }
    }

    private void ApplyBind(DiscoveredBind target, RobustaKeybind bind)
    {
        target.Field.SetValue(null, bind);
        target.EditBuffer = Format(bind);
        target.Synced = target.EditBuffer;

        RegisterCommand(target.Attribute.Command, bind);

        // A bind is one deliberate action, not a drag, so it is persisted straight away instead of at
        // the next lifecycle boundary. The registry and the file therefore never disagree.
        RobustaConfig.Save();
    }

    /// <summary>
    /// Replaces the live input binding for <paramref name="command"/> so the change takes effect
    /// without a restart. Commands without a <see cref="KeybindConfigAttribute.Command"/> are
    /// consumed elsewhere and only need the config write.
    /// </summary>
    private static void RegisterCommand(string? command, RobustaKeybind bind)
    {
        if (string.IsNullOrEmpty(command) ||
            !RobustaImGuiManager.TryResolve<IInputManager>(out var inputManager))
        {
            return;
        }

        UnregisterCommandBinding(inputManager, command);

        if (!bind.IsValid || bind.BaseKey == Keyboard.Key.Unknown)
            return;

        try
        {
            inputManager.RegisterBinding(new KeyBindingRegistration
            {
                Function = command,
                BaseKey = bind.BaseKey,
                Mod1 = bind.Mod1,
                Mod2 = bind.Mod2,
                Mod3 = bind.Mod3,
                Priority = 10,
                Type = KeyBindingType.Command,
                CommandWhenUIFocused = false
            }, false);
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to register binding for {command}: {ex.Message}");
        }
    }

    private static void UnregisterCommandBinding(IInputManager inputManager, string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var function = new BoundKeyFunction(command);
        while (true)
        {
            try
            {
                var existing = inputManager.GetKeyBinding(function);
                inputManager.RemoveBinding(existing);
            }
            catch (KeyNotFoundException)
            {
                break;
            }
            catch (Exception ex)
            {
                MarseyLogger.Warn($"Failed while removing binding for {command}: {ex.Message}");
                break;
            }
        }
    }

    private void ResetAllBinds()
    {
        // Each bind persists as it is applied, so a reset writes the file once per row and needs no
        // trailing flush.
        foreach (var item in _discoveredBinds)
            ApplyBind(item, DefaultBind(item));
    }
}
