namespace ArabicaCliento.UI;

public static class ArabicaRadarOpener
{
    private static ArabicaRadarWindow? _window;

    public static void Show()
    {
        if (_window != null && _window.IsOpen)
        {
            _window.MoveToFront();
            return;
        }

        _window = new ArabicaRadarWindow();
        _window.OpenCentered();
    }
}