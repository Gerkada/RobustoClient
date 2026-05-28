using Content.Shared.Administration;
using Robust.Client.Player;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class RobustaPlayerList : IConsoleCommand
{
    public string Command => "robusta.player_list";
    public string Description => "Output a playerlist";
    public string Help => "robusta.player_list";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var man = IoCManager.Resolve<IPlayerManager>();
        foreach (var ses in man.Sessions)
        {
            shell.WriteLine(ses.Name);
        }
    }
}