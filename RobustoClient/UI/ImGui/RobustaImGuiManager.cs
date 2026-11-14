using System.Numerics;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Timing;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public static unsafe class RobustaImGuiManager
{
    private static nint _context;
    private static ImGuiGl3Renderer? _renderer;
    private static bool _wasAnyWindowVisible;

    public static bool IsInitialized { get; private set; }

    /// <summary>Active GL backend. Null until <see cref="EnsureInitialized"/> has run.</summary>
    public static ImGuiGl3Renderer? Renderer => _renderer;

    public static CheatMenuImGui CheatMenu { get; } = new();
    public static RadarImGui Radar { get; } = new();
    public static InventoryViewerImGui InventoryViewer { get; } = new();
    public static AutoChemImGui AutoChem { get; } = new();

    public static bool AnyWindowVisible =>
        CheatMenu.IsOpen || Radar.IsOpen || InventoryViewer.IsOpen || AutoChem.IsOpen;

    public static bool TryResolve<T>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? instance) where T : class
    {
        if (IoCManager.Instance != null && IoCManager.Instance.TryResolveType(out instance))
            return true;
        instance = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureInitialized(IClyde clyde)
    {
        if (IsInitialized)
            return;

        var ctx = Hexa.NET.ImGui.ImGui.CreateContext();
        _context = (nint)ctx.Handle;
        Hexa.NET.ImGui.ImGui.SetCurrentContext(ctx);

        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.IniFilename = null;
        io.LogFilename = null;

        // Trickling reorders queued events within a frame and breaks the click registration the
        // input bridge relies on. Set once here; nothing else should touch it.
        io.ConfigInputTrickleEventQueue = false;

        _renderer = new ImGuiGl3Renderer();
        _renderer.Initialize(clyde);

        // Restore the persisted theme before the first frame so there is no flash of the default.
        RobustaImGuiTheme.RestoreSelectedTheme();
        RobustaImGuiTheme.Apply();
        CheatMenu.Initialize();

        IsInitialized = true;
    }

    internal static void EnsureCurrentContext()
    {
        if (_context != 0)
            Hexa.NET.ImGui.ImGui.SetCurrentContext(new ImGuiContextPtr((ImGuiContext*)_context));
    }

    public static void RenderFrame()
    {
        ImGuiNativeLoader.Initialize();
        RenderFrameCore();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RenderFrameCore()
    {
        if (!TryResolve<IClyde>(out var clyde) ||
            !TryResolve<IGameTiming>(out var timing) ||
            !TryResolve<IInputManager>(out var inputMan) ||
            !TryResolve<IUserInterfaceManager>(out var uiMan))
        {
            return;
        }

        EnsureInitialized(clyde);
        Hexa.NET.ImGui.ImGui.SetCurrentContext(new ImGuiContextPtr((ImGuiContext*)_context));

        // Palette edits from the Themes tab arrive as a dirty flag; re-applying is cheap struct writes.
        if (RobustaImGuiTheme.StyleDirty)
        {
            RobustaImGuiTheme.ApplyStyle();
            RobustaImGuiTheme.ClearStyleDirty();
        }

        bool anyVisible = AnyWindowVisible;
        if (!anyVisible && !_wasAnyWindowVisible)
            return;

        if (anyVisible != _wasAnyWindowVisible)
        {
            _wasAnyWindowVisible = anyVisible;
            ImGuiInputBridge.OnWindowVisibilityChanged(anyVisible);
        }

        ImGuiInputBridge.UpdateBeforeNewFrame(clyde, timing, inputMan, uiMan);
        Hexa.NET.ImGui.ImGui.NewFrame();

        if (!RobustaImGuiTheme.FontRegular.IsNull)
            Hexa.NET.ImGui.ImGui.PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize);

        if (CheatMenu.IsOpen)
            CheatMenu.Draw();

        if (Radar.IsOpen)
            Radar.Draw();

        if (InventoryViewer.IsOpen)
            InventoryViewer.Draw();

        if (AutoChem.IsOpen)
            AutoChem.Draw();

        if (!RobustaImGuiTheme.FontRegular.IsNull)
            Hexa.NET.ImGui.ImGui.PopFont();

        Hexa.NET.ImGui.ImGui.Render();
        if (anyVisible)
            _renderer!.RenderDrawData(Hexa.NET.ImGui.ImGui.GetDrawData());

        // One-shot: only the first frame after reopening sees this as true.
        ImGuiInputBridge.ConsumeVisibilityEdge();
    }
}
