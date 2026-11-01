using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Shared.Store.Components;
using Content.Shared.Tag;
using Content.Shared.Contraband;
using Content.Shared.PDA;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Timing;
using Robust.Shared.Containers;
using Content.Shared.Implants.Components;

namespace RobustoClient.Systems;

public record struct ImplantInfo(string Name, string Category);

public sealed class RobustaSyndicateDetectorSystem : EntitySystem
{
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, (bool Uplink, bool Contra, List<ImplantInfo> Implants, TimeSpan LastCheck)> _cache = new();
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(1);
    private static readonly FieldInfo? StoreBalanceField = typeof(StoreComponent).GetField("Balance");
    private static readonly Type? StoreDiscountType =
        Type.GetType("Content.Shared.StoreDiscount.Components.StoreDiscountComponent, Content.Shared", false);
    private static readonly FieldInfo? StoreDiscountsField = StoreDiscountType?.GetField("Discounts");
    private static FieldInfo? _discountCategoryField;
    private static MethodInfo? _fixedPointFloatMethod;

    // Set of currencies that 100% belong to antagonists
    private readonly HashSet<string> _antagCurrencies = new()
    {
        "Telecrystal",
        "StolenEssence",
        "WizCoin",
        "ChangelingDNA"
    };

    public (bool Uplink, bool Contra, List<ImplantInfo> Implants) CheckPlayerStatus(EntityUid uid)
    {
        var now = _timing.CurTime;

        if (_cache.TryGetValue(uid, out var cached) && (now - cached.LastCheck) < _cacheLifetime)
        {
            return (cached.Uplink, cached.Contra, cached.Implants);
        }

        bool hasUplink = false;
        bool hasContra = false;
        var implants = new List<ImplantInfo>();
        var scannedEntities = new HashSet<EntityUid>();

        ScanEntity(uid, ref hasUplink, ref hasContra, scannedEntities);

        // --- IMPLANT SCANNER ---
        if (TryComp<ImplantedComponent>(uid, out var implanted))
        {
            bool foundSpecific = false;

            if (implanted.ImplantContainer != null)
            {
                foreach (var implant in implanted.ImplantContainer.ContainedEntities)
                {
                    if (TryComp<MetaDataComponent>(implant, out var meta) && meta.EntityPrototype != null)
                    {
                        var id = meta.EntityPrototype.ID;
                        var name = meta.EntityName.Replace(" implant", "");
                        string category = "Neutral";

                        // SYNDICATE IMPLANTS
                        if (id.Contains("Uplink") || id.Contains("MicroBomb") || id.Contains("MacroBomb") ||
                            id.Contains("Emp") || id.Contains("Scram") || id.Contains("Freedom") ||
                            id.Contains("Storage") || id.Contains("VoiceMask") || id.Contains("DnaScrambler") ||
                            id.Contains("Chameleon") || id.Contains("DeathAcidifier") || id.Contains("DeathRattleImplant") && !id.Contains("Centcomm") ||
                            id.Contains("FakeMindShield"))
                        {
                            category = "Syndicate";
                            if (id.Contains("Uplink")) hasUplink = true;
                        }
                        // NANOTRASEN / CENTCOMM IMPLANTS
                        else if (id.Contains("MindShield") || id.Contains("Tracking") || id.Contains("Centcomm"))
                        {
                            category = "NT";
                        }

                        implants.Add(new ImplantInfo(name, category));
                        foundSpecific = true;
                    }
                }
            }

            // FALLBACK: If component exists but no specifics (or server doesn't send entities inside the container)
            if (!foundSpecific)
            {
                implants.Add(new ImplantInfo("Unknown Implant", "Neutral"));
            }
        }

        _cache[uid] = (hasUplink, hasContra, implants, now);
        return (hasUplink, hasContra, implants);
    }

    private bool CheckUplink(EntityUid target)
    {
        // 1. Check for the presence of a store and its balance (via IDictionary reflection to avoid FixedPoint2 assembly binding differences across Wizden/GoobStation)
        if (TryComp<StoreComponent>(target, out var store) &&
            StoreBalanceField?.GetValue(store) is System.Collections.IDictionary balance)
        {
            foreach (System.Collections.DictionaryEntry entry in balance)
            {
                var keyId = entry.Key?.ToString();
                if (keyId == null || !_antagCurrencies.Contains(keyId))
                    continue;

                var valObj = entry.Value;
                if (valObj == null)
                    continue;

                _fixedPointFloatMethod ??= valObj.GetType().GetMethod("Float", Type.EmptyTypes);
                if (_fixedPointFloatMethod?.Invoke(valObj, null) is float amount && amount > 0f)
                {
                    return true;
                }
            }
        }

        // 2. Check for hidden discounts (via reflection as older GoobStation downstreams lack StoreDiscountComponent)
        if (StoreDiscountType != null &&
            EntityManager.TryGetComponent(target, StoreDiscountType, out var discountComponent) &&
            StoreDiscountsField?.GetValue(discountComponent) is System.Collections.IEnumerable discounts)
        {
            foreach (var discount in discounts)
            {
                if (discount == null)
                    continue;

                _discountCategoryField ??= discount.GetType().GetField("DiscountCategory");
                var categoryId = _discountCategoryField?.GetValue(discount)?.ToString();
                if (categoryId != null &&
                    (categoryId.Contains("syndicate", StringComparison.OrdinalIgnoreCase) ||
                     categoryId.Contains("traitor", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        return false;
    }

    private bool IsSyndicateContraband(EntityUid item)
    {
        if (TryComp<MetaDataComponent>(item, out var meta) && meta.EntityPrototype != null)
        {
            var id = meta.EntityPrototype.ID.ToLower();

            // Exclude action entities and non-contraband structures/props
            if (id.StartsWith("action") || id.Contains("airlocksyndicateglass") || id.Contains("poster") || id.Contains("toy") || id.Contains("plushie"))
                return false;

            if (TryComp<ContrabandComponent>(item, out var contra) && contra.Severity == "Syndicate")
                return true;

            if (_tagSystem.HasTag(item, "Syndicate"))
                return true;

            bool isEmag = id.StartsWith("emag") || id.Contains("_emag") || id.Contains("emag_") || id == "emag";

            if (id.Contains("syndicate") || isEmag || id.Contains("esword") || id.Contains("c4") || id.Contains("stealthbox"))
                return true;
        }

        return false;
    }

    private void ScanEntity(EntityUid parent, ref bool hasUplink, ref bool hasContraband, HashSet<EntityUid> scanned, int depth = 0)
    {
        if (depth > 5) return;
        if (hasUplink && hasContraband) return;
        if (!scanned.Add(parent)) return;

        if (!hasUplink && CheckUplink(parent)) hasUplink = true;
        if (!hasContraband && IsSyndicateContraband(parent)) hasContraband = true;

        if (TryComp<ContainerManagerComponent>(parent, out var containerManager))
        {
            foreach (var (cId, container) in containerManager.Containers)
            {
                if (cId.Equals("actions", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var entity in container.ContainedEntities)
                {
                    ScanEntity(entity, ref hasUplink, ref hasContraband, scanned, depth + 1);
                }
            }
        }
    }
}