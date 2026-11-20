using Content.Shared.Access.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Contraband;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mind.Components;
using Content.Shared.PDA;
using Content.Shared.Roles;
using Content.Shared.Weapons.Melee;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using RobustoClient.Reflection;

namespace RobustoClient.Systems.PlayerState;

public sealed partial class PlayerStateUtility
{
    /// <summary>Services and identity a check needs while evaluating.</summary>
    private sealed record FlagContext(
        EntityUid Player,
        IEntityManager EntMan,
        InventorySystem Inventory,
        SharedHandsSystem Hands,
        IPrototypeManager Prototypes)
    {
        public RobustaSyndicateDetectorSystem Syndicate =>
            EntMan.System<RobustaSyndicateDetectorSystem>();

        /// <summary>Container tree flattened once per report and shared by every <see cref="CarriedCheck"/>.</summary>
        public List<EntityUid>? CarriedBuffer { get; set; }
    }

    /// <summary>Depth <see cref="CarriedCheck"/> defaults to.</summary>
    private const int DefaultCarriedDepth = 5;

    /// <summary>
    /// Evaluates one check against one entity, appending evidence when it matches.
    /// </summary>
    private static bool Matches(
        EntityUid uid,
        PlayerFlagCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        switch (check)
        {
            case EquippedCheck equipped:
                return MatchesEquipped(uid, equipped, ctx, evidence);

            case CarriedCheck carried:
                return MatchesCarried(uid, carried, ctx, evidence);

            case HasComponentCheck component:
                return MatchesComponent(uid, component.ComponentName, ctx);

            case HasAnyComponentCheck anyComponent:
                return MatchesAnyComponent(uid, anyComponent, ctx, evidence);

            case ComponentFieldCheck field:
                return MatchesField(uid, field, ctx, evidence);

            case JobDepartmentCheck department:
                return MatchesDepartment(uid, department.DepartmentId, ctx, evidence);

            case ContrabandSeverityCheck severity:
                return MatchesContraband(uid, severity, ctx, evidence);

            case MeleeDamageCheck melee:
                return MatchesMelee(uid, melee.MinDamage, ctx, evidence);

            case SyndicateSignalCheck signal:
                return MatchesSyndicateSignal(uid, signal.Signal, ctx, evidence);

            case SyndicateGearCheck:
                if (!ctx.Syndicate.IsSyndicateGear(uid))
                    return false;

                evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Inventory, "Syndicate gear", uid, Describe(uid, ctx)));
                return true;

            case ContrabandReagentCheck reagent:
                return MatchesContrabandReagent(uid, reagent, ctx, evidence);

            case EmaggedCheck:
                return MatchesEmagged(uid, ctx, evidence);

            case WantedCheck:
                return MatchesWanted(uid, ctx, evidence);

            case AntagonistCheck:
                return MatchesAntagonist(uid, ctx, evidence);

            case MindCheck:
                // Read Mind rather than HasMind: Goob defines HasMind as a property, so a field load
                // against it throws MissingFieldException at runtime.
                if (!ctx.EntMan.TryGetComponent<MindContainerComponent>(uid, out var mind) || mind.Mind == null)
                    return false;

                evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Mind, "Mind attached", uid));
                return true;

            default:
                return false;
        }
    }

    private static bool MatchesEquipped(
        EntityUid uid,
        EquippedCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        // Every matching item is reported, not just the first, so a consumer can list the collection.
        var matched = false;

        foreach (var item in EnumerateEquipped(uid, check.IncludeHands, ctx))
            matched |= Matches(item, check.Inner, ctx, evidence);

        return matched;
    }

    private static bool MatchesCarried(
        EntityUid uid,
        CarriedCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        // A deeper check falls back to its own walk.
        if (check.MaxDepth > DefaultCarriedDepth)
            return WalkCarried(uid, check.Inner, ctx, evidence, new HashSet<EntityUid>(), 0, check.MaxDepth);

        var carried = ctx.CarriedBuffer ??= CollectCarried(uid, ctx, DefaultCarriedDepth);
        var matched = false;

        foreach (var item in carried)
            matched |= Matches(item, check.Inner, ctx, evidence);

        return matched;
    }

    /// <summary>Flattens the container tree once per report.</summary>
    private static List<EntityUid> CollectCarried(EntityUid uid, FlagContext ctx, int maxDepth)
    {
        var collected = new List<EntityUid>();
        var seen = new HashSet<EntityUid>();
        Collect(uid, 0);
        return collected;

        void Collect(EntityUid current, int depth)
        {
            if (depth > maxDepth || !seen.Add(current))
                return;

            collected.Add(current);

            if (!ctx.EntMan.TryGetComponent<ContainerManagerComponent>(current, out var containers))
                return;

            foreach (var (containerId, container) in containers.Containers)
            {
                // Action buttons are UI entities, not possessions.
                if (containerId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var contained in container.ContainedEntities)
                    Collect(contained, depth + 1);
            }
        }
    }

    private static bool WalkCarried(
        EntityUid uid,
        PlayerFlagCheck inner,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence,
        HashSet<EntityUid> seen,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth || !seen.Add(uid))
            return false;

        if (Matches(uid, inner, ctx, evidence))
            return true;

        if (!ctx.EntMan.TryGetComponent<ContainerManagerComponent>(uid, out var containers))
            return false;

        foreach (var (containerId, container) in containers.Containers)
        {
            // Action buttons are UI entities, not possessions.
            if (containerId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var contained in container.ContainedEntities)
            {
                if (WalkCarried(contained, inner, ctx, evidence, seen, depth + 1, maxDepth))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<EntityUid> EnumerateEquipped(
        EntityUid uid,
        bool includeHands,
        FlagContext ctx)
    {
        if (includeHands && ctx.EntMan.TryGetComponent<HandsComponent>(uid, out var hands))
        {
            foreach (var held in ctx.Hands.EnumerateHeld((uid, hands)))
                yield return held;
        }

        if (!ctx.EntMan.TryGetComponent<InventoryComponent>(uid, out var inventory))
            yield break;

        var slots = new InventorySystem.InventorySlotEnumerator(inventory);
        while (slots.NextItem(out var item))
            yield return item;
    }

    private static bool MatchesComponent(EntityUid uid, string componentName, FlagContext ctx)
    {
        if (!ctx.EntMan.ComponentFactory.TryGetRegistration(componentName, out var registration))
            return false;

        return ctx.EntMan.HasComponent(uid, registration.Type);
    }

    private static bool MatchesAnyComponent(
        EntityUid uid,
        HasAnyComponentCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        foreach (var name in check.ComponentNames)
        {
            if (!MatchesComponent(uid, name, ctx))
                continue;

            evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Component, name, uid));
            return true;
        }

        return false;
    }

    private static string Describe(EntityUid uid, FlagContext ctx) =>
        ctx.EntMan.GetComponentOrNull<MetaDataComponent>(uid)?.EntityName ?? uid.ToString();

    private static ReflectOptions OptionsFor(ComponentFieldCheck check) =>
        check.AllowNonPublic ? ReflectOptions.Any : ReflectOptions.Default;

    /// <summary>Reads a component member and compares it.</summary>
    private static bool MatchesField(
        EntityUid uid,
        ComponentFieldCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        if (!ctx.EntMan.ComponentFactory.TryGetRegistration(check.ComponentName, out var registration))
            return false;

        if (!ctx.EntMan.TryGetComponent(uid, registration.Type, out var component))
            return false;

        if (!Reflect.TryGet(component, check.MemberPath, out var value, OptionsFor(check)))
            return false;

        if (!FlagValueComparer.Matches(value, check.Comparison, check.Expected))
            return false;

        evidence.Add(PlayerFlagTrigger.For(
            FlagTriggerSource.Component,
            $"{check.ComponentName}.{check.MemberPath}",
            uid,
            value?.ToString()));

        return true;
    }

    /// <summary>
    /// Whether the ID card's department list, or the job prototype, names the department.
    /// </summary>
    private static bool MatchesDepartment(
        EntityUid uid,
        string departmentId,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        if (!TryGetIdCard(uid, ctx, out var card, out var idCard) || card is not { } cardUid)
            return false;

        foreach (var department in idCard.JobDepartments)
        {
            if (!string.Equals(department.Id, departmentId, StringComparison.OrdinalIgnoreCase))
                continue;

            evidence.Add(PlayerFlagTrigger.For(
                FlagTriggerSource.Identity,
                "JobDepartment",
                cardUid,
                departmentId));
            return true;
        }

        if (idCard.JobPrototype is not { } jobId || !ctx.Prototypes.TryIndex(jobId, out var job))
            return false;

        foreach (var department in ctx.Prototypes.EnumeratePrototypes<DepartmentPrototype>())
        {
            if (!string.Equals(department.ID, departmentId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!department.Roles.Contains(jobId))
                continue;

            evidence.Add(PlayerFlagTrigger.For(
                FlagTriggerSource.Identity,
                "JobPrototype",
                card,
                $"{job.LocalizedName} is in {department.ID}"));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the ID card where upstream looks for it: the held item, the entity itself, then the
    /// ID slot.
    /// </summary>
    private static bool TryGetIdCard(
        EntityUid uid,
        FlagContext ctx,
        out EntityUid? card,
        out IdCardComponent idCard)
    {
        if (ctx.Hands.GetActiveItem(uid) is { } held && TryReadCard(held, ctx, out card, out idCard))
            return true;

        if (TryReadCard(uid, ctx, out card, out idCard))
            return true;

        if (ctx.Inventory.TryGetSlotEntity(uid, "id", out var slot) && slot is { } slotUid &&
            TryReadCard(slotUid, ctx, out card, out idCard))
        {
            return true;
        }

        card = null;
        idCard = default!;
        return false;
    }

    /// <summary>A card, or the card inside a PDA.</summary>
    private static bool TryReadCard(
        EntityUid uid,
        FlagContext ctx,
        out EntityUid? card,
        out IdCardComponent idCard)
    {
        if (ctx.EntMan.TryGetComponent<IdCardComponent>(uid, out var direct))
        {
            card = uid;
            idCard = direct;
            return true;
        }

        if (ctx.EntMan.TryGetComponent<PdaComponent>(uid, out var pda) &&
            pda.ContainedId is { } contained &&
            ctx.EntMan.TryGetComponent<IdCardComponent>(contained, out var containedCard))
        {
            card = contained;
            idCard = containedCard;
            return true;
        }

        card = null;
        idCard = default!;
        return false;
    }

    /// <summary>
    /// Contraband at the given severity, unless the player's job allows it.
    /// </summary>
    private static bool MatchesContraband(
        EntityUid uid,
        ContrabandSeverityCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        if (!ctx.EntMan.TryGetComponent<ContrabandComponent>(uid, out var contraband))
            return false;

        if (!string.Equals(contraband.Severity.Id, check.Severity, StringComparison.OrdinalIgnoreCase))
            return false;

        if (check.RespectAllowedJobs && IsAllowedForPlayer(contraband, ctx))
            return false;

        // The per-job exemption.
        if (IsTheftSeverity(check.Severity) && IsTheftExemptForPlayer(uid, ctx))
            return false;

        evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Inventory, contraband.Severity.Id, uid, Describe(uid, ctx)));
        return true;
    }

    private static bool IsTheftSeverity(string severity) =>
        TheftSeverities.Contains(severity, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the item is exempt from the theft flag for the player's job.</summary>
    private static bool IsTheftExemptForPlayer(EntityUid uid, FlagContext ctx)
    {
        if (!ctx.EntMan.TryGetComponent<MetaDataComponent>(uid, out var meta) || meta.EntityPrototype == null)
            return false;

        if (!TryGetIdCard(ctx.Player, ctx, out _, out var idCard))
            return false;

        return IsTheftExempt(meta.EntityPrototype.ID, idCard.LocalizedJobTitle ?? string.Empty);
    }

    /// <summary>
    /// Whether the player's job or department appears in the item's allowed set.
    /// </summary>
    private static bool IsAllowedForPlayer(ContrabandComponent contraband, FlagContext ctx)
    {
        if (!TryGetIdCard(ctx.Player, ctx, out _, out var idCard))
            return false;

        // A spawned card carries no job prototype, so the job is matched by localized name, as upstream does.
        var jobNames = new List<string>(contraband.AllowedJobs.Count);
        foreach (var job in contraband.AllowedJobs)
        {
            if (ctx.Prototypes.TryIndex(job, out JobPrototype? prototype))
                jobNames.Add(prototype.LocalizedName);
        }

        return IsAllowedBy(
            contraband.AllowedDepartments,
            jobNames,
            idCard.JobDepartments,
            idCard.LocalizedJobTitle);
    }

    /// <summary>
    /// Upstream's allowed-for rule: a matching department or job clears the item. An item with no
    /// allow-list is restricted for everyone.
    /// </summary>
    internal static bool IsAllowedBy(
        IReadOnlySet<ProtoId<DepartmentPrototype>> allowedDepartments,
        IReadOnlyCollection<string> allowedJobNames,
        IReadOnlyList<ProtoId<DepartmentPrototype>> playerDepartments,
        string? playerJob)
    {
        if (allowedDepartments.Count == 0 && allowedJobNames.Count == 0)
            return false;

        foreach (var department in playerDepartments)
        {
            if (allowedDepartments.Contains(department))
                return true;
        }

        if (string.IsNullOrEmpty(playerJob))
            return false;

        foreach (var name in allowedJobNames)
        {
            if (string.Equals(name, playerJob, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool MatchesMelee(
        EntityUid uid,
        float minDamage,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        if (!ctx.EntMan.TryGetComponent<MeleeWeaponComponent>(uid, out var melee))
            return false;

        float total = ReflectConvert.DamageTotal(melee.Damage);
        if (total < minDamage)
            return false;

        // Wielding does not scale melee damage upstream, so this is reported rather than applied.
        bool wielded = ctx.EntMan.TryGetComponent<WieldableComponent>(uid, out var wieldable) &&
                       wieldable.Wielded;

        evidence.Add(PlayerFlagTrigger.For(
            FlagTriggerSource.Equipped,
            "MeleeDamage",
            uid,
            $"{total:0.#} damage{(wielded ? ", wielded" : string.Empty)}"));

        return true;
    }

    private static bool MatchesSyndicateSignal(
        EntityUid uid,
        SyndicateSignal signal,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        var status = ctx.Syndicate.CheckPlayerStatus(uid);

        switch (signal)
        {
            case SyndicateSignal.Uplink when status.Uplink:
                evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Store, "Uplink", uid, "Antagonist store balance"));
                return true;

            case SyndicateSignal.Contraband when status.Contra:
                evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Inventory, "Contraband", uid, "Syndicate contraband"));
                return true;

            case SyndicateSignal.Implants:
                foreach (var implant in status.Implants)
                {
                    if (implant.Category != "Syndicate")
                        continue;

                    evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Implant, implant.Name, uid, implant.Category));
                }

                return evidence.Count > 0;
        }

        return false;
    }

    /// <summary>
    /// Whether a carried container's solution holds a reagent at one of the checked severities. A
    /// container's contents are what "carries a dangerous chemical" means.
    /// </summary>
    private static bool MatchesContrabandReagent(
        EntityUid uid,
        ContrabandReagentCheck check,
        FlagContext ctx,
        List<PlayerFlagTrigger> evidence)
    {
        if (!ctx.EntMan.TryGetComponent<SolutionComponent>(uid, out var solution))
            return false;

        foreach (var reagent in solution.Solution.Contents)
        {
            if (!ctx.Prototypes.TryIndex(reagent.Reagent.Prototype, out ReagentPrototype prototype))
                continue;

            if (prototype.ContrabandSeverity is not { } severity)
                continue;

            if (check.Severities is { } allowed &&
                !allowed.Contains(severity.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Quantity is a FixedPoint2, which lives in a different assembly on Goob, so it is read
            // reflectively rather than as a typed member.
            var amount = Reflect.TryGet(reagent, "Quantity", out var quantity, ReflectOptions.Any) &&
                         ReflectConvert.TryToDouble(quantity, out var value)
                ? value
                : 0d;

            evidence.Add(PlayerFlagTrigger.For(
                FlagTriggerSource.Chemistry,
                prototype.LocalizedName,
                uid,
                $"{amount}u ({severity.Id})"));
        }

        return evidence.Count > 0;
    }

    /// <summary>An emag with the interaction flag, i.e. one that actually rewrites a silicon's laws.</summary>
    private static bool MatchesEmagged(EntityUid uid, FlagContext ctx, List<PlayerFlagTrigger> evidence)
    {
        if (!ctx.Syndicate.IsEmagged(uid))
            return false;

        evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Component, "Emagged", uid));
        return true;
    }

    /// <summary>A criminal status security has flagged on the station record.</summary>
    private static bool MatchesWanted(EntityUid uid, FlagContext ctx, List<PlayerFlagTrigger> evidence)
    {
        var status = ctx.Syndicate.GetWantedStatus(uid);
        if (!RobustaSyndicateDetectorSystem.IsWantedStatus(status))
            return false;

        evidence.Add(PlayerFlagTrigger.For(FlagTriggerSource.Record, "Wanted", uid, status.ToString()));
        return true;
    }

    /// <summary>Any ghost-role verdict, including one the detector latched earlier in the round.</summary>
    private static bool MatchesAntagonist(EntityUid uid, FlagContext ctx, List<PlayerFlagTrigger> evidence)
    {
        if (ctx.Syndicate.DetectAntagonist(uid) is not { } kind)
            return false;

        evidence.Add(PlayerFlagTrigger.For(
            FlagTriggerSource.Component,
            kind.ToString(),
            uid,
            ctx.Syndicate.IsAntagonistRetained(uid) ? "retained" : null));

        return true;
    }
}
