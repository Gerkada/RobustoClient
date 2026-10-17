using System;
using System.Collections.Generic;
using Content.Shared.Body;
using Content.Shared.Body.Part;
using Content.Shared.CMU14.Round.Antags;
using Content.Shared.CMU14.Round.Antags.Arsonist;
using Content.Shared.CMU14.Round.Antags.BountyHunter;
using Content.Shared.CMU14.Round.Antags.Cannibal;
using Content.Shared.CMU14.Round.Antags.CLFSaboteur;
using Content.Shared.CMU14.Round.Antags.CLFSleeperAgent;
using Content.Shared.CMU14.Round.Antags.ColonyBounty;
using Content.Shared.CMU14.Round.Antags.CorporateAgent;
using Content.Shared.CMU14.Round.Antags.Replicant;
using Content.Shared.CMU14.Round.Antags.Rider;
using Content.Shared.CMU14.Round.Antags.StrikeOrganizer;
using Content.Shared.CMU14.Round.Antags.Vigilante;
using Content.Shared.CMU14.Threats.Mobs.Biomorph;
using Content.Shared.CMU14.Threats.Mobs.CLF;
using Content.Shared.CMU14.Threats.Mobs.Cultist;
using Content.Shared.CMU14.Threats.Mobs.SubvertedSynth;
using Content.Shared.CMU14.Threats.Mobs.ZombieSummoner;
using Content.Shared.Contraband;
using Content.Shared.Implants.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC.Components;
using Content.Shared.StatusIcon;
using Content.Shared.StatusIcon.Components;
using Content.Shared.Store.Components;
using Content.Shared.StoreDiscount.Components;
using Content.Shared.Tag;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace RobustoClient.Systems;

public record struct ImplantInfo(string Name, string Category);

public sealed class RobustaSyndicateDetectorSystem : EntitySystem
{
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _protoMan = default!;

    private readonly Dictionary<EntityUid, (bool Uplink, bool Contra, List<ImplantInfo> Implants, TimeSpan LastCheck)> _cache = new();
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(1);

    private StatusIconData? _cachedAntagIcon;
    private bool _iconResolved;

    // Set of currencies that 100% belong to antagonists
    private readonly HashSet<string> _antagCurrencies = new()
    {
        "Telecrystal",
        "StolenEssence",
        "WizCoin",
        "ChangelingDNA"
    };

    private static readonly HashSet<string> _hostileFactions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLF",
        "Biomorph",
        "Zombie",
        "CMUYautjaBadBlood"
    };

    // CMU/AU14 antagonist items mapped to their role label
    private static readonly Dictionary<string, string> _cmuAntagItemRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Replicant
        ["CMUForgedTransponder"] = "Replicant",

        // Corporate Agent / Spy
        ["CMUCorporateDataLink"] = "Corporate Agent",
        ["CMUIDCardWYAgent"] = "WY Agent",
        ["AU14IDCardCorporateSpy"] = "Corporate Spy",

        // CLF Saboteur & Veteran
        ["CMUCaseCLFSaboteur"] = "CLF Saboteur",
        ["CMUSatchelCLF"] = "CLF Saboteur",
        ["AU14CaseCLFVeteranKeepsake"] = "CLF Veteran",
        ["AU14WeaponRevolverM44Inscribed"] = "CLF Veteran",
        ["AUPaperCLFVeteran"] = "CLF Veteran",

        // CLF Cell / Guerilla / Recruiter
        ["AU14HeavyCellKit"] = "CLF",
        ["AU14CLFCellKit"] = "CLF",
        ["AU14CLFHeadset"] = "CLF",
        ["ANPRCFillCardCLF"] = "CLF",
        ["AU14CLFStandingOrdersPad"] = "CLF",
        ["AU14StampCLFCell"] = "CLF",
        ["AU14TattooGun"] = "CLF Recruiter",
        ["CMUCLFSynthSubversionKey"] = "CLF Subverter",
        ["AU14IDCardCLFCivilian"] = "CLF",
        ["AU14CMJumpsuitColonistCLF"] = "CLF",
        ["AU14RMCJumpsuitCivilianCLF"] = "CLF",

        // Cultist
        ["AU14SynthSubversionKeyCultist"] = "Cultist",
        ["AU14PortableVendorCultist"] = "Cultist",
        ["AU14CultistSatchelBlack"] = "Cultist",
        ["AU14CultistSatchelMarine"] = "Cultist",
        ["AU14CultistSatchelBlue"] = "Cultist",
        ["AU14CultistBelt"] = "Cultist",
        ["AU14BeltHolsterPistolCultistMK45"] = "Cultist",

        // Drug Dealer
        ["CMUDrugDealerDrugBackpack"] = "Drug Dealer",
        ["CMUDrugDealerChemKit"] = "Drug Dealer",
        ["CMUDrugCookingKit"] = "Drug Dealer",
        ["AU14DrugDealerDrugBottle"] = "Drug Dealer",
        ["AUPaperDrugs"] = "Drug Dealer",

        // Strike Organizer
        ["CMUStrikePetition"] = "Strike Organizer",
        ["AU14StrikeOrganizerBackpack"] = "Strike Organizer",
        ["AUPaperStrikeOrganizer"] = "Strike Organizer",

        // Vigilante
        ["CMUBaseballBatVigilante"] = "Vigilante",

        // Fugitive, Serial Killer, Runaway Synth
        ["AU14ObjectiveItemWYLaptop"] = "Fugitive",
        ["AUPaperFugitive"] = "Fugitive",
        ["AUPaperSerialKiller"] = "Serial Killer",
        ["AUPaperRunawaySynth"] = "Runaway Synth",

        // Wanted notices / Infiltrators
        ["CMUPaperWantedSynthInfiltrator"] = "Synth Infiltrator",
        ["CMUPaperWantedWYOperative"] = "WY Operative",
        ["CMUPaperWantedCorporateSpy"] = "Corporate Spy"
    };

    // CMU/AU14 contraband & sabotage items
    private static readonly HashSet<string> _cmuContrabandPrototypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AU14IED",
        "AU14IEDLarge",
        "AU14IEDVest",
        "CMUPipeBomb",
        "RMCGrenadeIED",
        "RMCExplosivePlastic",
        "AU14RemoteSignallerAdvanced",
        "AU14RadioJammer",
        "AU14CLFBaseStation",
        "AU14CLFNetSpliceKit",
        "AU14CLFClandestineRelay",
        "AU14TattooInkCartridge",
        "RMCCLFArmband",
        "RMCCLFCellLeaderArmband"
    };

    public void AddAntagStatusIcon(EntityUid uid, ref GetStatusIconsEvent args)
    {
        if (!HasComp<MobStateComponent>(uid))
            return;

        var status = CheckPlayerStatus(uid);
        if (!status.Uplink)
            return;

        var icon = GetOrResolveAntagIcon();
        if (icon != null)
        {
            args.StatusIcons.Add(icon);
        }
    }

    private StatusIconData? GetOrResolveAntagIcon()
    {
        if (_iconResolved)
            return _cachedAntagIcon;

        _iconResolved = true;

        StatusIconPrototype? proto = null;
        if (_protoMan.TryIndex<JobIconPrototype>("AU14JobIconThreatMember", out var threatJobIcon))
            proto = threatJobIcon;
        else if (_protoMan.TryIndex<FactionIconPrototype>("CLFFaction", out var clfIcon))
            proto = clfIcon;
        else if (_protoMan.TryIndex<FactionIconPrototype>("SyndicateFaction", out var syndiIcon))
            proto = syndiIcon;

        if (proto != null)
        {
            _cachedAntagIcon = new StatusIconData
            {
                Icon = proto.Icon,
                Priority = 20,
                LocationPreference = StatusIconLocationPreference.Right,
                Layer = StatusIconLayer.Base,
                VisibleToGhosts = true,
                HideInContainer = true,
                HideOnStealth = false,
                ShowTo = null,
                IsShaded = false
            };
        }

        return _cachedAntagIcon;
    }

    public (bool Uplink, bool Contra, List<ImplantInfo> Implants) CheckPlayerStatus(EntityUid uid)
    {
        if (!HasComp<MobStateComponent>(uid))
            return (false, false, []);

        var now = _timing.CurTime;

        if (_cache.TryGetValue(uid, out var cached) && (now - cached.LastCheck) < _cacheLifetime)
        {
            return (cached.Uplink, cached.Contra, cached.Implants);
        }

        bool hasUplink = false;
        bool hasContra = false;
        var implants = new List<ImplantInfo>();
        var addedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedEntities = new HashSet<EntityUid>();

        CheckMobAntagComponents(uid, ref hasUplink, ref hasContra, implants, addedTags);
        ScanEntity(uid, ref hasUplink, ref hasContra, implants, addedTags, scannedEntities);

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

                        if (addedTags.Add(name))
                            implants.Add(new ImplantInfo(name, category));
                        foundSpecific = true;
                    }
                }
            }

            // FALLBACK: If component exists but no specifics (or server doesn't send entities inside the container)
            if (!foundSpecific && addedTags.Add("Unknown Implant"))
            {
                implants.Add(new ImplantInfo("Unknown Implant", "Neutral"));
            }
        }

        _cache[uid] = (hasUplink, hasContra, implants, now);
        return (hasUplink, hasContra, implants);
    }

    private static void AddRoleTag(List<ImplantInfo> implants, HashSet<string> addedTags, string roleName, string category = "Syndicate")
    {
        if (addedTags.Add(roleName))
        {
            implants.Add(new ImplantInfo(roleName, category));
        }
    }

    private void CheckMobAntagComponents(
        EntityUid uid,
        ref bool hasUplink,
        ref bool hasContra,
        List<ImplantInfo> implants,
        HashSet<string> addedTags)
    {
        if (HasComp<CLFMemberComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "CLF");
        }

        if (HasComp<CLFSaboteurComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "CLF Saboteur");
        }

        if (HasComp<CLFSleeperAgentComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "CLF Sleeper");
        }

        if (HasComp<SubvertedSynthComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Subverted Synth");
        }

        if (HasComp<CultistComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Cultist");
        }

        if (HasComp<BiomorphMimicComponent>(uid) || HasComp<BiomorphMimicTransformedComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Biomorph Mimic");
        }
        else if (HasComp<BiomorphComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Biomorph");
        }

        if (HasComp<BiomorphInfectionComponent>(uid))
        {
            hasContra = true;
            AddRoleTag(implants, addedTags, "Biomorph Infected", "Contraband");
        }

        if (HasComp<ReplicantComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Replicant");
        }

        if (HasComp<CorporateAgentComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Corporate Agent");
        }

        if (HasComp<ColonyBountyComponent>(uid) || HasComp<BountyHunterComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Bounty Hunter");
        }

        if (HasComp<VigilanteComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Vigilante");
        }

        if (HasComp<ArsonistComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Arsonist");
        }

        if (HasComp<CannibalComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Cannibal");
        }

        if (HasComp<ZombieSummonerComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Zombie Summoner");
        }
        else if (HasComp<ZombieSummonerMinionComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Zombie Minion");
        }

        if (HasComp<RiderComponent>(uid) || HasComp<RiddenComponent>(uid) || HasComp<RiderManifestComponent>(uid))
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Rider Host");
        }

        if (HasComp<SynthGunAccessComponent>(uid))
        {
            hasContra = true;
            AddRoleTag(implants, addedTags, "Synth Gun Access", "Contraband");
        }

        if (TryComp<ContainerManagerComponent>(uid, out var containerMan) &&
            containerMan.Containers.TryGetValue("rider_hatchling_slot", out var riderSlot) &&
            riderSlot.ContainedEntities.Count > 0)
        {
            hasUplink = true;
            AddRoleTag(implants, addedTags, "Rider Host");
        }

        if (TryComp<NpcFactionMemberComponent>(uid, out var factionMember))
        {
            foreach (var faction in factionMember.Factions)
            {
                var factionName = faction.ToString();
                if (_hostileFactions.Contains(factionName))
                {
                    hasUplink = true;
                    AddRoleTag(implants, addedTags, factionName);
                }
            }
        }
    }

    private bool CheckUplink(EntityUid target, List<ImplantInfo> implants, HashSet<string> addedTags)
    {
        bool isAntagItem = false;

        // 1. Check CMU antagonist item components
        if (HasComp<TattooGunComponent>(target))
        {
            AddRoleTag(implants, addedTags, "CLF Recruiter");
            isAntagItem = true;
        }

        if (HasComp<SynthSubverterComponent>(target))
        {
            AddRoleTag(implants, addedTags, "Synth Subverter");
            isAntagItem = true;
        }

        if (HasComp<CorporateDataLinkComponent>(target))
        {
            AddRoleTag(implants, addedTags, "Corporate Agent");
            isAntagItem = true;
        }

        if (HasComp<StrikePetitionComponent>(target))
        {
            AddRoleTag(implants, addedTags, "Strike Organizer");
            isAntagItem = true;
        }

        // 2. Check CMU antagonist item prototypes
        if (TryComp<MetaDataComponent>(target, out var meta) && meta.EntityPrototype != null)
        {
            var protoId = meta.EntityPrototype.ID;
            if (_cmuAntagItemRoles.TryGetValue(protoId, out var roleName))
            {
                AddRoleTag(implants, addedTags, roleName);
                isAntagItem = true;
            }
        }

        if (isAntagItem)
            return true;

        // 3. Check for the presence of a store and its balance
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

        // 4. Check for hidden discounts
        if (TryComp<StoreDiscountComponent>(target, out var discountComponent))
        {
            foreach (var discount in discountComponent.Discounts)
            {
                var categoryId = discount.DiscountCategory.Id.ToLower();
                if (categoryId.Contains("syndicate") || categoryId.Contains("traitor") || categoryId.Contains("clf"))
                    return true;
            }
        }

        return false;
    }

    private bool IsSyndicateContraband(EntityUid item)
    {
        if (TryComp<ContrabandComponent>(item, out var contra) &&
            (contra.Severity == "Syndicate" || contra.Severity == "CLF"))
            return true;

        if (_tagSystem.HasTag(item, "Syndicate"))
            return true;

        if (TryComp<MetaDataComponent>(item, out var meta) && meta.EntityPrototype != null)
        {
            var protoId = meta.EntityPrototype.ID;
            if (_cmuContrabandPrototypes.Contains(protoId))
                return true;

            var id = protoId.ToLowerInvariant();
            if ((id.Contains("syndicate") || id.Contains("emag") || id.Contains("esword") || id.Contains("c4") || id.Contains("stealthbox"))
                && !id.Contains("poster") && !id.Contains("toy") && !id.Contains("plushie"))
            {
                return true;
            }
        }

        return false;
    }

    private void ScanEntity(
        EntityUid parent,
        ref bool hasUplink,
        ref bool hasContraband,
        List<ImplantInfo> implants,
        HashSet<string> addedTags,
        HashSet<EntityUid> scanned,
        int depth = 0)
    {
        if (depth > 5) return;
        if (!scanned.Add(parent)) return;

        // Skip CMU medical body parts and organs when traversing containers
        if (depth > 0 && (HasComp<BodyPartComponent>(parent) || HasComp<OrganComponent>(parent)))
            return;

        if (depth > 0)
        {
            if (CheckUplink(parent, implants, addedTags))
                hasUplink = true;
            if (!hasContraband && IsSyndicateContraband(parent))
                hasContraband = true;
        }

        if (TryComp<ContainerManagerComponent>(parent, out var containerManager))
        {
            foreach (var (containerId, container) in containerManager.Containers)
            {
                // Skip internal body/organ containers at root mob level
                if (depth == 0 && (containerId == "body_parts" || containerId == "body_organs" || containerId == "implants"))
                    continue;

                foreach (var entity in container.ContainedEntities)
                {
                    ScanEntity(entity, ref hasUplink, ref hasContraband, implants, addedTags, scanned, depth + 1);
                }
            }
        }
    }
}