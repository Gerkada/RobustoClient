using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

/// <summary>
/// The main cheat menu window: state, lifecycle, save policy, and the window shell.
/// Tab bodies live in <c>CheatMenuImGui.Tabs.cs</c>, the sidebar in <c>CheatMenuImGui.Sidebar.cs</c>,
/// and keybind editing in <c>CheatMenuImGui.Binds.cs</c> / <c>CheatMenuImGui.KeyCapture.cs</c>.
/// </summary>
public sealed partial class CheatMenuImGui
{
    internal enum MenuTab
    {
        Combat,
        Visuals,
        Utility,
        Binds,
        Themes
    }

    private sealed class DiscoveredBind(FieldInfo field, KeybindConfigAttribute attr)
    {
        public FieldInfo Field { get; } = field;
        public KeybindConfigAttribute Attribute { get; } = attr;
        public string EditBuffer = "";

        /// <summary>Config value <see cref="EditBuffer"/> was last filled from, so an idle row only
        /// rewrites the box when the live bind actually changed.</summary>
        public string Synced = "";
    }

    private readonly List<DiscoveredBind> _discoveredBinds = new();

    /// <summary>Receives the captured combination, null when no capture session is running.</summary>
    private Action<RobustaKeybind>? _captureSink;

    /// <summary>Row that started the capture, so only that row shows the listening state.</summary>
    private object? _captureOwner;

    private MenuTab _activeTab = MenuTab.Combat;

    /// <summary>Window title plus the layout store's id.</summary>
    private const string WindowTitle = "ROBUSTA##" + WindowLayoutId.Main;

    // Last measured cheat-menu size and position, refreshed at window scope every frame.
    private Vector2 _windowSize;
    private Vector2 _windowPos;

    private bool _isOpen;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;

            _isOpen = value;
            if (!_isOpen)
            {
                StopListening();

                // Closing the menu is the save boundary. Nothing is written while it stays open.
                RobustaConfig.SaveIfDirty();
            }
        }
    }

    public bool IsListeningForKeybind => _captureSink != null;

    /// <summary>Starts a capture session for <paramref name="owner"/>, the sink receives the result.</summary>
    internal void BeginCapture(object owner, Action<RobustaKeybind> sink)
    {
        _captureOwner = owner;
        _captureSink = sink;
    }

    /// <summary>Whether <paramref name="owner"/> is the row currently capturing a key.</summary>
    internal bool IsCapturing(object owner) => ReferenceEquals(_captureOwner, owner);

    public void Toggle() => IsOpen = !IsOpen;
    public void Initialize() => DiscoverKeybinds();

    /// <summary>
    /// Records that a config value changed. Persistence is deferred to a lifecycle boundary
    /// (see <see cref="IsOpen"/>) so dragging a slider does not write on every frame. Keybinds are the
    /// exception: one bind is one deliberate action, so <c>ApplyBind</c> saves it directly.
    /// </summary>
    private static void MarkConfigDirty() => RobustaConfig.MarkDirty();

    public void Draw()
    {
        var theme = RobustaImGuiTheme.Current;

        // A remembered placement replaces the first-run default. FirstUseEver applies it once per
        // launch, so the window still drags freely for the rest of the session.
        if (WindowLayout.Get(WindowLayoutId.Main) is { } placement)
        {
            SetNextWindowPos(WindowLayout.ClampPosition(placement, GetIO().DisplaySize), ImGuiCond.FirstUseEver);
            SetNextWindowSize(new Vector2(placement.Width, placement.Height), ImGuiCond.FirstUseEver);
        }
        else
        {
            // Sized so exactly two widget columns plus padding fit: no horizontal dead space.
            SetNextWindowSize(theme.DefaultWindowSize, ImGuiCond.FirstUseEver);
        }

        if (_headerCollapsed)
        {
            // The window has to be allowed to shrink to the header band, otherwise the minimum height
            // would leave a large empty box under a collapsed header.
            float band = HeaderBandHeight(out _);

            // GetWindowSize() outside Begin() reports the fallback window, not this one, so it widened
            // the window on collapse. The tracked size is this window's own width from the last frame.
            float width = _windowSize.X > 0f ? _windowSize.X : theme.DefaultWindowSize.X;
            SetNextWindowSizeConstraints(new Vector2(theme.MinWindowSize.X, band), new Vector2(1400f, 1000f));
            SetNextWindowSize(new Vector2(width, band), ImGuiCond.Always);
        }
        else
        {
            SetNextWindowSizeConstraints(theme.MinWindowSize, new Vector2(1400f, 1000f));

            // One-shot restore after expanding, so the pre-collapse size comes back.
            if (_expandedWindowSize != Vector2.Zero)
            {
                SetNextWindowSize(_expandedWindowSize, ImGuiCond.Always);
                _expandedWindowSize = Vector2.Zero;
            }
        }

        PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        bool open = _isOpen;
        if (!Begin(
                WindowTitle,
                ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            End();
            PopStyleVar();
            IsOpen = open;
            return;
        }

        IsOpen = open;

        // Captured here, at window scope: GetWindowSize() called from inside a child returns the
        // child's size, which is not what the Themes tab wants to report.
        _windowSize = GetWindowSize();
        _windowPos = GetWindowPos();

        // A collapsed menu is a header band, not the window's real size, remembering it would reopen
        // the menu one row tall.
        if (!_headerCollapsed)
            WindowLayout.Remember(WindowLayoutId.Main, _windowPos, _windowSize);

        if (IsMouseReleased(ImGuiMouseButton.Left))
            WindowLayout.FlushIfPending();

        // Leaving the Binds tab mid-capture would strand the capture session.
        if (_activeTab != MenuTab.Binds && IsListeningForKeybind)
            StopListening();

        // Window padding stays zero for the whole body so the header and tab strip bleed to the
        // edges, the content area applies its own padding inside a child.
        DrawHeader();

        if (!_headerCollapsed)
            DrawContentArea();

        End();
        PopStyleVar();
    }

    private void DrawContentArea()
    {
        PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Vector2(RobustaImGuiScale.WindowPadding, RobustaImGuiScale.WindowPadding));

        // Wheel scrolling is handled below rather than by ImGui: every card is an AutoResizeY child
        // that can never scroll, so a notch over one would otherwise go nowhere.
        BeginChild("##ContentArea", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollWithMouse);

        switch (_activeTab)
        {
            case MenuTab.Combat:
                DrawCombatTab();
                break;
            case MenuTab.Visuals:
                DrawVisualsTab();
                break;
            case MenuTab.Utility:
                DrawUtilityTab();
                break;
            case MenuTab.Binds:
                DrawBindsTab();
                break;
            case MenuTab.Themes:
                DrawThemesTab();
                break;
        }

        // Anywhere over this child or its cards, so the wheel works over the whole content area.
        // Skipped while a popup is open: the combo list is drawn later in the frame and handles its
        // own wheel, so scrolling here as well would move the menu and the list together.
        if (!IsPopupOpenThisFrame() &&
            IsWindowHovered(ImGuiHoveredFlags.ChildWindows) &&
            GetIO().MouseWheel != 0f)
        {
            SetScrollY(GetScrollY() - GetIO().MouseWheel * GetTextLineHeightWithSpacing() * 3f);
        }

        EndChild();
        PopStyleVar();
    }

    /// <summary>
    /// Whether a popup is open, which is the signal that the popup owns the wheel this frame. Read
    /// from ImGui's own popup stack rather than tracked by hand, so it stays correct for combos opened
    /// from any window and closes the moment the popup does.
    /// </summary>
    private static unsafe bool IsPopupOpenThisFrame() =>
        GetCurrentContext().OpenPopupStack.Size > 0;
}
