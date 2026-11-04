using JetBrains.Annotations;
using Robust.Client.UserInterface.Controllers;
using RobustoClient.UI.ImGui;

namespace RobustoClient.UI;

[UsedImplicitly]
public class RobustaCheatMenuUiController : UIController
{
    public void ToggleMenu()
    {
        RobustaImGuiManager.CheatMenu.Toggle();
    }
}
