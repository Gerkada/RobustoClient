using System;
using System.Collections.Generic;
using System.Text;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using RobustoClient.Systems.Abstract;
using RobustoClient.Systems.Macros;

namespace RobustoClient.UI.ImGui.Radial;

/// <summary>
/// Resolves and fires the action a radial sector names. A slot holds either a console command or a
/// bound key function, the pointer capture taken when the wheel opened is still active here, so
/// cursor-based commands act on the target the wheel was centred on.
/// </summary>
internal static class RadialMenuActions
{
    private static List<(string Id, string Label)>? _available;

    /// <summary>
    /// Short display names for the mod's console commands. The command id is the stable identifier;
    /// this is what the picker and the wheel sectors show, because the derived form ("Anti Slip Set
    /// Enabled") does not fit a combo or a sector. A command missing here still works and falls back
    /// to <see cref="DeriveDisplay"/>.
    /// </summary>
    private static readonly Dictionary<string, string> _shortNames = new(StringComparer.Ordinal)
    {
        ["robusta.anti_slip.set_enabled"] = "Anti-Slip",
        ["robusta.auto_construct"] = "Construct",
        ["robusta.auto_deconstruct"] = "Deconstruct",
        ["robusta.auto_zoom.set"] = "Zoom Set",
        ["robusta.auto_zoom.toggle"] = "Zoom Toggle",
        ["robusta.autoreload"] = "Auto-Reload",
        ["robusta.esp.toggle"] = "ESP Toggle",
        ["robusta.friend"] = "Friend",
        ["robusta.friend_list"] = "Friend List",
        ["robusta.hack"] = "Hack",
        ["robusta.melee_aimbot.toggle"] = "Melee Aimbot",
        ["robusta.player_info"] = "Player Info",
        ["robusta.player_list"] = "Player List",
        ["robusta.ranged_aimbot.toggle"] = "Ranged Aimbot",
        ["robusta.spin.set_degrees"] = "Spin Degrees",
        ["robusta.spin.set_enabled"] = "Spin Toggle",
        ["robusta.toggle_menu"] = "Menu",
        ["robusta.unfriend"] = "Unfriend",
        ["robusta.view_inv"] = "View Inv"
    };

    /// <summary>
    /// Console commands the picker offers. Built once, because command registration is finished
    /// before the first frame.
    /// </summary>
    public static IReadOnlyList<(string Id, string Label)> Available
    {
        get
        {
            if (_available != null)
                return _available;

            // Retry until the console host is up, caching an empty list would hide every command.
            if (!RobustaImGuiManager.TryResolve<IConsoleHost>(out var console))
                return Array.Empty<(string Id, string Label)>();

            var available = new List<(string Id, string Label)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Robusto's own commands only. The free-text box still accepts any string, but the
            // picker stays a short list of the mod's actions rather than every engine key function.
            foreach (var command in console.AvailableCommands.Keys)
            {
                if (command.StartsWith("robusta.", StringComparison.Ordinal) && seen.Add(command))
                    available.Add((command, Display(command)));
            }

            available.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            // Macros after the console commands, so the mod's own actions stay at the top.
            foreach (var macro in RobustaMacroSystem.Macros)
                available.Add((macro.Id, PickerLabel(macro.Id)));

            _available = available;
            return _available;
        }
    }

    public static void Execute(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        if (RobustaMacroSystem.TryGetMacro(action, out var macro))
        {
            if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
                macro.Execute(entMan.System<RobustaMacroSystem>());

            return;
        }

        if (RobustaImGuiManager.TryResolve<IConsoleHost>(out var console) &&
            console.AvailableCommands.ContainsKey(action))
        {
            console.ExecuteCommand(action);
            return;
        }

        FireKeyFunction(action);
    }

    /// <summary>
    /// Label for the F4 picker and its combo preview. Macros are tagged so they are distinguishable
    /// from console commands while choosing what to put in a sector.
    /// </summary>
    internal static string PickerLabel(string action)
    {
        if (RobustaMacroSystem.TryGetMacro(action, out var macro))
            return $"[MACRO] {macro.Label}";

        return Display(action);
    }

    /// <summary>
    /// Label for the wheel itself. A macro answers with its bare label so it fits a sector, a known
    /// console command with its short name, and anything else with the derived form so a hand-typed
    /// key function is still readable.
    /// </summary>
    internal static string Display(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "";

        if (RobustaMacroSystem.TryGetMacro(action, out var macro))
            return macro.Label;

        if (_shortNames.TryGetValue(action, out var shortName))
            return shortName;

        return DeriveDisplay(action);
    }

    /// <summary>
    /// Fallback display name built from the id: strips the mod prefix, splits words and capitalises
    /// them, so a command added without a short name reads acceptably rather than as a raw id.
    /// </summary>
    internal static string DeriveDisplay(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "";

        var text = action.StartsWith("robusta.", StringComparison.Ordinal)
            ? action["robusta.".Length..].Replace('_', ' ').Replace('.', ' ')
            : action;

        var builder = new StringBuilder(text.Length + 4);
        bool startOfWord = true;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == ' ')
            {
                builder.Append(c);
                startOfWord = true;
                continue;
            }

            if (!startOfWord && char.IsUpper(c) && !char.IsUpper(text[i - 1]))
                builder.Append(' ');

            builder.Append(startOfWord ? char.ToUpperInvariant(c) : c);
            startOfWord = false;
        }

        return builder.ToString();
    }

    private static void FireKeyFunction(string action)
    {
        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) ||
            !RobustaImGuiManager.TryResolve<IInputManager>(out var input) ||
            !RobustaImGuiManager.TryResolve<IPlayerManager>(out var player) ||
            !RobustaImGuiManager.TryResolve<IGameTiming>(out var timing))
        {
            return;
        }

        if (player.LocalSession is not { } session)
            return;

        if (!input.NetworkBindMap.FunctionExists(action))
        {
            MarseyLogger.Warn($"[Radial] Unknown action '{action}': not a console command or a bound key function.");
            return;
        }

        var function = new BoundKeyFunction(action);
        var keyFunctionId = input.NetworkBindMap.KeyFunctionID(function);

        var mouse = entMan.System<MouseUtilitySystem>();
        var coords = mouse.TryGetMouseMapCoordinates(out var map)
            ? entMan.System<SharedTransformSystem>().ToCoordinates(map)
            : EntityCoordinates.Invalid;

        var screen = mouse.MouseScreenPosition;
        var target = mouse.GetHoveredEntity(range: 0f) ?? EntityUid.Invalid;

        var down = new ClientFullInputCmdMessage(timing.CurTick, timing.TickFraction, keyFunctionId)
        {
            State = BoundKeyState.Down,
            Coordinates = coords,
            ScreenCoordinates = screen,
            Uid = target
        };
        var up = new ClientFullInputCmdMessage(timing.CurTick, timing.TickFraction, keyFunctionId)
        {
            State = BoundKeyState.Up,
            Coordinates = coords,
            ScreenCoordinates = screen,
            Uid = target
        };

        var inputSystem = entMan.System<InputSystem>();
        inputSystem.HandleInputCommand(session, function, down);
        inputSystem.HandleInputCommand(session, function, up);
    }
}
