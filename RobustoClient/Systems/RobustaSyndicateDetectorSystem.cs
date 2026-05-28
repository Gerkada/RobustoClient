using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Store.Components;
using Content.Shared.StoreDiscount.Components;
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
        // 1. Check for the presence of a store and its balance
        if (TryComp<StoreComponent>(target, out var store))
        {
            foreach (var currencyPair in store.Balance)
            {
                if ((float)currencyPair.Value > 0 && _antagCurrencies.Contains(currencyPair.Key.Id))
                {
                    return true;
                }
            }
        }

        // 2. Check for hidden discounts
        if (TryComp<StoreDiscountComponent>(target, out var discountComponent))
        {
            foreach (var discount in discountComponent.Discounts)
            {
                var categoryId = discount.DiscountCategory.Id.ToLower();
                if (categoryId.Contains("syndicate") || categoryId.Contains("traitor"))
                    return true;
            }
        }

        return false;
    }

    private bool IsSyndicateContraband(EntityUid item)
    {
        if (TryComp<ContrabandComponent>(item, out var contra) && contra.Severity == "Syndicate")
            return true;

        if (_tagSystem.HasTag(item, "Syndicate") || _tagSystem.HasTag(item, "NuclearOperative"))
            return true;

        if (TryComp<MetaDataComponent>(item, out var meta) && meta.EntityPrototype != null)
        {
            var id = meta.EntityPrototype.ID.ToLower();
            if ((id.Contains("syndicate") || id.Contains("emag") || id.Contains("esword") || id.Contains("c4") || id.Contains("stealthbox"))  
                && !id.Contains("poster") && !id.Contains("toy") && !id.Contains("plushie"))
            {
                return true;
            }
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
            foreach (var container in containerManager.Containers.Values)
            {
                foreach (var entity in container.ContainedEntities)
                {
                    ScanEntity(entity, ref hasUplink, ref hasContraband, scanned, depth + 1);
                }
            }
        }
    }
}