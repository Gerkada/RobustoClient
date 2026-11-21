using Content.Shared.Armor;
using Content.Shared.Damage.Components;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using RobustoClient.Reflection;
using RobustoClient.Systems;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Equipment block: weapons, non-lethals and the combined armour coefficients.</summary>
public sealed partial class PlayerStateUtility
{
    /// <summary>Slot upstream keeps the inserted magazine in, mirrors <c>SharedGunSystem.MagazineSlot</c>.</summary>
    private const string MagazineSlot = "gun_magazine";

    /// <summary>Slots whose armor counts toward the total.</summary>
    private static readonly string[] _armorSlots =
        ["head", "outerClothing", "gloves", "shoes", "mask", "eyes", "neck", "belt", "suitstorage"];

    /// <summary>Melee damage a weapon must reach to be listed.</summary>
    private const float _weaponMinDamage = 12f;

    private IPlayerData? BuildEquipment(EntityUid entity, Func<IReadOnlyList<InventoryEntry>> inventory)
    {
        var entries = inventory();
        if (entries.Count == 0)
            return null;

        var weapons = ScanWeapons(entries);
        var nonLethals = ScanNonLethals(entries);
        var armor = ScanArmorCoefficients(entity);

        if (weapons.Count == 0 && nonLethals.Count == 0 && armor.Count == 0)
            return null;

        return new EquipmentData(weapons, nonLethals, armor);
    }

    /// <summary>Firearms and melee weapons, with the damage one shot or swing can do.</summary>
    private List<WeaponEntry> ScanWeapons(IReadOnlyList<InventoryEntry> inventory)
    {
        var results = new List<WeaponEntry>();

        foreach (var entry in inventory)
        {
            var ranged = HasComp<GunComponent>(entry.Uid);

            if (ranged && GetProjectileStamina(entry.Uid) > 0f)
                continue;

            var hasMelee = TryComp<MeleeWeaponComponent>(entry.Uid, out var meleeComp);
            var meleeDamage = hasMelee ? ReflectConvert.DamageTotal(meleeComp!.Damage) : 0f;

            if (!ranged && meleeDamage < _weaponMinDamage)
                continue;

            // A gun's number is what its projectile does.
            var damage = ranged ? GetProjectileDamage(entry.Uid) : meleeDamage;
            results.Add(new WeaponEntry(entry.ItemName, damage, ReadAmmunition(entry.Uid), ranged));
        }

        return results;
    }

    /// <summary>Disablers, batons and bolas.</summary>
    private List<WeaponEntry> ScanNonLethals(IReadOnlyList<InventoryEntry> inventory)
    {
        var results = new List<WeaponEntry>();

        foreach (var entry in inventory)
        {
            float stamina;

            if (TryComp<StaminaDamageOnHitComponent>(entry.Uid, out var onHit))
            {
                stamina = onHit.Damage;
            }
            else if (TryComp<StaminaDamageOnCollideComponent>(entry.Uid, out var onCollide))
            {
                stamina = onCollide.Damage;
            }
            else if (GetProjectileStamina(entry.Uid) is > 0f and var projectileStamina)
            {
                // A disabler's stamina figure lives on the bolt, not on the item.
                stamina = projectileStamina;
            }
            else
            {
                continue;
            }

            results.Add(new WeaponEntry(entry.ItemName, stamina, ReadAmmunition(entry.Uid), false));
        }

        return results;
    }

    /// <summary>Prototype of the entity this weapon fires, or null when it cannot be resolved.</summary>
    internal EntProtoId? ResolveProjectile(EntityUid item)
    {
        EntProtoId? ammo;

        if (TryComp<BatteryAmmoProviderComponent>(item, out var battery))
        {
            ammo = battery.Prototype;
        }
        else if (TryComp<BallisticAmmoProviderComponent>(item, out var ballistic))
        {
            ammo = ballistic.Proto;
        }
        else if (HasComp<ChamberMagazineAmmoProviderComponent>(item))
        {
            // A magazine-fed gun names its ammo on the inserted magazine, not on itself. The component
            // type is only used to spot the gun, the slot is read through the container system because
            // SharedGunSystem.GetMagazineEntity is protected.
            if (!_container.TryGetContainer(item, MagazineSlot, out var container) ||
                container.ContainedEntities.Count == 0)
            {
                return null;
            }

            ammo = TryComp<BallisticAmmoProviderComponent>(container.ContainedEntities[0], out var magazineAmmo)
                ? magazineAmmo.Proto
                : null;
        }
        else
        {
            return null;
        }

        if (ammo is not { } ammoId || !_prototypes.TryIndex(ammoId.Id, out EntityPrototype? prototype))
            return null;

        if (prototype.TryGetComponent<CartridgeAmmoComponent>(out var cartridge, EntityManager.ComponentFactory))
            return cartridge.Prototype;

        return ammoId;
    }

    /// <summary>Damage one shot of the weapon's projectile deals, or 0 when it carries none.</summary>
    internal float GetProjectileDamage(EntityUid item)
    {
        if (ResolveProjectile(item) is not { } projectile ||
            !_prototypes.TryIndex(projectile.Id, out EntityPrototype? prototype) ||
            !prototype.TryGetComponent<ProjectileComponent>(out var projectileComp, EntityManager.ComponentFactory))
        {
            return 0f;
        }

        return ReflectConvert.DamageTotal(projectileComp.Damage);
    }

    /// <summary>Stamina damage the item's projectile carries, or 0 when it fires something lethal.</summary>
    internal float GetProjectileStamina(EntityUid item)
    {
        if (ResolveProjectile(item) is not { } projectile ||
            !_prototypes.TryIndex(projectile.Id, out EntityPrototype? prototype) ||
            !prototype.TryGetComponent<StaminaDamageOnCollideComponent>(out var stamina, EntityManager.ComponentFactory))
        {
            return 0f;
        }

        return stamina.Damage;
    }

    /// <summary>Rounds left, loaded count, or battery hits left, kept numeric for the UI to format.</summary>
    internal AmmunitionState ReadAmmunition(EntityUid item)
    {
        // Upstream's own count first: it is the only one that knows about chambers and magazines,
        // and it reaches the inserted magazine for magazine-fed guns.
        var ammoEvent = new GetAmmoCountEvent();
        RaiseLocalEvent(item, ref ammoEvent, false);
        if (ammoEvent.Capacity > 0)
            return new AmmunitionState(Shots: ammoEvent.Count, Capacity: ammoEvent.Capacity);

        if (TryComp<BatteryAmmoProviderComponent>(item, out var battery))
            return new AmmunitionState(Shots: battery.Shots, Capacity: battery.Capacity);

        // UnspawnedCount is what the client is guaranteed to know.
        if (TryComp<BallisticAmmoProviderComponent>(item, out var ballistic))
        {
            return new AmmunitionState(
                Shots: Math.Max(ballistic.UnspawnedCount, ballistic.Count),
                Capacity: ballistic.Capacity);
        }

        // The melee battery counter is upstream-only, so it is resolved by name and read reflectively.
        var hits = EntityManager.System<RobustaSyndicateDetectorSystem>()
            .GetComponentNamed(item, RobustaSyndicateDetectorSystem.MeleeBatteryHitsLeftComponentName);

        if (hits != null &&
            Reflect.TryGet(hits, "HitPowerCost", out var cost, RobustaSyndicateDetectorSystem.OptionalMember) &&
            cost is float hitCost && hitCost > 0f &&
            TryComp<Content.Shared.Power.Components.BatteryComponent>(item, out var meleeBattery))
        {
            return new AmmunitionState(HitsLeft: (int)(meleeBattery.LastCharge / hitCost));
        }

        return new AmmunitionState();
    }

    /// <summary>
    /// Protection from everything the target wears, as a coefficient per damage type. Combining is
    /// game truth, so it happens here, grouping and percentages belong to the panel.
    /// </summary>
    private IReadOnlyDictionary<string, float> ScanArmorCoefficients(EntityUid entity)
    {
        var pieces = new List<IReadOnlyDictionary<string, float>>();

        foreach (var slot in _armorSlots)
        {
            if (!_inventory.TryGetSlotEntity(entity, slot, out var worn) || worn is not { } wornUid)
                continue;

            if (!TryComp<ArmorComponent>(wornUid, out var armor))
                continue;

            pieces.Add(armor.Modifiers.Coefficients.ToDictionary(
                entry => entry.Key.Id,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase));
        }

        return CombineArmorCoefficients(pieces);
    }

    /// <summary>
    /// Overlapping armor pieces multiply rather than add, which is what upstream does when it applies
    /// them. Pure so the rule is testable without a client.
    /// </summary>
    internal static Dictionary<string, float> CombineArmorCoefficients(
        IEnumerable<IReadOnlyDictionary<string, float>> sets)
    {
        var coefficients = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var set in sets)
        {
            foreach (var (type, coefficient) in set)
            {
                coefficients[type] = coefficients.TryGetValue(type, out var existing)
                    ? existing * coefficient
                    : coefficient;
            }
        }

        return coefficients;
    }
}
