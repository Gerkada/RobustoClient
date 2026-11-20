using System.Numerics;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Timing;
using RobustoClient.ImGui;
using RobustoClient.UI.ImGui.Radial;

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
    public static RadarInfoImGui RadarInfo { get; } = new();
    public static InventoryViewerImGui InventoryViewer { get; } = new();
    public static AutoChemImGui AutoChem { get; } = new();
    public static RadialMenuImGui RadialMenu { get; } = new();

    public static bool AnyWindowVisible =>
        CheatMenu.IsOpen || Radar.IsOpen || RadarInfo.IsOpen ||
        InventoryViewer.IsOpen || AutoChem.IsOpen || RadialMenu.IsActive;

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

        var ctx = CreateContext();
        _context = (nint)ctx.Handle;
        SetCurrentContext(ctx);

        var io = GetIO();
        io.IniFilename = null;
        io.LogFilename = null;

        // Trickling reorders queued events within a frame and breaks the click registration the
        // input bridge relies on. Set once here, nothing else should touch it.
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
            SetCurrentContext(new ImGuiContextPtr((ImGuiContext*)_context));
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
        SetCurrentContext(new ImGuiContextPtr((ImGuiContext*)_context));

        // Palette edits from the Themes tab arrive as a dirty flag, re-applying is cheap struct writes.
        if (RobustaImGuiTheme.StyleDirty)
        {
            RobustaImGuiTheme.ApplyStyle();
            RobustaImGuiTheme.ClearStyleDirty();
        }

        // The radial arms and commits before the visibility gate: while nothing is visible this
        // method returns before NewFrame, so a held bind would otherwise never be seen.
        RadialMenu.PollHold(inputMan, uiMan);

        bool anyVisible = AnyWindowVisible;
        if (!anyVisible && !_wasAnyWindowVisible)
            return;

        if (anyVisible != _wasAnyWindowVisible)
        {
            _wasAnyWindowVisible = anyVisible;
            ImGuiInputBridge.OnWindowVisibilityChanged(anyVisible);
        }

        ImGuiInputBridge.UpdateBeforeNewFrame(clyde, timing, inputMan, uiMan);
        NewFrame();

        // A throw between NewFrame and Render leaves the frame open, and the next NewFrame trips a
        // native assertion that kills the process. Draw failures are contained and the frame is
        // unwound, so one bad window cannot take the client down.
        var depthBefore = CurrentStackDepth();
        try
        {
            if (!RobustaImGuiTheme.FontRegular.IsNull)
                PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize);

            DrawWindows();

            if (!RobustaImGuiTheme.FontRegular.IsNull)
                PopFont();
        }
        catch (Exception e)
        {
            MarseyLogger.Warn($"[ImGui] A window threw while drawing, unwinding the frame: {e}");
            UnwindFrame(depthBefore);
        }

        Render();
        if (anyVisible)
            _renderer!.RenderDrawData(GetDrawData());

        // One-shot: only the first frame after reopening sees this as true.
        ImGuiInputBridge.ConsumeVisibilityEdge();
    }

    /// <summary>Window and font stack sizes, in one value, so an unwind can restore exactly this.</summary>
    internal readonly record struct FrameStackDepth(int Windows, int Fonts);

    /// <summary>Reads the current stack depth, so tests can drive the unwind the way the frame loop does.</summary>
    internal static FrameStackDepth CurrentStackDepthForTests() => CurrentStackDepth();

    private static FrameStackDepth CurrentStackDepth()
    {
        var context = GetCurrentContext();
        return context.Handle == null
            ? new FrameStackDepth(0, 0)
            : new FrameStackDepth(context.Handle->CurrentWindowStack.Size, context.Handle->FontStack.Size);
    }

    /// <summary>
    /// Restores the stacks to their depth at the start of the frame. ImGui asserts on a window or font
    /// stack left unbalanced, so this must run before <c>Render()</c>. It unwinds to the recorded
    /// depth rather than popping everything: a draw can throw after its own <c>PopFont</c>, and
    /// popping again asserts "Calling PopFont() too many times!".
    /// </summary>
    internal static void UnwindFrame(FrameStackDepth depth)
    {
        var context = GetCurrentContext();
        if (context.Handle == null)
            return;

        while (context.Handle->CurrentWindowStack.Size > depth.Windows)
            End();

        while (context.Handle->FontStack.Size > depth.Fonts)
            PopFont();
    }

    /// <summary>Draws every open window. Split out so the caller can guarantee <c>Render()</c> runs.</summary>
    private static void DrawWindows()
    {
        if (CheatMenu.IsOpen)
            CheatMenu.Draw();

        if (Radar.IsOpen)
            Radar.Draw();

        if (RadarInfo.IsOpen)
            RadarInfo.Draw();

        if (InventoryViewer.IsOpen)
            InventoryViewer.Draw();

        if (AutoChem.IsOpen)
            AutoChem.Draw();

        if (RadialMenu.IsActive)
            RadialMenu.Draw();
    }
}
