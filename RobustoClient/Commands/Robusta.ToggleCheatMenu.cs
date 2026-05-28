using RobustoClient.Systems;
using RobustoClient.UI;
using Content.Shared.Administration;
using Robust.Client.UserInterface;
using Robust.Shared.Console;

namespace RobustoClient.Commands;

[AnyCommand]
public class ToggleCheatMenuCommand: IConsoleCommand
{
    public string Command => "robusta.toggle_menu";
    public string Description => "This command toggle the cheat menu";
    
    public string Help => "robusta.toggle_menu";
    
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        IoCManager.Resolve<IUserInterfaceManager>().GetUIController<RobustaCheatMenuUiController>().ToggleMenu();
    }
}