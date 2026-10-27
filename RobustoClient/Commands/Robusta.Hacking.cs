using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.Systems.Hacking;

namespace RobustoClient.Commands;

[AnyCommand]
public sealed class HackingCommand : IConsoleCommand
{
    public string Command => "robusta.hack";
    public string Description => "Automatically hacks the wire-panel entity under the cursor.";
    public string Help => "Usage: robusta.hack";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        IoCManager.Resolve<IEntityManager>().System<RobustaHackingSystem>().TriggerAtCursor();
    }
}