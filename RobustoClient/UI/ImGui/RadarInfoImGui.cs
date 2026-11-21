using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Shared.GameObjects;
using RobustoClient.ImGui;
using RobustoClient.Systems;

namespace RobustoClient.UI.ImGui;

/// <summary>
/// The radar's Info panel: draw only, with all scanning in <see cref="RobustaPlayerInfoSystem"/>.
/// </summary>
public sealed class RadarInfoImGui
{
    public bool IsOpen;

    private EntityUid _target;
    private bool _placed;
    private bool _uncollapse;

    /// <summary>Entity the panel is showing, so a repeated open request is a no-op.</summary>
    public EntityUid Target => _target;

    /// <summary>Whether the next draw still has to dock beside the radar. Read by the docking test.</summary>
    internal bool PendingDock => !_placed;

    /// <summary>Whether the next draw has to undo a collapse. Read by the docking test.</summary>
    internal bool PendingUncollapse => _uncollapse;

    /// <summary>Records that the pending dock request has been applied.</summary>
    internal void MarkDocked() => _placed = true;

    /// <summary>
    /// Points the panel at an entity and opens it.
    /// </summary>
    public void Show(EntityUid target)
    {
        if (IsOpen && target == _target)
            return;

        _target = target;

        if (IsOpen)
        {
            _uncollapse = true;
            return;
        }

        IsOpen = true;
        _placed = false;
    }

    /// <summary>
    /// Puts the panel beside the radar, flipping to its left when the radar sits on the right half.
    /// </summary>
    private void PlaceBesideRadar()
    {
        if (_placed)
            return;

        if (RobustaImGuiManager.Radar.WindowPos is not { } pos ||
            RobustaImGuiManager.Radar.WindowSize is not { } size)
        {
            // The radar has not laid out yet. try again next frame rather than giving up.
            return;
        }

        var displayWidth = GetIO().DisplaySize.X;
        var radarOnRight = pos.X + size.X * 0.5f > displayWidth * 0.5f;

        if (radarOnRight)
        {
            SetNextWindowPos(new Vector2(pos.X - 8f, pos.Y), ImGuiCond.Always, new Vector2(1f, 0f));
        }
        else
        {
            SetNextWindowPos(new Vector2(pos.X + size.X + 8f, pos.Y), ImGuiCond.Always, new Vector2(0f, 0f));
        }

        MarkDocked();
    }

    public void Draw()
    {
        // Match the radar's default height so the two read as one panel.
        SetNextWindowSize(new Vector2(420f, 290f), ImGuiCond.FirstUseEver);
        SetNextWindowSizeConstraints(new Vector2(280f, 240f), new Vector2(900f, 1200f));

        // Re-expand in place. the reader's position is kept.
        if (_uncollapse)
        {
            SetNextWindowCollapsed(false, ImGuiCond.Always);
            _uncollapse = false;
        }

        PlaceBesideRadar();

        if (!Begin("Player Info##RadarInfoWin", ref IsOpen, ImGuiWindowFlags.None))
        {
            End();
            return;
        }

        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
        {
            RobustaImGuiWidgets.MutedText("Client unavailable.");
            End();
            return;
        }

        // The system keeps the last identified report, so sections stay readable out of PVS.
        var snapshot = entMan.System<RobustaPlayerInfoSystem>().GetCachedReport(_target);

        if (snapshot is not { } info)
        {
            RobustaImGuiWidgets.MutedText("Target out of range.");
            End();
            return;
        }

        if (info.Stale)
        {
            RobustaImGuiWidgets.MutedText("Out of range: showing the last known state.");
            Spacing();
        }

        // A child owns the scrollbar so the window never grows a horizontal one.
        BeginChild("##report", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
        DrawReport(info.Report);
        EndChild();

        End();
    }

    private void DrawReport(PlayerInfoReport report)
    {
        // Scope tree state to the target so collapses do not carry over.
        var scope = _target.Id;

        foreach (var section in report.Sections)
        {
            PushStyleColor(ImGuiCol.Header, RobustaImGuiTheme.BgCard);
            PushStyleColor(ImGuiCol.HeaderHovered, RobustaImGuiTheme.BgControlHover);
            PushStyleColor(ImGuiCol.HeaderActive, RobustaImGuiTheme.BgCard);
            PushStyleColor(ImGuiCol.Text, ColorFor(section.Kind));
            // A sliver of left padding, well under ImGui's default.
            PushStyleVar(
                ImGuiStyleVar.FramePadding,
                new Vector2(GetContentRegionAvail().X * 0.02f, GetStyle().FramePadding.Y));

            SetNextItemOpen(true, ImGuiCond.FirstUseEver);
            var expanded = CollapsingHeader($"{TitleFor(section.Kind)}##info{section.Kind}:{scope}");

            PopStyleVar();
            PopStyleColor(4);

            if (!expanded)
                continue;

            foreach (var row in section.Rows)
                DrawRow(row, scope);
        }
    }

    private static void DrawRow(InfoRow row, int scope)
    {
        if (row.Children is { Count: > 0 })
        {
            // Open by default. the target-scoped id keeps a collapse from carrying over.
            SetNextItemOpen(true, ImGuiCond.FirstUseEver);

            // A container row carries no value, so the parenthesised suffix is only added when there
            // is something to put in it.
            var suffix = string.IsNullOrEmpty(row.Value) ? string.Empty : $" ({row.Value})";

            // A contraband prefix carries its own color, so the model stays free of UI types.
            var treeColor = AlertColor(row.Label);
            if (treeColor is { } treeTint)
                PushStyleColor(ImGuiCol.Text, treeTint);

            var open = TreeNode($"{row.Label}{suffix}##info{scope}:{row.Label}{row.Value}");

            if (treeColor is not null)
                PopStyleColor();

            if (open)
            {
                foreach (var child in row.Children)
                    DrawRow(child, scope);

                TreePop();
            }

            return;
        }

        LabelValue(row);
    }

    private static void LabelValue(InfoRow row)
    {
        // Match the indent a tree node puts its label at.
        float indent = GetTreeNodeToLabelSpacing();
        Indent(indent);

        // A plain row's label is muted, so an alert label has to carry its own color.
        var labelTint = AlertColor(row.Label);
        if (labelTint is { } labelColor)
            TextColored(labelColor, row.Label);
        else
            RobustaImGuiWidgets.MutedText(row.Label);

        if (!string.IsNullOrEmpty(row.Value))
        {
            SameLine();
            // Wrap at the content edge so a long value cannot widen the panel.
            PushTextWrapPos(0f);

            var tint = AlertColor(row.Value);
            if (tint is { } tintColor)
                PushStyleColor(ImGuiCol.Text, tintColor);

            TextWrapped(row.Value);

            if (tint is not null)
                PopStyleColor();

            PopTextWrapPos();
        }

        Unindent(indent);
    }

    /// <summary>
    /// color for an alert label or a contraband prefix. The text is the model's only signal, so the
    /// renderer owns the palette and the model stays free of UI types.
    /// </summary>
    private static Vector4? AlertColor(string text) => text switch
    {
        _ when text.StartsWith("[MAJOR CONTRA]", StringComparison.Ordinal) =>
            RobustaImGuiTheme.Colors.Danger,
        _ when text.StartsWith("[MINOR CONTRA]", StringComparison.Ordinal) =>
            RobustaImGuiTheme.Colors.Warning,
        _ when text.StartsWith("NO MINDSHIELD", StringComparison.Ordinal) =>
            RobustaImGuiTheme.Colors.Danger,
        _ => null
    };

    private static string TitleFor(InfoSectionKind kind) => kind switch
    {
        InfoSectionKind.General => "GENERAL",
        InfoSectionKind.Syndicate => "SYNDICATE",
        InfoSectionKind.Chemical => "CHEMICAL",
        InfoSectionKind.Security => "SECURITY",
        InfoSectionKind.Command => "COMMAND",
        InfoSectionKind.Flags => "GENERAL FLAGS",
        _ => "OTHER"
    };

    private static Vector4 ColorFor(InfoSectionKind kind)
    {
        var colors = RobustaImGuiTheme.Colors;
        return kind switch
        {
            InfoSectionKind.General => colors.TextPrimary,
            InfoSectionKind.Syndicate => colors.SeveritySyndicate,
            InfoSectionKind.Chemical => colors.Info,
            InfoSectionKind.Security => colors.Danger,
            InfoSectionKind.Command => colors.Success,
            InfoSectionKind.Flags => colors.Warning,
            _ => colors.TextMuted
        };
    }
}
