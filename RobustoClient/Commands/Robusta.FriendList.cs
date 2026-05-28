using Content.Shared.Administration;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class RobustaFriendList : IConsoleCommand
{
    public string Command => "robusta.friend_list";
    public string Description => "Output a friendlist";
    public string Help => "robusta.friend_list";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        foreach (var friend in RobustaConfig.FriendsSet)
        {
            shell.WriteLine(friend);
        }
    }
}