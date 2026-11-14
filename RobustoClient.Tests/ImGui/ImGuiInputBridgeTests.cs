using System.Numerics;
using System.Reflection;
using FluentAssertions;
using HarmonyLib;
using Hexa.NET.ImGui;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Shared.Map;
using RobustoClient.ImGui;
using RobustoClient.Patches;
using RobustoClient.UI.ImGui;
using Xunit;

namespace RobustoClient.Tests.ImGui;

/// <summary>
/// ImGui state and the config dirty flag are process-wide, so these tests share one collection with the
/// config persistence tests and must not run alongside any other class that creates a context.
/// </summary>
[Collection("ProcessWideState")]
public unsafe class ImGuiInputBridgeTests : IDisposable
{
    private static readonly Vector2 ButtonCenter = new(150, 120);

    private readonly string _tempConfigPath = Path.Combine(
        Path.GetTempPath(), "RobustoClient.Tests", Guid.NewGuid().ToString("N"), "config.json");

    public ImGuiInputBridgeTests()
    {
        // Closing a window flushes a dirty config. Without a throwaway path, a stray dirty flag would
        // overwrite the developer's real config.json.
        RobustaConfig.ConfigPathOverride = _tempConfigPath;
        ImGuiNativeLoader.Initialize();
    }

    public void Dispose()
    {
        RobustaConfig.ConfigPathOverride = null;

        try
        {
            var dir = Path.GetDirectoryName(_tempConfigPath);
            if (dir != null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file is not worth failing a test over.
        }
    }

    [Fact]
    public void InputPatchTarget_IsTooLargeForJitInlining()
    {
        var target = AccessTools.Method(typeof(ClydeImGuiInputPatch), "TargetMethod")!.Invoke(null, null) as MethodBase;

        target.Should().NotBeNull();
        target!.Name.Should().Be("DispatchSingleEvent");
        // RyuJIT inlines <=32B IL callees (Clyde.Send* are 20B) before MarseyEntry runs PatchAll, so their prefixes never fire.
        target.GetMethodBody()!.GetILAsByteArray()!.Length.Should().BeGreaterThan(32);
    }

    [Fact]
    public void InputWhileAllWindowsClosed_IsNotQueuedAndDoesNotWedgeLaterClicks()
    {
        SetupHeadlessImGuiContext();
        CloseAllWindows();

        for (int i = 0; i < 200; i++)
        {
            MoveMouse(new Vector2(10 + i % 50, 10 + i % 50));
            var key = i % 2 == 0 ? Keyboard.Key.W : Keyboard.Key.MouseLeft;
            ImGuiInputBridge.OnKeyDown(Key(key));
            ImGuiInputBridge.OnKeyUp(Key(key));
        }

        QueuedEventCount().Should().Be(0, "NewFrame() is skipped while closed, so nothing would ever drain the queue");

        RobustaImGuiManager.CheatMenu.IsOpen = true;
        ClickButtonAfterSettling().Should().BeTrue();
    }

    [Fact]
    public void SameFrameMoveAndClick_RegistersButtonClick()
    {
        SetupHeadlessImGuiContext();
        ClickButtonAfterSettling().Should().BeTrue();
    }

    [Fact]
    public void DispatchPrefix_BlocksClickOverImGuiWindow_AndPassesThroughWhenClosed()
    {
        SetupHeadlessImGuiContext();
        MoveMouse(ButtonCenter);
        DrawSingleButtonFrame();
        DrawSingleButtonFrame();
        Hexa.NET.ImGui.ImGui.GetIO().WantCaptureMouse.Should().BeTrue();

        var mouseDown = NewClydeEvent("DEventKeyDown", Key(Keyboard.Key.MouseLeft));
        var mouseMove = NewClydeEvent("DEventMouseMove",
            new MouseMoveEventArgs(Vector2.Zero, new ScreenCoordinates(ButtonCenter, new WindowId(1))));

        ClydeImGuiInputPatch.DispatchSingleEventPrefix(mouseMove).Should().BeTrue("mouse moves always reach SS14");
        ClydeImGuiInputPatch.DispatchSingleEventPrefix(mouseDown).Should().BeFalse("clicks on an ImGui window must not reach SS14");

        CloseAllWindows();
        ClydeImGuiInputPatch.DispatchSingleEventPrefix(mouseDown).Should().BeTrue("with no ImGui window open, input belongs to SS14");
    }

    // ImGui needs two frames after a window appears: frame 1 cannot hover it, frame 2 runs nav-init which
    // marks the first item as keyboard-focused and would make a click on that exact frame keyboard-sourced.
    private static bool ClickButtonAfterSettling()
    {
        DrawSingleButtonFrame();
        DrawSingleButtonFrame();

        MoveMouse(ButtonCenter);
        ImGuiInputBridge.OnKeyDown(Key(Keyboard.Key.MouseLeft));
        bool clicked = DrawSingleButtonFrame();

        ImGuiInputBridge.OnKeyUp(Key(Keyboard.Key.MouseLeft));
        return DrawSingleButtonFrame() || clicked;
    }

    private static bool DrawSingleButtonFrame()
    {
        Hexa.NET.ImGui.ImGui.NewFrame();
        Hexa.NET.ImGui.ImGui.SetNextWindowPos(new Vector2(50, 50), ImGuiCond.Always);
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(300, 200), ImGuiCond.Always);
        Hexa.NET.ImGui.ImGui.Begin("TestWin", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
        Hexa.NET.ImGui.ImGui.SetCursorScreenPos(new Vector2(100, 100));
        bool clicked = Hexa.NET.ImGui.ImGui.Button("ClickMe", new Vector2(120, 40));
        Hexa.NET.ImGui.ImGui.End();
        Hexa.NET.ImGui.ImGui.Render();
        return clicked;
    }

    private static void MoveMouse(Vector2 pos) =>
        ImGuiInputBridge.OnMouseMove(new MouseMoveEventArgs(Vector2.Zero, new ScreenCoordinates(pos, new WindowId(1))));

    private static KeyEventArgs Key(Keyboard.Key key) => new(key, false, false, false, false, false, 0);

    private static int QueuedEventCount() => Hexa.NET.ImGui.ImGui.GetCurrentContext().Handle->InputEventsQueue.Size;

    private static object NewClydeEvent(string recordName, object args)
    {
        var type = typeof(IClyde).Assembly.GetType("Robust.Client.Graphics.Clyde.Clyde")!
            .GetNestedType(recordName, BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [args], null)!;
    }

    private static void CloseAllWindows()
    {
        RobustaImGuiManager.CheatMenu.IsOpen = false;
        RobustaImGuiManager.Radar.IsOpen = false;
        RobustaImGuiManager.InventoryViewer.IsOpen = false;
        RobustaImGuiManager.AutoChem.IsOpen = false;
    }

    private static void SetupHeadlessImGuiContext()
    {
        var ctx = Hexa.NET.ImGui.ImGui.CreateContext();
        Hexa.NET.ImGui.ImGui.SetCurrentContext(ctx);
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.IniFilename = null;
        io.LogFilename = null;
        io.ConfigInputTrickleEventQueue = false;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        io.DisplaySize = new Vector2(1920, 1080);
        io.DeltaTime = 1f / 60f;
        io.Fonts.AddFontDefault();

        typeof(RobustaImGuiManager)
            .GetField("_context", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, (nint)ctx.Handle);
        typeof(RobustaImGuiManager)
            .GetProperty("IsInitialized", BindingFlags.Static | BindingFlags.Public)!
            .GetSetMethod(true)!
            .Invoke(null, [true]);

        CloseAllWindows();
        RobustaImGuiManager.CheatMenu.IsOpen = true;
    }

    [Fact]
    public void VisibilityEdge_IsSetOnReopen_AndClearedByConsume()
    {
        SetupHeadlessImGuiContext();

        CloseAllWindows();
        ImGuiInputBridge.OnWindowVisibilityChanged(false);
        ImGuiInputBridge.ConsumeVisibilityEdge();

        ImGuiInputBridge.JustBecameVisible.Should().BeFalse();

        // Hiding then showing again is the transition that must be observable exactly once.
        RobustaImGuiManager.CheatMenu.IsOpen = true;
        ImGuiInputBridge.OnWindowVisibilityChanged(true);

        ImGuiInputBridge.JustBecameVisible.Should().BeTrue();

        ImGuiInputBridge.ConsumeVisibilityEdge();
        ImGuiInputBridge.JustBecameVisible.Should().BeFalse("the edge is one-shot");
    }

    [Fact]
    public void CloseAndReopen_DoesNotLeakConfigWrites()
    {
        SetupHeadlessImGuiContext();

        // Close flushes at most once; reopening must not flush again.
        RobustaImGuiManager.CheatMenu.IsOpen = false;

        var dirtyField = typeof(RobustaConfig).GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Static)!;
        dirtyField.SetValue(null, false);

        RobustaImGuiManager.CheatMenu.IsOpen = true;
        ImGuiInputBridge.OnWindowVisibilityChanged(true);

        dirtyField.GetValue(null).Should().Be(false, "opening the menu must not queue a config write");
    }
}
