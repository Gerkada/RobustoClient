using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

/// <summary>
/// Global ESP switch.
/// </summary>
[AnyCommand]
public class ToggleEspCommand : IConsoleCommand
{
    public string Command => "robusta.esp.toggle";
    public string Description => "Toggles every ESP overlay on or off, keeping the per-category selection";
    public string Help => "robusta.esp.toggle";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        RobustaConfig.EspOverlayEnabled = !RobustaConfig.EspOverlayEnabled;
        RobustaConfig.Save();

        shell.WriteLine($"[Robusta] ESP {(RobustaConfig.EspOverlayEnabled ? "enabled" : "disabled")}.");
    }
}
