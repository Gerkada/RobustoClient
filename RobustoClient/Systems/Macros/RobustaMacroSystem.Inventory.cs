using Content.Shared.Cuffs.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Flash.Components;
using Content.Shared.Slippery;
using Content.Shared.Weapons.Melee;
using RobustoClient.Reflection;

namespace RobustoClient.Systems.Macros;

public sealed partial class RobustaMacroSystem
{
    private const string InventoryCategory = "Inventory";
    private const string SecurityCategory = "Security";

    #region General

    /// <summary>
    /// Highest knockdown time wins.
    /// </summary>
    private static readonly MacroDefinition _slippery = new(
        "macro.slippery",
        "Slippery",
        InventoryCategory,
        sys => sys.EquipBest<SlipperyComponent>(slip =>
            (float)slip.SlipData.KnockdownTime.TotalSeconds + (slip.SlipData.SuperSlippery ? 100f : 0f)));

    /// <summary>Highest total melee damage wins.</summary>
    private static readonly MacroDefinition _melee = new(
        "macro.melee",
        "Melee",
        InventoryCategory,
        sys => sys.EquipBest<MeleeWeaponComponent>(melee => ReflectConvert.DamageTotal(melee.Damage)));

    /// <summary>Highest per-shot projectile damage times available shots wins.</summary>
    private static readonly MacroDefinition _ranged = new(
        "macro.ranged",
        "Ranged",
        InventoryCategory,
        sys => sys.EquipBestRanged());

    private static readonly MacroDefinition[] _inventoryMacros = [_slippery, _melee, _ranged];

    #endregion

    #region Security

    private static readonly MacroDefinition _baton = new(
        "macro.baton",
        "Baton",
        SecurityCategory,
        sys => sys.EquipBest<StaminaDamageOnHitComponent>(stamina => stamina.Damage));

    private static readonly MacroDefinition _disabler = new(
        "macro.disabler",
        "Disabler",
        SecurityCategory,
        sys => sys.EquipBestDisabler());

    private static readonly MacroDefinition _cuffs = new(
        "macro.cuffs",
        "Cuffs",
        SecurityCategory,
        sys => sys.EquipBest<HandcuffComponent>(cuffs => cuffs.StunBonus));

    private static readonly MacroDefinition _flash = new(
        "macro.flash",
        "Flash",
        SecurityCategory,
        sys => sys.EquipBest<FlashComponent>(flash => (float)flash.FlashingTime.TotalSeconds));

    private static readonly MacroDefinition[] _securityMacros = [_baton, _disabler, _cuffs, _flash];

    #endregion
}
