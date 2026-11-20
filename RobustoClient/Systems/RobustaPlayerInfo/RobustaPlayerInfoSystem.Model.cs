using Robust.Shared.GameObjects;

namespace RobustoClient.Systems;

/// <summary>Which block a section is, the window owns its title and color.</summary>
public enum InfoSectionKind
{
    General,
    Syndicate,
    Chemical,
    Security,
    Command,
    Flags
}

/// <summary>One line of a section, <c>Children</c> makes it an expandable row.</summary>
public sealed record InfoRow(string Label, string Value, IReadOnlyList<InfoRow>? Children = null);

public sealed record InfoSection(InfoSectionKind Kind, IReadOnlyList<InfoRow> Rows);

/// <summary>Everything the panel shows for one entity. This is the window's only input.</summary>
public sealed record PlayerInfoReport(
    EntityUid Player,
    string Title,
    IReadOnlyList<InfoSection> Sections);

/// <summary>A rendered report plus whether it came from retained data.</summary>
public sealed record PlayerInfoSnapshot(PlayerInfoReport Report, bool Stale);
