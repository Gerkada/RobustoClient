namespace RobustoClient.UI;

public static class RobustaRadarOpener
{
    private static RobustaRadarWindow? _window;

    public static void Show()
    {
        if (_window != null && _window.IsOpen)
        {
            _window.MoveToFront();
            return;
        }

        _window = new RobustaRadarWindow();
        _window.OpenCentered();
    }
}