using RobustoClient.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.IoC;

namespace RobustoClient.Commands;

[AnyCommand]
public class ForceZoomCommand : IConsoleCommand
{
    public string Command => "robusta.auto_zoom.set";
    public string Description => "Set forced zoom";
    public string Help => "robusta.auto_zoom.set <zoom>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Invalid args count");
            return;
        }

        if (!float.TryParse(args[0], out var zoom))
        {
            shell.WriteError($"Unable to parse {args[0]} as float");
            return;
        }

        var sys = IoCManager.Resolve<IEntityManager>().System<RobustaAutoZoomSystem>();
        sys.UpdateZoom(zoom);
        shell.WriteLine($"[Robusta] Force zoom set to {zoom} and enabled.");
    }
}

[AnyCommand]
public class ToggleZoomCommand : IConsoleCommand
{
    public string Command => "robusta.auto_zoom.toggle";
    public string Description => "Toggles the forced zoom on or off";
    public string Help => "robusta.auto_zoom.toggle";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var sys = IoCManager.Resolve<IEntityManager>().System<RobustaAutoZoomSystem>();
        sys.ToggleZoom();
        shell.WriteLine("[Robusta] Force zoom toggled.");
    }
}