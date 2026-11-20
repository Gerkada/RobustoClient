using Robust.Client.Input;
using Robust.Shared.Input;
using static Hexa.NET.ImGui.ImGui;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    public void StopListening()
    {
        _captureSink = null;
        _captureOwner = null;
    }

    /// <summary>
    /// Consumes key events while a bind or a wheel key is being captured. Returns true when the event
    /// was handled and must not reach the game, <see cref="RobustaKeybind.IsModifierOrIgnoredKey"/>
    /// are swallowed but do not end the capture, so modifier presses can be part of the combination.
    /// </summary>
    public bool TryHandleKeybindListening(KeyEventArgs ev)
    {
        if (_captureSink == null)
            return false;

        switch (ev.Key)
        {
            case Keyboard.Key.Escape:
                StopListening();
                return true;

            case Keyboard.Key.BackSpace:
            case Keyboard.Key.Delete:
                _captureSink(new RobustaKeybind(Keyboard.Key.Unknown));
                StopListening();
                return true;
        }

        if (RobustaKeybind.IsModifierOrIgnoredKey(ev.Key))
            return true;

        _captureSink(RobustaKeybind.FromCapture(ev.Key, ev.Shift, ev.Control, ev.Alt, ev.System));
        StopListening();
        return true;
    }
}
