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
    private DiscoveredBind? _activeListeningTarget;
    private MenuTab _activeTab = MenuTab.Combat;

    // Last measured cheat-menu size, refreshed at window scope every frame.
    private Vector2 _windowSize;

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

    public bool IsListeningForKeybind => _activeListeningTarget != null;

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

        // Sized so exactly two widget columns plus padding fit: no horizontal dead space.
        SetNextWindowSize(theme.DefaultWindowSize, ImGuiCond.FirstUseEver);

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
                "ROBUSTA##MainWindow",
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

        // Leaving the Binds tab mid-capture would strand the capture session.
        if (_activeTab != MenuTab.Binds && _activeListeningTarget != null)
            StopListening();

        // Window padding stays zero for the whole body so the header and tab strip bleed to the
        // edges; the content area applies its own padding inside a child.
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
        BeginChild("##ContentArea", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);

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

        EndChild();
        PopStyleVar();
    }
}
