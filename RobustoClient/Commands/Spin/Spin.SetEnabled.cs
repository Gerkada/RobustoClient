using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands.Spin;

[AnyCommand]
public class RobustaSpinSetEnabled : IConsoleCommand
{
    public string Command => "robusta.spin.set_enabled";
    public string Description => "Enabling a autospin";
    public string Help => "robusta.spin.set_enabled <bool>";
    
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Invalid argument count.");
            return;
        }

        if (!bool.TryParse(args[0], out var enabled))
        {
            shell.WriteError($"Can't parse {args[0]} as bool");
            return;
        }

        RobustaConfig.SpinBotEnabled = enabled;
    }
}