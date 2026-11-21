using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Fluids.Components;
using Content.Shared.Kitchen.Components;
using Content.Shared.Medical.Healing;
using Content.Shared.Chemistry.Components.SolutionManager;
using Robust.Shared.GameObjects;
using RobustoClient.Reflection;
using RobustoClient.Systems;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Chemistry block: reagents the target carries and the medical items among them.</summary>
public sealed partial class PlayerStateUtility
{
    /// <summary>
    /// Medical items identified by prototype id.
    /// <see cref="Content.Shared.Medical.Healing.HealingComponent"/>.
    /// </summary>
    private static readonly string[] _medicalItemMarkers =
    [
        "autoinjector",
        "medipen",
        "hypospray"
    ];

    /// <summary>Reagent parents whose contents are not worth listing.</summary>
    private static readonly string[] _excludedReagentParents =
    [
        "BaseSoda",
        "BaseJuice"
    ];

    /// <summary>
    /// Containers that belong to the target's own body rather than to what it carries. Every organ
    /// sits in the body's organ container, and the lungs and stomach hold their own gas and food
    /// solutions, which are not chemicals the target is carrying.
    /// </summary>
    private static readonly string[] _bodyContainers =
    [
        "body_organs",
        "lungs",
        "stomach"
    ];

    /// <summary>
    /// Whether an inventory entry sits inside the target's own body. A nested entry reads
    /// <c>Parent -> Container</c>, so every segment names a container. Pure so it is testable.
    /// </summary>
    internal static bool IsBodyContainer(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return false;

        foreach (var segment in containerName.Split(" -> ", StringSplitOptions.None))
        {
            var name = segment.Trim();

            foreach (var body in _bodyContainers)
            {
                if (string.Equals(name, body, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private IPlayerData? BuildChemistry(EntityUid entity, PlayerFlagReport flags, Func<IReadOnlyList<InventoryEntry>> inventory)
    {
        var entries = inventory();
        if (entries.Count == 0)
            return null;

        var contents = ScanChemicals(entries);
        var healing = ScanHealing(entries);
        var dangerous = flags.Has(PlayerFlag.HasDangerousChemicals);

        // A dangerous reagent is always reportable, even when the rows below find nothing to name:
        // it is what raises the radar's hedged tag, so the panel must be able to say why.
        if (contents.Count == 0 && healing.Count == 0 && !dangerous)
            return null;

        return new ChemistryData(contents, healing, dangerous);
    }

    /// <summary>Reagents in something that actually holds a solution.</summary>
    private List<ChemicalEntry> ScanChemicals(IReadOnlyList<InventoryEntry> inventory)
    {
        var results = new List<ChemicalEntry>();

        foreach (var entry in inventory)
        {
            // An organ inside the body is not a container the target is carrying.
            if (IsBodyContainer(entry.ContainerName))
                continue;

            // A medipen belongs under Healing, not in the chemical contents.
            if (IsMedicalItem(entry.Uid))
                continue;

            if (!TryComp<SolutionComponent>(entry.Uid, out var solution))
                continue;

            var vessel = IsSolutionContainer(entry.Uid);

            // A non-vessel only reports reagents worth knowing about.
            if (!vessel && !HasContrabandReagent(solution))
                continue;

            foreach (var reagent in solution.Solution.Contents)
            {
                if (!_prototypes.TryIndex(reagent.Reagent.Prototype, out ReagentPrototype? prototype))
                    continue;

                // A soda or juice is not worth a row.
                if (IsExcludedReagent(prototype))
                    continue;

                // Quantity is a FixedPoint2, which lives in a different assembly on Goob, so it is
                // read reflectively rather than as a typed member.
                if (!Reflect.TryGet(reagent, "Quantity", out var quantity, ReflectOptions.Any) ||
                    !ReflectConvert.TryToDouble(quantity, out var amount))
                {
                    continue;
                }

                results.Add(new ChemicalEntry(
                    entry.ItemName,
                    prototype.LocalizedName,
                    (float)amount,
                    prototype.ContrabandSeverity is { } proto ? proto.Id : string.Empty));
            }
        }

        return results;
    }

    /// <summary>Whether the reagent is one the panel deliberately leaves out.</summary>
    private static bool IsExcludedReagent(ReagentPrototype prototype)
    {
        if (prototype.Parents is not { } parents)
            return false;

        foreach (var parent in parents)
        {
            foreach (var excluded in _excludedReagentParents)
            {
                if (string.Equals(parent, excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool HasContrabandReagent(SolutionComponent solution)
    {
        foreach (var reagent in solution.Solution.Contents)
        {
            if (_prototypes.TryIndex(reagent.Reagent.Prototype, out ReagentPrototype? prototype) &&
                prototype.ContrabandSeverity is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A vessel the target can pour, draw from or inject.</summary>
    private bool IsSolutionContainer(EntityUid uid)
    {
        if (HasComp<DrawableSolutionComponent>(uid) ||
            HasComp<InjectableSolutionComponent>(uid) ||
            HasComp<SpillableComponent>(uid) ||
            HasComp<SolutionManagerComponent>(uid))
        {
            return true;
        }

        // A grinder input holds reagents for extraction, not for drinking.
        return !HasComp<ExtractableComponent>(uid) && HasComp<ExaminableSolutionComponent>(uid);
    }

    /// <summary>Healing items and auto-injectors, identified by component or prototype id.</summary>
    private bool IsMedicalItem(EntityUid uid)
    {
        if (HasComp<HealingComponent>(uid))
            return true;

        var protoId = EntityManager.GetComponentOrNull<MetaDataComponent>(uid)?.EntityPrototype?.ID;
        if (string.IsNullOrEmpty(protoId))
            return false;

        var id = protoId.ToLowerInvariant();
        foreach (var marker in _medicalItemMarkers)
        {
            if (id.Contains(marker))
                return true;
        }

        return false;
    }

    private List<HealingEntry> ScanHealing(IReadOnlyList<InventoryEntry> inventory) =>
        inventory
            .Where(e => IsMedicalItem(e.Uid))
            .GroupBy(e => e.ItemName)
            .Select(g => new HealingEntry(g.Key, g.Count()))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
