using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using RobustoClient.Systems;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Theft block: contraband at a theft severity that the target's own ID does not clear, minus the
/// per-job exemptions.
/// </summary>
public sealed partial class PlayerStateUtility
{
    /// <summary>
    /// Gear a job is expected to carry, so holding it is not theft. Matched on a prototype id
    /// fragment, case-insensitively.
    /// </summary>
    private static readonly (string Prototype, string[] Jobs)[] TheftExemptions =
    [
        ("HandheldCrewMonitor", ["Chief Medical Officer", "Paramedic"])
    ];

    /// <summary>Whether a job is exempt from the theft flag for this prototype.</summary>
    internal static bool IsTheftExempt(string prototypeId, string jobTitle)
    {
        if (string.IsNullOrEmpty(prototypeId) || string.IsNullOrEmpty(jobTitle))
            return false;

        foreach (var (prototype, jobs) in TheftExemptions)
        {
            if (!prototypeId.Contains(prototype, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var job in jobs)
            {
                if (string.Equals(job, jobTitle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private IPlayerData? BuildTheft(EntityUid entity, PlayerFlagReport flags, Func<IReadOnlyList<InventoryEntry>> inventory)
    {
        var entries = inventory();
        if (entries.Count == 0)
            return null;

        var jobTitle = FindIdCard(entity)?.LocalizedJobTitle ?? string.Empty;
        var results = new List<TheftEntry>();

        foreach (var entry in entries)
        {
            if (!TheftSeverities.Contains(entry.ContrabandSeverity, StringComparer.OrdinalIgnoreCase))
                continue;

            // A key or tool spawned with the job is restricted for others but not for its owner.
            if (IsAllowedToCarry(entry.Uid, entity))
                continue;

            if (IsTheftExempt(entry.PrototypeId, jobTitle))
                continue;

            results.Add(new TheftEntry(entry.ItemName, entry.ContrabandSeverity));
        }

        return results.Count == 0 ? null : new TheftData(results);
    }
}
