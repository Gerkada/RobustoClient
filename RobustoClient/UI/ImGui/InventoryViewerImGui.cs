using System.Numerics;
using Hexa.NET.ImGui;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using RobustoClient.ImGui;
using RobustoClient.Systems;

namespace RobustoClient.UI.ImGui;

public sealed class InventoryViewerImGui
{
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
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(560f, 520f), ImGuiCond.FirstUseEver);
        Hexa.NET.ImGui.ImGui.SetNextWindowSizeConstraints(new Vector2(440f, 320f), new Vector2(1200f, 1000f));

        if (!Hexa.NET.ImGui.ImGui.Begin("Target Inventory Viewer##InvViewerWin", ref IsOpen, ImGuiWindowFlags.NoCollapse))
        {
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

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

        // Row 1: Header + Refresh/Scan buttons
        float rightEdge = Hexa.NET.ImGui.ImGui.GetCursorPosX() + Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X;
        Hexa.NET.ImGui.ImGui.AlignTextToFramePadding();
        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.AccentCyan, $"Target: {_targetName}");
        Hexa.NET.ImGui.ImGui.SameLine();
        string countText = _allEntries.Count == filteredList.Count
            ? $"({_allEntries.Count} items)"
            : $"({filteredList.Count} / {_allEntries.Count} items)";
        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, countText);

        Hexa.NET.ImGui.ImGui.SameLine(rightEdge - 176f);
        if (RobustaImGuiWidgets.AccentButton("Scan Target", new Vector2(96f, 0f)))
        {
            if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
                entMan.System<RobustaInventoryViewerSystem>().OpenForHoverOrTarget();
        }
        Hexa.NET.ImGui.ImGui.SameLine();
        if (RobustaImGuiWidgets.AccentButton("Refresh", new Vector2(72f, 0f)))
        {
            RefreshCurrentTarget();
        }

        // Row 2: Search bar
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
        Hexa.NET.ImGui.ImGui.InputTextWithHint("##InvSearch", "Search item, container, prototype, or contraband tier...", ref _searchQuery, 128);

        // Row 3: Sort & Filter combos
        float halfW = (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 8f) * 0.5f;
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(halfW);
        Hexa.NET.ImGui.ImGui.Combo("##InvSort", ref _selectedSortMode, SortModes, SortModes.Length);
        Hexa.NET.ImGui.ImGui.SameLine();
        Hexa.NET.ImGui.ImGui.SetNextItemWidth(halfW);
        Hexa.NET.ImGui.ImGui.Combo("##InvFilter", ref _selectedTierFilter, TierFilters, TierFilters.Length);

        Hexa.NET.ImGui.ImGui.Spacing();

        if (filteredList.Count == 0)
        {
            Hexa.NET.ImGui.ImGui.TextColored(
                RobustaImGuiTheme.TextMuted,
                _allEntries.Count == 0
                    ? "No items found (or server did not replicate contents)."
                    : "No items match the current filter.");
            Hexa.NET.ImGui.ImGui.End();
            return;
        }

        // Row 4: Item Table
        if (Hexa.NET.ImGui.ImGui.BeginTable(
                "##InvTable",
                4,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            Hexa.NET.ImGui.ImGui.TableSetupScrollFreeze(0, 1);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Container / Slot", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Contraband", ImGuiTableColumnFlags.WidthFixed, 105f);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Prototype", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            Hexa.NET.ImGui.ImGui.TableHeadersRow();

            foreach (var entry in filteredList)
            {
                var itemCol = GetSeverityColor(entry.ContrabandSeverity);
                Hexa.NET.ImGui.ImGui.TableNextRow();

                Hexa.NET.ImGui.ImGui.TableNextColumn();
                Hexa.NET.ImGui.ImGui.TextColored(itemCol, entry.ItemName);
                if (Hexa.NET.ImGui.ImGui.IsItemHovered())
                {
                    Hexa.NET.ImGui.ImGui.SetTooltip(
                        $"Item: {entry.ItemName}\nContainer: {entry.ContainerName}\nPrototype: {entry.PrototypeId}\nUID: {entry.Uid}");
                }

                Hexa.NET.ImGui.ImGui.TableNextColumn();
                Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, entry.ContainerName);

                Hexa.NET.ImGui.ImGui.TableNextColumn();
                if (!string.IsNullOrEmpty(entry.ContrabandSeverity))
                    Hexa.NET.ImGui.ImGui.TextColored(itemCol, $"[{FormatSeverity(entry.ContrabandSeverity)}]");
                else
                    Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "—");

                Hexa.NET.ImGui.ImGui.TableNextColumn();
                Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, entry.PrototypeId);
            }

            Hexa.NET.ImGui.ImGui.EndTable();
        }

        Hexa.NET.ImGui.ImGui.End();
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

    private static Vector4 GetSeverityColor(string severity) => severity.ToLowerInvariant() switch
    {
        "syndicate" => RobustaImGuiTheme.Hex("#FF6B6B"),
        "grandtheft" => RobustaImGuiTheme.Hex("#FFA500"),
        "major" => RobustaImGuiTheme.Hex("#FFD700"),
        "restricted" => RobustaImGuiTheme.Hex("#7090FF"),
        "minor" => RobustaImGuiTheme.Hex("#B0B0B0"),
        "" => RobustaImGuiTheme.TextPrimary,
        _ => RobustaImGuiTheme.Hex("#E090FF")
    };
}
