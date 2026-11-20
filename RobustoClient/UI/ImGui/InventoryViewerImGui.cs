using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.ImGui;
using RobustoClient.Systems;

namespace RobustoClient.UI.ImGui;

public sealed class InventoryViewerImGui
{
    /// <summary>Window title plus the layout store's id.</summary>
    private const string WindowTitle = "Target Inventory Viewer##" + WindowLayoutId.InventoryViewer;

    public bool IsOpen;

    private EntityUid _currentTarget;
    private string _targetName = "No Target";
    private List<InventoryEntry> _allEntries = new();
    private string _searchQuery = "";
    private int _selectedSortMode; // 0: By Container, 1: By Severity, 2: By Name
    private int _selectedTierFilter; // 0: All, 1: Contraband Only, 2: Syndicate, 3: GrandTheft, 4: Major, 5: Restricted, 6: Minor

    private static readonly string[] SortModes =
    [
        "Sort: By Container",
        "Sort: By Severity Tier",
        "Sort: By Name (A-Z)"
    ];

    private static readonly string[] TierFilters =
    [
        "Filter: All Items",
        "Filter: Any Contraband",
        "Filter: Syndicate",
        "Filter: Grand Theft",
        "Filter: Major",
        "Filter: Restricted",
        "Filter: Minor"
    ];

    public void SetData(EntityUid target, string targetName, List<InventoryEntry> entries)
    {
        _currentTarget = target;
        _targetName = targetName;
        _allEntries = entries;
        IsOpen = true;
    }

    public void Draw()
    {
        if (WindowLayout.Get(WindowLayoutId.InventoryViewer) is { } placement)
        {
            SetNextWindowPos(WindowLayout.ClampPosition(placement, GetIO().DisplaySize), ImGuiCond.FirstUseEver);
            SetNextWindowSize(new Vector2(placement.Width, placement.Height), ImGuiCond.FirstUseEver);
        }
        else
        {
            SetNextWindowSize(new Vector2(560f, 520f), ImGuiCond.FirstUseEver);
        }

        SetNextWindowSizeConstraints(new Vector2(440f, 320f), new Vector2(1200f, 1000f));

        if (!Begin(WindowTitle, ref IsOpen, ImGuiWindowFlags.None))
        {
            End();
            return;
        }

        WindowLayout.Remember(WindowLayoutId.InventoryViewer, GetWindowPos(), GetWindowSize());

        if (IsMouseReleased(ImGuiMouseButton.Left))
            WindowLayout.FlushIfPending();

        // Filtered items
        var filtered = _allEntries.AsEnumerable();
        filtered = _selectedTierFilter switch
        {
            1 => filtered.Where(e => !string.IsNullOrEmpty(e.ContrabandSeverity)),
            2 => filtered.Where(e => e.ContrabandSeverity.Equals("Syndicate", StringComparison.OrdinalIgnoreCase)),
            3 => filtered.Where(e => e.ContrabandSeverity.Equals("GrandTheft", StringComparison.OrdinalIgnoreCase)),
            4 => filtered.Where(e => e.ContrabandSeverity.Equals("Major", StringComparison.OrdinalIgnoreCase)),
            5 => filtered.Where(e => e.ContrabandSeverity.Equals("Restricted", StringComparison.OrdinalIgnoreCase)),
            6 => filtered.Where(e => e.ContrabandSeverity.Equals("Minor", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            filtered = filtered.Where(e =>
                e.ItemName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                e.ContainerName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                e.PrototypeId.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                e.ContrabandSeverity.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = _selectedSortMode switch
        {
            1 => filtered.OrderByDescending(i => GetSeverityWeight(i.ContrabandSeverity)).ThenBy(i => i.ItemName).ToList(),
            2 => filtered.OrderBy(i => i.ItemName).ToList(),
            _ => filtered.OrderBy(i => i.ContainerName).ThenByDescending(i => GetSeverityWeight(i.ContrabandSeverity)).ThenBy(i => i.ItemName).ToList()
        };

        // Row 1: header and Refresh/Scan buttons.
        float rightEdge = GetCursorPosX() + GetContentRegionAvail().X;
        AlignTextToFramePadding();
        TextColored(RobustaImGuiTheme.AccentCyan, $"Target: {_targetName}");
        SameLine();
        string countText = _allEntries.Count == filteredList.Count
            ? $"({_allEntries.Count} items)"
            : $"({filteredList.Count} / {_allEntries.Count} items)";
        RobustaImGuiWidgets.MutedText(countText);

        SameLine(rightEdge - 176f);
        if (RobustaImGuiWidgets.AccentButton("Scan Target", new Vector2(96f, 0f)))
        {
            if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
                entMan.System<RobustaInventoryViewerSystem>().OpenForHoverOrTarget();
        }
        SameLine();
        if (RobustaImGuiWidgets.AccentButton("Refresh", new Vector2(72f, 0f)))
        {
            RefreshCurrentTarget();
        }

        // Row 2: search bar.
        SetNextItemWidth(-1f);
        InputTextWithHint("##InvSearch", "Search item, container, prototype, or contraband tier...", ref _searchQuery, 128);

        // Row 3: sort and filter combos.
        float halfW = (GetContentRegionAvail().X - 8f) * 0.5f;
        SetNextItemWidth(halfW);
        Combo("##InvSort", ref _selectedSortMode, SortModes, SortModes.Length);
        SameLine();
        SetNextItemWidth(halfW);
        Combo("##InvFilter", ref _selectedTierFilter, TierFilters, TierFilters.Length);

        Spacing();

        if (filteredList.Count == 0)
        {
            RobustaImGuiWidgets.MutedText(
                _allEntries.Count == 0
                    ? "No items found (or server did not replicate contents)."
                    : "No items match the current filter.");
            End();
            return;
        }

        // Row 4: item table.
        if (BeginTable(
                "##InvTable",
                4,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            TableSetupScrollFreeze(0, 1);
            TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            TableSetupColumn("Container / Slot", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            TableSetupColumn("Contraband", ImGuiTableColumnFlags.WidthFixed, 105f);
            TableSetupColumn("Prototype", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            TableHeadersRow();

            foreach (var entry in filteredList)
            {
                var itemCol = GetSeverityColor(entry.ContrabandSeverity);
                TableNextRow();

                TableNextColumn();
                TextColored(itemCol, entry.ItemName);
                if (IsItemHovered())
                {
                    SetTooltip(
                        $"Item: {entry.ItemName}\nContainer: {entry.ContainerName}\nPrototype: {entry.PrototypeId}\nUID: {entry.Uid}");
                }

                TableNextColumn();
                RobustaImGuiWidgets.MutedText(entry.ContainerName);

                TableNextColumn();
                if (!string.IsNullOrEmpty(entry.ContrabandSeverity))
                    TextColored(itemCol, $"[{FormatSeverity(entry.ContrabandSeverity)}]");
                else
                    RobustaImGuiWidgets.MutedText("—");

                TableNextColumn();
                RobustaImGuiWidgets.MutedText(entry.PrototypeId);
            }

            EndTable();
        }

        End();
    }

    private void RefreshCurrentTarget()
    {
        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
            return;

        if (_currentTarget.IsValid() && entMan.EntityExists(_currentTarget))
        {
            var sys = entMan.System<RobustaInventoryViewerSystem>();
            var entries = sys.GetFullInventory(_currentTarget);
            var name = entMan.GetComponent<MetaDataComponent>(_currentTarget).EntityName;
            SetData(_currentTarget, name, entries);
        }
        else
        {
            _targetName = "Target unavailable";
            _allEntries.Clear();
        }
    }

    private static int GetSeverityWeight(string severity) => severity.ToLowerInvariant() switch
    {
        "syndicate" => 6,
        "grandtheft" => 5,
        "major" => 4,
        "restricted" => 3,
        "minor" => 2,
        "" => 0,
        _ => 1
    };

    private static string FormatSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "grandtheft" => "GRAND THEFT",
        "syndicate" => "SYNDICATE",
        "major" => "MAJOR",
        "restricted" => "RESTRICTED",
        "minor" => "MINOR",
        _ => severity.ToUpperInvariant()
    };

    private static Vector4 GetSeverityColor(string severity)
    {
        var colors = RobustaImGuiTheme.Colors;
        return severity.ToLowerInvariant() switch
        {
            "syndicate" => colors.SeveritySyndicate,
            "grandtheft" => colors.SeverityGrandTheft,
            "major" => colors.SeverityMajor,
            "restricted" => colors.SeverityRestricted,
            "minor" => colors.SeverityMinor,
            "" => colors.TextPrimary,
            _ => colors.SeverityUnknown
        };
    }
}
