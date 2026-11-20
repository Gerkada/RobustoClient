using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Contraband;
using Content.Shared.FixedPoint;
using Content.Shared.Roles;
using FluentAssertions;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using RobustoClient.Systems;
using RobustoClient.Systems.PlayerState;
using Xunit;

namespace RobustoClient.Tests.Systems.PlayerState;

/// <summary>
/// Covers the declarative surface of the flag system and the comparison layer under it. Evaluation
/// itself needs a live client, so it is not reachable from here.
/// </summary>
public class PlayerStateUtilityTests
{
    [Fact]
    public void EveryDefaultFlag_HasADefinition()
    {
        var defined = PlayerStateUtility.DefaultFlagDefinitions()
            .Select(d => d.Flag.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var flag in PlayerFlag.Defaults)
            defined.Should().Contain(flag.Id, $"'{flag.Id}' is declared but never registered");
    }

    [Fact]
    public void NoFlag_IsRegisteredTwice()
    {
        var ids = PlayerStateUtility.DefaultFlagDefinitions().Select(d => d.Flag.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("Body_organs", true)]
    [InlineData("Body_organs -> stomach", true)]
    [InlineData("Lungs", true)]
    [InlineData("Stomach", true)]
    [InlineData("Backpack", false)]
    [InlineData("Backpack -> cartridge", false)]
    [InlineData("", false)]
    public void BodyContainers_AreExcludedFromTheChemicalScan(string container, bool expected) =>
        // The lungs and stomach hold the mob's own gas and food solutions, and every organ sits in
        // the body organ container, so none of it is a chemical the target is carrying.
        PlayerStateUtility.IsBodyContainer(container).Should().Be(expected);

    [Theory]
    [InlineData("Ninja")]
    [InlineData("Dragon")]
    [InlineData("Carp")]
    [InlineData("Changeling")]
    [InlineData("Clone")]
    [InlineData("Skeleton")]
    [InlineData("RatKing")]
    [InlineData("NukeOperative")]
    [InlineData("Thief")]
    public void EveryAntagonistKind_HasATagLabel(string name)
    {
        // The radar uppercases the enum name for its tag, so a renamed member changes the label
        // silently. Pin the set so that is a deliberate edit.
        Enum.TryParse<AntagonistKind>(name, out var kind).Should().BeTrue();
        kind.ToString().ToUpperInvariant().Should().Be(name.ToUpperInvariant());
    }

    [Theory]
    [InlineData("JobIconCaptain", "CAP")]
    [InlineData("JobIconHeadOfSecurity", "HOS")]
    [InlineData("JobIconResearchDirector", "RD")]
    [InlineData("JobIconChiefMedicalOfficer", "CMO")]
    [InlineData("JobIconQuarterMaster", "QM")]
    [InlineData("JobIconHeadOfPersonnel", "HOP")]
    [InlineData("JobIconChiefEngineer", "CE")]
    [InlineData("JobIconUnknown", null)]
    [InlineData("JobIconSecurityOfficer", null)]
    public void CommandRoleForIcon_NamesOnlyTheHeadJobs(string iconId, string? expected)
    {
        // The icon is the datum a spawned ID card actually carries: upstream writes JobPrototype only
        // from the ID card console, so the icon is what gives a head's blip its own color. The
        // QuarterMaster spelling is upstream's, not a typo.
        PlayerStateUtility.CommandRoleForIcon(iconId).Should().Be(expected);
    }

    [Theory]
    [InlineData("ActionChangelingStore", true)]
    [InlineData("ActionChangelingStasis", true)]
    [InlineData("ActionChangelingStasisWeak", true)]
    [InlineData("actionchangelingstore", true)]
    [InlineData("ActionDragonsBreath", false)]
    [InlineData("ActionChangelingScreech", false)]
    [InlineData("ActionTogglePhaseCloak", false)]
    public void HasChangelingAction_MatchesOnlySpawnGrantedChangelingActions(string action, bool expected)
    {
        // Only the actions every changeling body or slug is granted on assignment count. Screech is
        // deliberately excluded: the changeling horror grants it too, and tagging a mob as a player
        // antagonist would be worse than missing it.
        RobustaSyndicateDetectorSystem
            .HasChangelingAction([new EntProtoId(action)])
            .Should()
            .Be(expected);
    }
    [Fact]
    public void IsCloneInBucket_HoldsTheFirstSightingAsTheOriginal()
    {
        var original = new EntityUid(10);
        var clone = new EntityUid(42);
        IReadOnlyList<(EntityUid Uid, TimeSpan? FirstSeen)> pair =
            [(original, TimeSpan.FromMinutes(3)), (clone, TimeSpan.FromMinutes(24))];

        // The original was seen before a clone can exist, so the later sighting is the clone.
        RobustaSyndicateDetectorSystem.IsCloneInBucket(clone, pair, TimeSpan.FromMinutes(20))
            .Should().BeTrue();
        RobustaSyndicateDetectorSystem.IsCloneInBucket(original, pair, TimeSpan.FromMinutes(20))
            .Should().BeFalse();

        // A lone entity is nobody's duplicate.
        RobustaSyndicateDetectorSystem
            .IsCloneInBucket(clone, [(clone, TimeSpan.FromMinutes(3))], TimeSpan.FromMinutes(20))
            .Should().BeFalse();
    }

    [Fact]
    public void IsCloneInBucket_HedgesEveryoneFirstSeenAfterTheCloneWindow()
    {
        // Nothing in the bucket predates the point where a paradox clone can exist, so the earlier
        // sighting is not provably the original either.
        var original = new EntityUid(10);
        var clone = new EntityUid(42);
        var late = TimeSpan.FromMinutes(31);
        IReadOnlyList<(EntityUid Uid, TimeSpan? FirstSeen)> pair = [(original, late), (clone, late)];

        RobustaSyndicateDetectorSystem.IsCloneInBucket(original, pair, TimeSpan.FromMinutes(20))
            .Should().BeTrue();
        RobustaSyndicateDetectorSystem.IsCloneInBucket(clone, pair, TimeSpan.FromMinutes(20))
            .Should().BeTrue();
    }

    [Fact]
    public void IsCloneInBucket_FallsBackToCreationOrderForOneFrame()
    {
        // The radar scans everything in range in one pass, so both sightings can carry one stamp.
        var original = new EntityUid(10);
        var clone = new EntityUid(42);
        var sameFrame = TimeSpan.FromMinutes(4);
        IReadOnlyList<(EntityUid Uid, TimeSpan? FirstSeen)> pair =
            [(clone, sameFrame), (original, sameFrame)];

        RobustaSyndicateDetectorSystem.IsCloneInBucket(clone, pair, TimeSpan.FromMinutes(20))
            .Should().BeTrue();
        RobustaSyndicateDetectorSystem.IsCloneInBucket(original, pair, TimeSpan.FromMinutes(20))
            .Should().BeFalse();
    }

    [Fact]
    public void DisplayMarker_SelectsTheFlagsWorthListing()
    {
        var displayed = PlayerStateUtility.DefaultFlagDefinitions()
            .Where(d => d.Display)
            .Select(d => d.Flag)
            .ToList();

        displayed.Should().Contain(PlayerFlag.HasNoSlips, "No Slips is the marker's reference case");
        displayed.Should().NotContain(PlayerFlag.HasMind, "every player has a mind, so the row is noise");
    }

    [Fact]
    public void EveryDefinition_HasAtLeastOneCheck()
    {
        foreach (var definition in PlayerStateUtility.DefaultFlagDefinitions())
            definition.Checks.Should().NotBeEmpty($"'{definition.Flag.Id}' would never fire");
    }

    [Fact]
    public void HasMindFlag_UsesTheMindCheck()
    {
        // Player control is a flag like any other, so callers never reach for a raw component test.
        var definition = PlayerStateUtility.DefaultFlagDefinitions()
            .Single(d => d.Flag == PlayerFlag.HasMind);

        definition.Checks.Single().Should().BeOfType<MindCheck>();
    }

    [Fact]
    public void ScopingChecks_WrapTheirInnerCheck()
    {
        var definition = PlayerStateUtility.DefaultFlagDefinitions()
            .Single(d => d.Flag == PlayerFlag.HasNoSlips);

        var equipped = definition.Checks.Single().Should().BeOfType<EquippedCheck>().Subject;
        equipped.Inner.Should().BeOfType<HasComponentCheck>();
        equipped.IncludeHands.Should().BeFalse("NoSlip is relayed through worn inventory only");
    }

    [Fact]
    public void CarriedFlags_ScopeToContainers()
    {
        var definition = PlayerStateUtility.DefaultFlagDefinitions()
            .Single(d => d.Flag == PlayerFlag.HasSyndicateGear);

        definition.Checks.Single().Should().BeOfType<CarriedCheck>();
    }

    [Fact]
    public void StolenGearFlag_CoversBothTheftSeverities()
    {
        // GrandTheft is the obvious tier and Restricted is "belongs to another department", which is
        // what a stolen job tool reads as. The panel's theft list is built from the same array, so a
        // severity added there reaches both without a second edit.
        var definition = PlayerStateUtility.DefaultFlagDefinitions()
            .Single(d => d.Flag == PlayerFlag.HasStolenGear);

        var severities = definition.Checks
            .Select(check => check.Should().BeOfType<CarriedCheck>().Subject)
            .Select(carried => carried.Inner.Should().BeOfType<ContrabandSeverityCheck>().Subject.Severity)
            .ToList();

        severities.Should().BeEquivalentTo(PlayerStateUtility.TheftSeverities);
    }

    [Fact]
    public void ThiefFlag_ReadsTheStartingGear()
    {
        // Every component that names a thief is SendOnlyToOwner or server-only, so the two prototypes
        // ThiefGear hands out are the only tell another client gets. Both sit in the back slot's
        // storage, so the check has to be carried rather than equipped.
        var definition = PlayerStateUtility.DefaultFlagDefinitions()
            .Single(d => d.Flag == PlayerFlag.IsThief);

        var inner = definition.Checks
            .Select(check => check.Should().BeOfType<CarriedCheck>().Subject.Inner)
            .ToList();

        inner.Should().Contain(new HasComponentCheck("ThiefBeacon"));
        inner.Should().Contain(new ComponentFieldCheck(
            "MetaData", "EntityPrototype.ID", FlagComparison.Equals, PlayerStateUtility.ThiefSatchelPrototype));
    }

    [Fact]
    public void Report_ExposesOnlyTriggeredFlags()
    {
        var report = new PlayerFlagReport([
            new PlayerFlagResult(PlayerFlag.IsSecurity, Category(PlayerFlag.IsSecurity), true,
                [PlayerFlagTrigger.For(FlagTriggerSource.Identity, "JobDepartment", null, "ID card lists Security")]),
            PlayerFlagResult.Miss(PlayerFlag.IsCommand, Category(PlayerFlag.IsCommand))
        ]);

        report.Has(PlayerFlag.IsSecurity).Should().BeTrue();
        report.Has(PlayerFlag.IsCommand).Should().BeFalse();
        report.Triggered.Should().ContainSingle();
        report.Result(PlayerFlag.IsSecurity)!.Triggers.Single().Reason.Should().Be("JobDepartment");
    }

    [Fact]
    public void Report_Has_FindsAFlagAtTheEndOfTheList()
    {
        // The radar reads several flags off one report, so Has runs against a full list. An off-by-one
        // in the index loop would silently drop the last flag.
        var results = PlayerFlag.Defaults
            .Select(flag => flag == PlayerFlag.HasMedicalGear
                ? new PlayerFlagResult(flag, Category(flag), true,
                    [PlayerFlagTrigger.For(FlagTriggerSource.Equipped, "Health analyser")])
                : PlayerFlagResult.Miss(flag, Category(flag)))
            .ToList();

        var report = new PlayerFlagReport(results);

        report.Has(PlayerFlag.HasMedicalGear).Should().BeTrue();
        report.Has(PlayerFlag.HasMind).Should().BeFalse();
    }

    [Fact]
    public void Report_ForCategory_ReturnsOnlyThatBlock()
    {
        var report = new PlayerFlagReport(PlayerFlag.Defaults
            .Select(flag => new PlayerFlagResult(flag, Category(flag), true, []))
            .ToList());

        report.ForCategory(PlayerStateData.Syndicate).Results
            .Should().OnlyContain(result => result.Category == PlayerStateData.Syndicate)
            .And.NotBeEmpty();
    }

    [Fact]
    public void RetainedFlag_KeepsAnsweringAfterTheEvidenceIsGone()
    {
        // The clone, thief and revolutionary rules fire on a tell that can disappear: a rev takes the
        // flash off, an item leaves PVS. The verdict has to survive that for the round.
        var store = new FlagRetentionStore();
        var definition = Definition(PlayerFlag.IsThief);
        var entity = new EntityUid(1);
        var fired = new PlayerFlagResult(PlayerFlag.IsThief, definition.Category, true,
            [PlayerFlagTrigger.For(FlagTriggerSource.Inventory, "Stolen gear")]);

        store.Apply(entity, definition, fired).Triggered.Should().BeTrue();

        var applied = store.Apply(entity, definition, PlayerFlagResult.Miss(PlayerFlag.IsThief, definition.Category));

        applied.Triggered.Should().BeTrue("the verdict is latched for the round");
        applied.Triggers.Should().ContainSingle();
    }

    [Fact]
    public void UnretainedFlag_ForgetsAMiss()
    {
        var store = new FlagRetentionStore();
        var definition = Definition(PlayerFlag.HasWeapon);
        var entity = new EntityUid(1);

        store.Apply(entity, definition, new PlayerFlagResult(PlayerFlag.HasWeapon, definition.Category, true,
            [PlayerFlagTrigger.For(FlagTriggerSource.Equipped, "Baton")]));

        store.Apply(entity, definition, PlayerFlagResult.Miss(PlayerFlag.HasWeapon, definition.Category))
            .Triggered.Should().BeFalse();
    }

    [Fact]
    public void Retention_IsClearedOnRoundRestart()
    {
        var store = new FlagRetentionStore();
        var definition = Definition(PlayerFlag.IsAntagonist);
        var entity = new EntityUid(1);

        store.Apply(entity, definition, new PlayerFlagResult(PlayerFlag.IsAntagonist, definition.Category, true,
            [PlayerFlagTrigger.For(FlagTriggerSource.Component, "HeadRevolutionary")]));

        store.Clear();

        store.Apply(entity, definition, PlayerFlagResult.Miss(PlayerFlag.IsAntagonist, definition.Category))
            .Triggered.Should().BeFalse();
    }

    private static PlayerFlagDefinition Definition(PlayerFlag flag) =>
        PlayerStateUtility.DefaultFlagDefinitions().Single(definition => definition.Flag == flag);

    /// <summary>The category a flag's definition declares.</summary>
    private static PlayerStateData Category(PlayerFlag flag) => Definition(flag).Category;

    [Fact]
    public void Gate_RefusesASecondEntryForTheSameEntity()
    {
        // This is the guard that stops a flag → scanner → flag loop from overflowing the stack, which
        // would kill the client with no catchable exception.
        var gate = new EvaluationGate();
        var entity = new EntityUid(1);

        gate.TryEnter(entity).Should().BeTrue();
        gate.TryEnter(entity).Should().BeFalse("a re-entrant evaluation must not recurse");
    }

    [Fact]
    public void Gate_AllowsTheEntityAgainAfterExit()
    {
        var gate = new EvaluationGate();
        var entity = new EntityUid(1);

        gate.TryEnter(entity).Should().BeTrue();
        gate.Exit(entity);

        gate.TryEnter(entity).Should().BeTrue();
    }

    [Fact]
    public void Gate_TracksEntitiesIndependently()
    {
        var gate = new EvaluationGate();
        var first = new EntityUid(1);
        var second = new EntityUid(2);

        gate.TryEnter(first).Should().BeTrue();
        gate.TryEnter(second).Should().BeTrue();

        gate.Exit(first);
        gate.TryEnter(first).Should().BeTrue();
        gate.TryEnter(second).Should().BeFalse("the second entity is still being evaluated");
    }

    [Fact]
    public void Gate_ClearReleasesEveryEntity()
    {
        var gate = new EvaluationGate();
        var entity = new EntityUid(1);

        gate.TryEnter(entity).Should().BeTrue();
        gate.Clear();

        gate.TryEnter(entity).Should().BeTrue();
    }

    [Fact]
    public void ArmorCoefficients_MultiplyOverlappingPieces()
    {
        // Upstream multiplies the coefficients of everything worn, so 0.7 over 0.5 leaves 35% damage,
        // not 20%.
        var combined = PlayerStateUtility.CombineArmorCoefficients(
        [
            new Dictionary<string, float> { ["Blunt"] = 0.7f },
            new Dictionary<string, float> { ["Blunt"] = 0.5f }
        ]);

        combined["Blunt"].Should().BeApproximately(0.35f, 1e-5f);
    }

    [Fact]
    public void ArmorCoefficients_KeepTheFirstValueForASinglePiece()
    {
        var combined = PlayerStateUtility.CombineArmorCoefficients(
        [
            new Dictionary<string, float> { ["Heat"] = 0.8f }
        ]);

        combined.Should().ContainSingle().Which.Value.Should().BeApproximately(0.8f, 1e-5f);
    }

    [Fact]
    public void Miss_CarriesNoEvidence()
    {
        var miss = PlayerFlagResult.Miss(PlayerFlag.HasWeapon, Category(PlayerFlag.HasWeapon));

        miss.Triggered.Should().BeFalse();
        miss.Triggers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ToyFigurineSyndicate", true)]
    [InlineData("PlushieSyndicate", true)]
    [InlineData("LighterSyndicate", true)]
    [InlineData("ClothingUniformJumpsuitPyjamaSyndicateRed", true)]
    [InlineData("ClothingNeckScarfStripedSyndieGreen", true)]
    [InlineData("ClothingNeckScarfStripedSyndieRed", true)]
    [InlineData("ClothingOuterWinterSyndie", true)]
    [InlineData("ClothingOuterWinterSyndieCap", true)]
    [InlineData("ClothingOuterCoatSyndieCap", true)]
    [InlineData("ClothingShoesBootsWinterSyndicate", true)]
    [InlineData("ClothingUniformJumpsuitSyndieFormal", true)]
    [InlineData("ClothingUniformJumpskirtSyndieFormalDress", true)]
    [InlineData("ClothingUniformJumpsuitOperative", true)]
    [InlineData("ClothingUniformJumpsuitRecruitSyndie", true)]
    [InlineData("ClothingHeadHatSyndie", true)]
    [InlineData("ClothingHeadHatCatEars", true)]
    // An armoured variant of the same garment is real protection, so it stays gear.
    [InlineData("ClothingOuterWinterSyndieCapArmored", false)]
    [InlineData("ClothingOuterCoatSyndieCapArmored", false)]
    // Chameleon clothing is a disguise ability, so a uniform token must not swallow it.
    [InlineData("ClothingUniformJumpsuitChameleon", false)]
    [InlineData("ClothingNeckChameleon", false)]
    [InlineData("ClothingOuterHardsuitSyndie", false)]
    [InlineData("ClothingShoesBootsMagSyndie", false)]
    [InlineData("ClothingEyesHudSyndicate", false)]
    [InlineData("Emag", false)]
    [InlineData("EnergySword", false)]
    [InlineData("C4", false)]
    public void SyndicateGearExclusions_RejectFlavourAndKeepGear(string prototypeId, bool excluded)
    {
        RobustaSyndicateDetectorSystem.IsExcludedSyndicateGear(prototypeId).Should().Be(excluded);
    }

    [Fact]
    public void AllowedByContraband_ClearsTheOwningDepartment()
    {
        // A medical encryption key is restricted for others but not for the department that owns it.
        PlayerStateUtility.IsAllowedBy(
            Allowed("Medical"),
            [],
            Departments("Medical"),
            "Paramedic").Should().BeTrue();
    }

    [Fact]
    public void AllowedByContraband_FlagsAnotherDepartment()
    {
        PlayerStateUtility.IsAllowedBy(
            Allowed("Medical"),
            [],
            Departments("Cargo"),
            "Cargo Technician").Should().BeFalse();
    }

    [Fact]
    public void AllowedByContraband_ClearsTheOwningJob()
    {
        // A spawned card carries no job prototype, so the job is matched by its localized name.
        PlayerStateUtility.IsAllowedBy(
            Allowed(),
            ["Janitor"],
            Departments("Service"),
            "Janitor").Should().BeTrue();
    }

    [Fact]
    public void AllowedByContraband_FlagsAnItemWithNoAllowList()
    {
        // Fully restricted: no ID clears it.
        PlayerStateUtility.IsAllowedBy(
            Allowed(),
            [],
            Departments("Medical"),
            "Paramedic").Should().BeFalse();
    }

    private static HashSet<ProtoId<DepartmentPrototype>> Allowed(params string[] ids) =>
        ids.Select(id => new ProtoId<DepartmentPrototype>(id)).ToHashSet();

    private static List<ProtoId<DepartmentPrototype>> Departments(params string[] ids) =>
        ids.Select(id => new ProtoId<DepartmentPrototype>(id)).ToList();
}

/// <summary>
/// The comparison and coercion layer is the only part of the flag machinery reachable without a live
/// client, so it carries the coverage. Everything above it needs an ECS.
/// </summary>
public class FlagValueComparerTests
{
    [Fact]
    public void FixedPoint_ComparesNumerically()
    {
        FlagValueComparer.Matches(FixedPoint2.New(14.5f), FlagComparison.GreaterThan, 14f)
            .Should().BeTrue();
    }

    [Fact]
    public void FixedPoint_BelowThreshold_DoesNotMatch()
    {
        FlagValueComparer.Matches(FixedPoint2.New(10f), FlagComparison.GreaterThan, 14f)
            .Should().BeFalse();
    }

    [Fact]
    public void Enum_MatchesByNameIgnoringCase()
    {
        FlagValueComparer.Matches(FlagComparison.GreaterThan, FlagComparison.Equals, "greaterthan")
            .Should().BeTrue();
    }

    [Fact]
    public void ProtoId_MatchesByItsIdentifier()
    {
        var severity = new ProtoId<ContrabandSeverityPrototype>("GrandTheft");

        FlagValueComparer.Matches(severity, FlagComparison.Equals, "GrandTheft").Should().BeTrue();
        FlagValueComparer.Matches(severity, FlagComparison.Equals, "Minor").Should().BeFalse();
    }

    [Fact]
    public void LocId_MatchesByItsIdentifier()
    {
        FlagValueComparer.Matches(new LocId("foo"), FlagComparison.Equals, "foo").Should().BeTrue();
    }

    [Fact]
    public void Collection_ContainsMatchesAnElement()
    {
        var departments = new List<ProtoId<DepartmentPrototype>>
        {
            new("Command"),
            new("Security")
        };

        FlagValueComparer.Matches(departments, FlagComparison.Contains, "Command").Should().BeTrue();
        FlagValueComparer.Matches(departments, FlagComparison.Contains, "Cargo").Should().BeFalse();
    }

    [Fact]
    public void Nullable_IsNullMatches()
    {
        FlagValueComparer.Matches(null, FlagComparison.IsNull, null).Should().BeTrue();
        FlagValueComparer.Matches("x", FlagComparison.IsNull, null).Should().BeFalse();
    }

    [Fact]
    public void Numeric_CrossTypeEqualityHolds()
    {
        FlagValueComparer.Matches(5, FlagComparison.Equals, 5.0f).Should().BeTrue();
    }

    [Fact]
    public void Bool_MatchesAgainstString()
    {
        FlagValueComparer.Matches(true, FlagComparison.Equals, "true").Should().BeTrue();
        FlagValueComparer.Matches(false, FlagComparison.Equals, "true").Should().BeFalse();
    }

    [Fact]
    public void NotEquals_InvertsEquality()
    {
        FlagValueComparer.Matches("a", FlagComparison.NotEquals, "b").Should().BeTrue();
    }
}
