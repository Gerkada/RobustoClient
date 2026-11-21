using System.Globalization;
using Robust.Shared.GameObjects;
using RobustoClient.Systems.PlayerState;

namespace RobustoClient.Systems;

/// <summary>Turns a snapshot into the sections the panel draws.</summary>
public sealed partial class RobustaPlayerInfoSystem
{
    internal const string NoRangeText = "N/A";

    /// <summary>Shown when the snapshot carries no block at all, so a blank panel is never ambiguous.</summary>
    internal const string NoStateText = "No state available";

    /// <summary>Builds the report, a section appears only when the snapshot carries its block.</summary>
    internal static PlayerInfoReport Compose(PlayerSnapshot snapshot)
    {
        var sections = new List<InfoSection>();

        // The general section reads the identity block, so it is only meaningful when that block
        // exists. Emitting it unconditionally produced a panel of "Unknown" rows that read as broken.
        if (snapshot.Identity is not null)
            sections.Add(BuildGeneral(snapshot));

        AddIfPresent(sections, BuildAntag(snapshot));
        AddIfPresent(sections, BuildChemical(snapshot));
        AddIfPresent(sections, BuildSecurity(snapshot));
        AddIfPresent(sections, BuildCommand(snapshot));
        AddIfPresent(sections, BuildFlags(snapshot));

        // A panel with no sections is indistinguishable from a broken one, so say so explicitly.
        if (sections.Count == 0)
            sections.Add(new InfoSection(InfoSectionKind.General, [new InfoRow(NoStateText, string.Empty)]));

        return new PlayerInfoReport(
            snapshot.Entity,
            snapshot.Identity?.EntityName ?? string.Empty,
            sections.Select(section => section with { Rows = Prioritise(section.Rows) }).ToList());
    }

    /// <summary>Adds a section only when its builder exists.</summary>
    private static void AddIfPresent(List<InfoSection> sections, InfoSection? section)
    {
        if (section is { } present)
            sections.Add(present);
    }

    /// <summary>Plain rows first, then expandable ones.</summary>
    private static List<InfoRow> Prioritise(IReadOnlyList<InfoRow> rows) =>
        rows.OrderBy(row => row.Children is { Count: > 0 } ? 1 : 0).ToList();

    internal static string FormatRange(float? meters) =>
        meters is { } value
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + "m"
            : NoRangeText;
}
