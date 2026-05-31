using Robust.Shared.Console;
using Robust.Shared.IoC;
using Robust.Shared.GameObjects;
using Content.Shared.Administration;
using RobustoClient.Systems.AutoReload;

namespace RobustoClient.Commands;

[AnyCommand]
public sealed class AutoReloadCommand : IConsoleCommand
{
    public string Command => "robusta.autoreload";
    public string Description => "Triggers the smart auto-reload macro.";
    public string Help => "Usage: robusta.autoreload";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entManager = IoCManager.Resolve<IEntityManager>();
        var sys = entManager.System<RobustaAutoReloadSystem>();
        
        sys.StartReload();
        shell.WriteLine("[AutoReload] Command sent to system.");
    }
}
