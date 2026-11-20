using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class ToggleRangedAimbotCommand : IConsoleCommand
{
    public string Command => "robusta.ranged_aimbot.toggle";
    public string Description => "Toggles ranged aimbot";
    public string Help => "robusta.ranged_aimbot.toggle";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        RobustaConfig.RangedAimbotEnabled = !RobustaConfig.RangedAimbotEnabled;
        RobustaConfig.Save();

        shell.WriteLine($"[Robusta] Ranged aimbot {(RobustaConfig.RangedAimbotEnabled ? "enabled" : "disabled")}.");
    }
}
