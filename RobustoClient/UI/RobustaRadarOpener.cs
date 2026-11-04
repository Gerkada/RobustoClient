using RobustoClient.UI.ImGui;

namespace RobustoClient.UI;

public static class RobustaRadarOpener
{
    public static void Show()
    {
        RobustaImGuiManager.Radar.IsOpen = true;
    }
}
