namespace RobustoClient.Systems.PlayerState;

/// <summary>Comparison operator for <see cref="ComponentFieldCheck"/>.</summary>
public enum FlagComparison
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Contains,
    IsNull,
    IsNotNull
}

/// <summary>A single predicate over one entity.</summary>
public abstract record PlayerFlagCheck;

/// <summary>Runs the inner check over the player's hands and inventory slots.</summary>
public sealed record EquippedCheck(PlayerFlagCheck Inner, bool IncludeHands = true) : PlayerFlagCheck;

/// <summary>Runs the inner check over everything the player carries, containers included.</summary>
public sealed record CarriedCheck(PlayerFlagCheck Inner, int MaxDepth = 5) : PlayerFlagCheck;

public sealed record HasComponentCheck(string ComponentName) : PlayerFlagCheck;

public sealed record HasAnyComponentCheck(params string[] ComponentNames) : PlayerFlagCheck;

/// <summary>Reads <paramref name="MemberPath"/> off a component and compares it.</summary>
public sealed record ComponentFieldCheck(
    string ComponentName,
    string MemberPath,
    FlagComparison Comparison,
    object? Expected = null,
    bool AllowNonPublic = false) : PlayerFlagCheck;

/// <summary>Whether the player's job belongs to <paramref name="DepartmentId"/>.</summary>
public sealed record JobDepartmentCheck(string DepartmentId) : PlayerFlagCheck;

/// <summary>
/// Whether a carried item is contraband at <paramref name="Severity"/>, unless the player's job allows it.
/// </summary>
public sealed record ContrabandSeverityCheck(string Severity, bool RespectAllowedJobs = true) : PlayerFlagCheck;

/// <summary>Whether a carried item's melee damage total reaches <paramref name="MinDamage"/>.</summary>
public sealed record MeleeDamageCheck(float MinDamage) : PlayerFlagCheck;

/// <summary>Whether <see cref="RobustaSyndicateDetectorSystem"/> reports the given signal.</summary>
public sealed record SyndicateSignalCheck(SyndicateSignal Signal) : PlayerFlagCheck;

/// <summary>Whether a carried item counts as syndicate gear, per the detector's rules.</summary>
public sealed record SyndicateGearCheck : PlayerFlagCheck;

/// <summary>
/// Whether a carried container holds a contraband reagent at one of <paramref name="Severities"/>,
/// or any contraband reagent when the list is null.
/// </summary>
public sealed record ContrabandReagentCheck(IReadOnlyList<string>? Severities = null) : PlayerFlagCheck;

/// <summary>Whether the entity is player controlled.</summary>
public sealed record MindCheck : PlayerFlagCheck;

/// <summary>A flag and the ordered checks that decide it, first check that matches wins.</summary>
/// <param name="Category">The data block the flag answers for.</param>
/// <param name="RoundRetained">
/// Latches the verdict for the round: once the checks fire, the flag keeps answering from the
/// retained result after the evidence leaves PVS or is dropped.
/// </param>
public sealed record PlayerFlagDefinition(
    PlayerFlag Flag,
    IReadOnlyList<PlayerFlagCheck> Checks,
    PlayerStateData Category,
    bool Display = false,
    bool RoundRetained = false);
