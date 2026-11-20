using Content.Shared.Access.Components;
using Content.Shared.PDA;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using RobustoClient.Systems;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Command block: the role, the mindshield, and the items that attributed the role.</summary>
public sealed partial class PlayerStateUtility
{
    /// <summary>
    /// The command roles. The job prototype and the job icon both resolve to these keys, and the label
    /// is what the Info panel and the radar show.
    /// </summary>
    private static readonly CommandRole[] CommandRoles =
    [
        new("CAP", "Captain", "Captain", "JobIconCaptain"),
        new("HOS", "Head of Security", "HeadOfSecurity", "JobIconHeadOfSecurity"),
        new("RD", "Research Director", "ResearchDirector", "JobIconResearchDirector"),
        new("CMO", "Chief Medical Officer", "ChiefMedicalOfficer", "JobIconChiefMedicalOfficer"),
        new("QM", "Quartermaster", "Quartermaster", "JobIconQuarterMaster"),
        new("HOP", "Head of Personnel", "HeadOfPersonnel", "JobIconHeadOfPersonnel"),
        new("CE", "Chief Engineer", "ChiefEngineer", "JobIconChiefEngineer")
    ];

    /// <summary>Role attribution for command-flavoured items, matched by prototype id fragment.</summary>
    private static readonly (string Role, string[] Markers)[] _commandItemMarkers =
    [
        ("CAPTAIN", ["captainidcard", "clothingneckcloakcap", "clothingouterhardsuitcap",
                     "captainsword", "jetpackcaptainfilled", "nukedisk", "weaponantiquelaser",
                     "boxfoldernuclearcodes", "rubberstampcaptain"]),
        ("HOP",     ["hopidcard", "clothingneckcloakhop", "rubberstamphop"]),
        ("RD",      ["rdidcard", "clothingneckcloakrd", "clothingouterhardsuitrd", "rubberstamprd"]),
        ("CMO",     ["cmoidcard", "clothingneckcloakcmo", "clothingouterhardsuitmedical", "rubberstampcmo"]),
        ("CE",      ["ceidcard", "clothingneckcloakce", "clothingouterhardsuitengineeringwhite",
                     "rubberstampce"]),
        ("HOS",     ["hosidcard", "clothingneckcloakhos", "clothingouterhardsuitsecurityred",
                     "rubberstamphos"]),
        ("QM",      ["quartermasteridcard", "clothingneckcloakqm", "boxfolderqmclipboard",
                     "clothinghandsknuckledustersqm", "rubberstampqm"]),
        ("COMMAND", ["headcloak", "headhardsuit", "doorremote", "encryptionkeycommand",
                     "encryptionkeystationmaster"])
    ];

    /// <summary>One command job: the key, its display label, and the two ids that resolve to it.</summary>
    private sealed record CommandRole(string Key, string Label, string JobPrototype, string JobIcon);

    /// <summary>
    /// Command role key for the entity's job, or null when the job is not a head.
    /// </summary>
    /// <remarks>Tries the job prototype, then the job icon, then the localized title.</remarks>
    public string? GetCommandRole(EntityUid player)
    {
        if (_commandRoleCache.TryGetValue(player, out var cached) &&
            _timing.CurTime - cached.Stamp < CacheLifetime)
        {
            return cached.Role;
        }

        var role = ResolveCommandRoleFromIdCard(player);
        _commandRoleCache[player] = (role, _timing.CurTime);
        return role;
    }

    private string? ResolveCommandRoleFromIdCard(EntityUid player)
    {
        if (!_inventory.TryGetSlotEntity(player, "id", out var slot) || slot is not { } slotUid)
            return null;

        var card = slotUid;

        if (EntityManager.TryGetComponent<PdaComponent>(card, out var pda) && pda.ContainedId is { } contained)
            card = contained;

        if (!EntityManager.TryGetComponent<IdCardComponent>(card, out var idCard))
            return null;

        if (idCard.JobPrototype is { } job)
        {
            foreach (var role in CommandRoles)
            {
                if (string.Equals(job.Id, role.JobPrototype, StringComparison.OrdinalIgnoreCase))
                    return role.Key;
            }
        }

        if (CommandRoleForIcon(idCard.JobIcon.Id) is { } byIcon)
            return byIcon;

        // Only the seven head jobs are indexed, so this stays seven prototype lookups.
        foreach (var role in CommandRoles)
        {
            if (_prototypes.TryIndex<JobPrototype>(role.JobPrototype, out var prototype) &&
                string.Equals(prototype.LocalizedName, idCard.LocalizedJobTitle, StringComparison.OrdinalIgnoreCase))
            {
                return role.Key;
            }
        }

        return null;
    }

    /// <summary>Role key for a job icon id, or null when the icon names no head job.</summary>
    internal static string? CommandRoleForIcon(string iconId)
    {
        foreach (var role in CommandRoles)
        {
            if (string.Equals(role.JobIcon, iconId, StringComparison.OrdinalIgnoreCase))
                return role.Key;
        }

        return null;
    }

    /// <summary>Role a command prototype belongs to, or null when it is not one.</summary>
    internal static string? ResolveCommandRole(string prototypeId)
    {
        var id = prototypeId.ToLowerInvariant();

        foreach (var (role, markers) in _commandItemMarkers)
        {
            foreach (var marker in markers)
            {
                if (id.Contains(marker))
                    return role;
            }
        }

        return null;
    }

    /// <summary>Label for a short role key, or the key itself when it names no declared role.</summary>
    internal static string CommandRoleLabel(string role)
    {
        foreach (var declared in CommandRoles)
        {
            if (string.Equals(declared.Key, role, StringComparison.OrdinalIgnoreCase))
                return declared.Label;
        }

        return role;
    }

    private IPlayerData? BuildCommand(
        EntityUid entity,
        PlayerFlagReport flags,
        Func<IReadOnlyList<InventoryEntry>> inventory)
    {
        var role = GetCommandRole(entity);

        if (!flags.Has(PlayerFlag.IsCommand) && role == null)
            return null;

        var key = role ?? "COMMAND";

        return new CommandData(
            key,
            CommandRoleLabel(key),
            EntityManager.System<RobustaSyndicateDetectorSystem>().IsMindshielded(entity),
            ScanCommandItems(inventory()));
    }

    private IReadOnlyList<CommandItemEntry> ScanCommandItems(IReadOnlyList<InventoryEntry> inventory)
    {
        var results = new List<CommandItemEntry>();

        foreach (var entry in inventory)
        {
            if (ResolveCommandRole(entry.PrototypeId) is not { } role)
                continue;

            results.Add(new CommandItemEntry(role, entry.ItemName));
        }

        return results;
    }
}
