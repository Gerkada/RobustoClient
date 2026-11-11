using System.Numerics;
using Hexa.NET.ImGui;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Timing;
using RobustoClient.UI.ImGui;

namespace RobustoClient.ImGui;

public static class ImGuiInputBridge
{
    private static bool _textInputActive;
    private static Vector2 _lastMousePos;
    private static bool _hasMousePos;
    private static bool _mouseButtonQueuedThisFrame;

    public static void OnWindowVisibilityChanged(bool anyVisible)
    {
        if (!RobustaImGuiManager.IsInitialized)
            return;

        RobustaImGuiManager.EnsureCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.ClearEventsQueue();
        io.ClearInputKeys();
        _mouseButtonQueuedThisFrame = false;

        if (anyVisible && _hasMousePos)
            io.AddMousePosEvent(_lastMousePos.X, _lastMousePos.Y);
    }

    public static void UpdateBeforeNewFrame(
        IClyde clyde,
        IGameTiming timing,
        IInputManager inputManager,
        IUserInterfaceManager uiManager)
    {
        RobustaImGuiManager.EnsureCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.ConfigInputTrickleEventQueue = false;

        var screenSize = clyde.ScreenSize;
        io.DisplaySize = new Vector2(Math.Max(1, screenSize.X), Math.Max(1, screenSize.Y));
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = MathF.Max((float)timing.FrameTime.TotalSeconds, 1e-4f);

        var mousePos = inputManager.MouseScreenPosition;
        if (mousePos.IsValid)
        {
            _lastMousePos = mousePos.Position;
            _hasMousePos = true;
        }

        // Do not append a trailing MousePos event after a MouseButton event was already queued this frame.
        if (_hasMousePos && !_mouseButtonQueuedThisFrame && io.MousePos != _lastMousePos)
        {
            io.AddMousePosEvent(_lastMousePos.X, _lastMousePos.Y);
        }

        _mouseButtonQueuedThisFrame = false;

        bool wantText = RobustaImGuiManager.AnyWindowVisible && io.WantTextInput;
        if (wantText != _textInputActive)
        {
            _textInputActive = wantText;
            try
            {
                if (wantText)
                    clyde.MainWindow.TextInputStart();
                else if (uiManager.KeyboardFocused == null)
                    clyde.MainWindow.TextInputStop();
            }
            catch
            {
                // Windowing not ready yet.
            }
        }
    }

    public static void OnMouseMove(MouseMoveEventArgs ev)
    {
        if (ev.Position.IsValid)
        {
            _lastMousePos = ev.Position.Position;
            _hasMousePos = true;
        }

        if (!RobustaImGuiManager.IsInitialized || !RobustaImGuiManager.AnyWindowVisible)
            return;

        RobustaImGuiManager.EnsureCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.ConfigInputTrickleEventQueue = false;
        io.AddMousePosEvent(ev.Position.Position.X, ev.Position.Position.Y);
    }

    /// <summary>Returns true if the scroll event should be blocked from reaching SS14.</summary>
    public static bool OnMouseWheel(MouseWheelEventArgs ev)
    {
        if (ev.Position.IsValid)
        {
            _lastMousePos = ev.Position.Position;
            _hasMousePos = true;
        }

        if (!RobustaImGuiManager.IsInitialized || !RobustaImGuiManager.AnyWindowVisible)
            return false;

        RobustaImGuiManager.EnsureCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.ConfigInputTrickleEventQueue = false;
        if (_hasMousePos)
            io.AddMousePosEvent(_lastMousePos.X, _lastMousePos.Y);
        io.AddMouseWheelEvent(ev.Delta.X, ev.Delta.Y);

        return io.WantCaptureMouse;
    }

    /// <summary>Returns true if the key/mouse-down event should be blocked from reaching SS14.</summary>
    public static bool OnKeyDown(KeyEventArgs ev)
    {
        if (!RobustaImGuiManager.IsInitialized || !RobustaImGuiManager.AnyWindowVisible)
            return false;

        RobustaImGuiManager.EnsureCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.ConfigInputTrickleEventQueue = false;

        if (ev.Key.IsMouseKey())
        {
            SyncMousePositionBeforeButtonEvent(io);

            int btn = MapMouseButton(ev.Key);
            if (btn >= 0)
            {
                io.AddMouseButtonEvent(btn, true);
                _mouseButtonQueuedThisFrame = true;
            }

            if (io.WantCaptureMouse)
            {
                if (RobustaImGuiManager.TryResolve<IUserInterfaceManager>(out var uiMan) && uiMan.KeyboardFocused != null)
                {
                    uiMan.ReleaseKeyboardFocus();
                    _textInputActive = false;
                }
                return true;
            }

            if (RobustaImGuiManager.CheatMenu.IsListeningForKeybind)
            {
                RobustaImGuiManager.CheatMenu.StopListening();
            }

            return false;
        }

        // If the Binds tab is actively listening for a key combination, capture it and swallow the event.
        if (RobustaImGuiManager.CheatMenu.IsListeningForKeybind &&
            RobustaImGuiManager.CheatMenu.TryHandleKeybindListening(ev))
        {
            return true;
        }

        UpdateModifiers(io, ev);
        var imguiKey = MapKey(ev.Key);
        if (imguiKey != ImGuiKey.None)
            io.AddKeyEvent(imguiKey, true);

        // Always allow ToggleMenuKey (e.g. F4) through so the menu can always be toggled.
        if (ev.Key == RobustaConfig.ToggleMenuKey.BaseKey)
            return false;

        // Block game hotkeys/WASD/chat only when an ImGui text input widget is focused.
        return io.WantTextInput;
    }

    /// <summary>Returns true if the key/mouse-up event should be blocked from reaching SS14.</summary>
    public static bool OnKeyUp(KeyEventArgs ev)
    {
        if (!RobustaImGuiManager.IsInitialized || !RobustaImGuiManager.AnyWindowVisible)
            return false;

        RobustaImGuiManager.EnsureCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.ConfigInputTrickleEventQueue = false;

        if (ev.Key.IsMouseKey())
        {
            SyncMousePositionBeforeButtonEvent(io);

            int btn = MapMouseButton(ev.Key);
            if (btn >= 0)
            {
                io.AddMouseButtonEvent(btn, false);
                _mouseButtonQueuedThisFrame = true;
            }

            // Never block MouseUp so SS14 doesn't get stuck with a held button.
            return false;
        }

        UpdateModifiers(io, ev);
        var imguiKey = MapKey(ev.Key);
        if (imguiKey != ImGuiKey.None)
            io.AddKeyEvent(imguiKey, false);

        return false;
    }

    private static void SyncMousePositionBeforeButtonEvent(ImGuiIOPtr io)
    {
        if (RobustaImGuiManager.TryResolve<IInputManager>(out var inputMan))
        {
            var mousePos = inputMan.MouseScreenPosition;
            if (mousePos.IsValid)
            {
                _lastMousePos = mousePos.Position;
                _hasMousePos = true;
            }
        }

        if (_hasMousePos)
            io.AddMousePosEvent(_lastMousePos.X, _lastMousePos.Y);
    }

    /// <summary>Returns true if the text input event should be blocked from reaching SS14.</summary>
    public static bool OnTextEntered(TextEnteredEventArgs ev)
    {
        if (!RobustaImGuiManager.IsInitialized || !RobustaImGuiManager.AnyWindowVisible)
            return false;

        var io = Hexa.NET.ImGui.ImGui.GetIO();
        if (!string.IsNullOrEmpty(ev.Text))
            io.AddInputCharactersUTF8(ev.Text);

        return io.WantTextInput;
    }

    private static void UpdateModifiers(ImGuiIOPtr io, KeyEventArgs ev)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, ev.Control || ev.Key == Keyboard.Key.Control);
        io.AddKeyEvent(ImGuiKey.ModShift, ev.Shift || ev.Key == Keyboard.Key.Shift);
        io.AddKeyEvent(ImGuiKey.ModAlt, ev.Alt || ev.Key == Keyboard.Key.Alt);
        io.AddKeyEvent(ImGuiKey.ModSuper, ev.System || ev.Key is Keyboard.Key.LSystem or Keyboard.Key.RSystem);
    }

    private static int MapMouseButton(Keyboard.Key key) => key switch
    {
        Keyboard.Key.MouseLeft => 0,
        Keyboard.Key.MouseRight => 1,
        Keyboard.Key.MouseMiddle => 2,
        Keyboard.Key.MouseButton4 => 3,
        Keyboard.Key.MouseButton5 => 4,
        _ => -1
    };

    private static ImGuiKey MapKey(Keyboard.Key key) => key switch
    {
        Keyboard.Key.Tab => ImGuiKey.Tab,
        Keyboard.Key.Left => ImGuiKey.LeftArrow,
        Keyboard.Key.Right => ImGuiKey.RightArrow,
        Keyboard.Key.Up => ImGuiKey.UpArrow,
        Keyboard.Key.Down => ImGuiKey.DownArrow,
        Keyboard.Key.PageUp => ImGuiKey.PageUp,
        Keyboard.Key.PageDown => ImGuiKey.PageDown,
        Keyboard.Key.Home => ImGuiKey.Home,
        Keyboard.Key.End => ImGuiKey.End,
        Keyboard.Key.Insert => ImGuiKey.Insert,
        Keyboard.Key.Delete => ImGuiKey.Delete,
        Keyboard.Key.BackSpace => ImGuiKey.Backspace,
        Keyboard.Key.Space => ImGuiKey.Space,
        Keyboard.Key.Return => ImGuiKey.Enter,
        Keyboard.Key.NumpadEnter => ImGuiKey.KeypadEnter,
        Keyboard.Key.Escape => ImGuiKey.Escape,
        Keyboard.Key.Control => ImGuiKey.LeftCtrl,
        Keyboard.Key.Shift => ImGuiKey.LeftShift,
        Keyboard.Key.Alt => ImGuiKey.LeftAlt,
        Keyboard.Key.LSystem => ImGuiKey.LeftSuper,
        Keyboard.Key.RSystem => ImGuiKey.RightSuper,
        Keyboard.Key.A => ImGuiKey.A,
        Keyboard.Key.B => ImGuiKey.B,
        Keyboard.Key.C => ImGuiKey.C,
        Keyboard.Key.D => ImGuiKey.D,
        Keyboard.Key.E => ImGuiKey.E,
        Keyboard.Key.F => ImGuiKey.F,
        Keyboard.Key.G => ImGuiKey.G,
        Keyboard.Key.H => ImGuiKey.H,
        Keyboard.Key.I => ImGuiKey.I,
        Keyboard.Key.J => ImGuiKey.J,
        Keyboard.Key.K => ImGuiKey.K,
        Keyboard.Key.L => ImGuiKey.L,
        Keyboard.Key.M => ImGuiKey.M,
        Keyboard.Key.N => ImGuiKey.N,
        Keyboard.Key.O => ImGuiKey.O,
        Keyboard.Key.P => ImGuiKey.P,
        Keyboard.Key.Q => ImGuiKey.Q,
        Keyboard.Key.R => ImGuiKey.R,
        Keyboard.Key.S => ImGuiKey.S,
        Keyboard.Key.T => ImGuiKey.T,
        Keyboard.Key.U => ImGuiKey.U,
        Keyboard.Key.V => ImGuiKey.V,
        Keyboard.Key.W => ImGuiKey.W,
        Keyboard.Key.X => ImGuiKey.X,
        Keyboard.Key.Y => ImGuiKey.Y,
        Keyboard.Key.Z => ImGuiKey.Z,
        Keyboard.Key.Num0 => ImGuiKey.Key0,
        Keyboard.Key.Num1 => ImGuiKey.Key1,
        Keyboard.Key.Num2 => ImGuiKey.Key2,
        Keyboard.Key.Num3 => ImGuiKey.Key3,
        Keyboard.Key.Num4 => ImGuiKey.Key4,
        Keyboard.Key.Num5 => ImGuiKey.Key5,
        Keyboard.Key.Num6 => ImGuiKey.Key6,
        Keyboard.Key.Num7 => ImGuiKey.Key7,
        Keyboard.Key.Num8 => ImGuiKey.Key8,
        Keyboard.Key.Num9 => ImGuiKey.Key9,
        Keyboard.Key.F1 => ImGuiKey.F1,
        Keyboard.Key.F2 => ImGuiKey.F2,
        Keyboard.Key.F3 => ImGuiKey.F3,
        Keyboard.Key.F4 => ImGuiKey.F4,
        Keyboard.Key.F5 => ImGuiKey.F5,
        Keyboard.Key.F6 => ImGuiKey.F6,
        Keyboard.Key.F7 => ImGuiKey.F7,
        Keyboard.Key.F8 => ImGuiKey.F8,
        Keyboard.Key.F9 => ImGuiKey.F9,
        Keyboard.Key.F10 => ImGuiKey.F10,
        Keyboard.Key.F11 => ImGuiKey.F11,
        Keyboard.Key.F12 => ImGuiKey.F12,
        _ => ImGuiKey.None
    };
}
