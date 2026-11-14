using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using RobustoClient.ImGui;

namespace RobustoClient.UI.ImGui;

public sealed partial class CheatMenuImGui
{
    // Collapse state is keyed by card id because the tab lambdas rebuild their WidgetCard list every
    // frame; the card objects are not stable, their ids are.
    private readonly Dictionary<string, bool> _collapsedCards = new();

    private bool _headerCollapsed;

    // Size to restore when the header expands again. Zero until first collapse.
    private Vector2 _expandedWindowSize;

    /// <summary>True when the whole content area is folded away.</summary>
    internal bool IsHeaderCollapsed => _headerCollapsed;

    /// <summary>Currently selected tab, exposed for tests.</summary>
    internal MenuTab ActiveTab => _activeTab;

    internal bool IsCardCollapsed(string id) => _collapsedCards.TryGetValue(id, out bool collapsed) && collapsed;

    internal void SetCardCollapsed(string id, bool collapsed) => _collapsedCards[id] = collapsed;

    internal void ToggleCardCollapsed(string id) => SetCardCollapsed(id, !IsCardCollapsed(id));

    /// <summary>
    /// Selects a tab, expanding the header if it was collapsed so the tab's content is visible.
    /// </summary>
    internal void SelectTab(MenuTab tab)
    {
        _activeTab = tab;
        _headerCollapsed = false;
    }

    /// <summary>
    /// Folds the content area away. Any keybind capture in progress is cancelled, since the Binds
    /// tab is no longer on screen and would otherwise keep swallowing keys.
    /// </summary>
    internal void CollapseHeader()
    {
        if (_headerCollapsed)
            return;

        // _windowSize is refreshed at window scope every frame, so this needs no ImGui call and the
        // method stays pure state (callable from tests without a context).
        if (_windowSize.X > 0f && _windowSize.Y > 0f)
            _expandedWindowSize = _windowSize;

        _headerCollapsed = true;
        StopListening();
    }

    /// <summary>Restores the content area and the window size it had before collapsing.</summary>
    internal void ExpandHeader() => _headerCollapsed = false;
}
