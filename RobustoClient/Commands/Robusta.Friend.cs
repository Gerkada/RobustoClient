using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class RobustaFriendCommand : IConsoleCommand
{
    public string Command => "robusta.friend";
    public string Description => "Add username to friend-list";
    public string Help => "robusta.friend <username>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Invalid args count");
            return;
        }
        
        if (RobustaConfig.FriendsSet.Add(args[0]))
            shell.WriteLine("Username is successfully added");
        else
            shell.WriteError("Username is already presented");
    }
}