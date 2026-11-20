using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class ToggleMeleeAimbotCommand : IConsoleCommand
{
    public string Command => "robusta.melee_aimbot.toggle";
    public string Description => "Toggles melee aimbot";
    public string Help => "robusta.melee_aimbot.toggle";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        RobustaConfig.MeleeAimbotEnabled = !RobustaConfig.MeleeAimbotEnabled;
        RobustaConfig.Save();

        shell.WriteLine($"[Robusta] Melee aimbot {(RobustaConfig.MeleeAimbotEnabled ? "enabled" : "disabled")}.");
    }
}
