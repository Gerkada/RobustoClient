using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class RobustaUnfriendCommand : IConsoleCommand
{
    public string Command => "robusta.unfriend";
    public string Description => "Remove username from friend-list";
    public string Help => "robusta.unfriend <username>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Invalid args count");
            return;
        }
        
        if (RobustaConfig.FriendsSet.Remove(args[0]))
            shell.WriteLine("Username is successfully removed");
        else
            shell.WriteError("Username is not presented in friend-list");
    }
}