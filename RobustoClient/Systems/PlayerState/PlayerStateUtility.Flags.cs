namespace RobustoClient.Systems.PlayerState;

public sealed partial class PlayerStateUtility
{
    /// <summary>Base Melee damage before a weapon is logged.</summary>
    private const float WeaponDamageThreshold = 14f;

    /// <summary>Contraband severities the theft flag covers.</summary>
    internal static readonly string[] TheftSeverities = ["GrandTheft", "Restricted"];

    /// <summary>
    /// Reagent severities that make a container dangerous.
    /// </summary>
    internal static readonly string[] DangerousReagentSeverities = ["Major", "Syndicate"];

    /// <summary>One carried check per theft severity.</summary>
    private static IReadOnlyList<PlayerFlagCheck> TheftChecks() => TheftSeverities
        .Select(severity => (PlayerFlagCheck)new CarriedCheck(new ContrabandSeverityCheck(severity)))
        .ToArray();
    internal const string ThiefSatchelPrototype = "SatchelThief";

    /// <summary>
    /// Default flag definitions, adding a flag is a definition here plus a <see cref="PlayerFlag"/> constant.
    /// </summary>
    internal static IReadOnlyList<PlayerFlagDefinition> DefaultFlagDefinitions() =>
    [
        // Every mob inherits MindContainerComponent, so presence alone says nothing.
        new PlayerFlagDefinition(PlayerFlag.HasMind,
        [
            new MindCheck()
        ], PlayerStateData.Vitality),

        new PlayerFlagDefinition(PlayerFlag.IsCommand,
        [
            new JobDepartmentCheck("Command")
        ], PlayerStateData.Command),

        new PlayerFlagDefinition(PlayerFlag.IsSecurity,
        [
            new JobDepartmentCheck("Security")
        ], PlayerStateData.Security),

        // Security's own record outranks anything the mob carries, and it can be cleared again, so
        // it is not latched.
        new PlayerFlagDefinition(PlayerFlag.IsWanted,
        [
            new WantedCheck()
        ], PlayerStateData.Security),

        // An uplink seen open once stays known for the round.
        new PlayerFlagDefinition(PlayerFlag.IsSyndicate,
        [
            new SyndicateSignalCheck(SyndicateSignal.Uplink)
        ], PlayerStateData.Syndicate, RoundRetained: true),

        // NoSlip is relayed through inventory, so only worn clothing matters.
        new PlayerFlagDefinition(PlayerFlag.HasNoSlips,
        [
            new EquippedCheck(new HasComponentCheck("NoSlip"), IncludeHands: false)
        ], PlayerStateData.Equipment, Display: true),

        new PlayerFlagDefinition(PlayerFlag.HasWeapon,
        [
            new EquippedCheck(new MeleeDamageCheck(WeaponDamageThreshold))
        ], PlayerStateData.Equipment),

        new PlayerFlagDefinition(PlayerFlag.HasSyndicateGear,
        [
            new CarriedCheck(new SyndicateGearCheck())
        ], PlayerStateData.Syndicate, RoundRetained: true),

        new PlayerFlagDefinition(PlayerFlag.HasStolenGear, TheftChecks(), PlayerStateData.Theft),

        // Both items come from ThiefGear, the only place upstream hands either out.
        new PlayerFlagDefinition(PlayerFlag.IsThief,
        [
            new CarriedCheck(new HasComponentCheck("ThiefBeacon")),
            new CarriedCheck(new ComponentFieldCheck(
                "MetaData", "EntityPrototype.ID", FlagComparison.Equals, ThiefSatchelPrototype))
        ], PlayerStateData.Theft, RoundRetained: true),

        new PlayerFlagDefinition(PlayerFlag.HasDangerousChemicals,
        [
            new CarriedCheck(new ContrabandReagentCheck(DangerousReagentSeverities))
        ], PlayerStateData.Chemistry),

        new PlayerFlagDefinition(PlayerFlag.HasMedicalGear,
        [
            new EquippedCheck(new HasAnyComponentCheck(
                "Healing", "Injector", "HealthAnalyzer", "Defibrillator"))
        ], PlayerStateData.Chemistry),

        // An emag is a syndicate tool, so it reads as a syndicate signal rather than as security.
        new PlayerFlagDefinition(PlayerFlag.IsEmagged,
        [
            new EmaggedCheck()
        ], PlayerStateData.Syndicate),

        // Ghost-role identification lives in RobustaSyndicateDetectorSystem.DetectAntagonist, the
        // detector latches a positive verdict, so a rev who drops the gear stays named.
        new PlayerFlagDefinition(PlayerFlag.IsAntagonist,
        [
            new AntagonistCheck()
        ], PlayerStateData.Antagonist, RoundRetained: true),
    ];

    private void RegisterDefaultFlags()
    {
        foreach (var definition in DefaultFlagDefinitions())
            RegisterFlag(definition);
    }
}
