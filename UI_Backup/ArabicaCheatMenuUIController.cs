using JetBrains.Annotations;
using Robust.Client.UserInterface.Controllers;

namespace ArabicaCliento.UI;

[UsedImplicitly]
public class ArabicaCheatMenuUiController : UIController
{
    private CheatMenuWindow _window = null!;

    
    private void EnsureMenu()
    {
        if (_window is { Disposed: false })
            return;

        _window = UIManager.CreateWindow<CheatMenuWindow>();
    }
    
    public void ToggleMenu()
    {
        EnsureMenu();
        if (_window.IsOpen)
            _window.Close();
        else
            _window.OpenCentered();
    }
}