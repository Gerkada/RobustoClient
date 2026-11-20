using Content.Shared.Administration;
using Content.Shared.Mobs.Components;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems.Abstract;
using RobustoClient.UI.ImGui;

namespace RobustoClient.Commands;

[AnyCommand]
public sealed class PlayerInfoCommand : IConsoleCommand
{
    public string Command => "robusta.player_info";
    public string Description => "Opens the radar Info panel for the entity under cursor, locked target, or self.";
    public string Help => "robusta.player_info [optional EntityUid]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();

        if (args.Length == 1 && int.TryParse(args[0], out var rawUid))
        {
            var target = new EntityUid(rawUid);
            if (!entMan.EntityExists(target))
            {
                shell.WriteError($"Entity {target} does not exist.");
                return;
            }

            RobustaImGuiManager.RadarInfo.Show(target);
            shell.WriteLine($"[PlayerInfo] Opened info for entity {target}.");
            return;
        }

        if (!entMan.System<MouseUtilitySystem>().TryResolveTarget(out var resolved, whitelist: [typeof(MobStateComponent)]))
        {
            shell.WriteError("[PlayerInfo] No target under the cursor, in lock, or local player.");
            return;
        }

        RobustaImGuiManager.RadarInfo.Show(resolved);
        shell.WriteLine($"[PlayerInfo] Opened info for entity {resolved}.");
    }
}
