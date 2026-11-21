using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Shared.Changeling.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Mind.Components;
using Content.Shared.Mindshield.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Security;
using Content.Shared.StationRecords;
using Content.Shared.StationRecords.Components;
using Content.Shared.Store;
using Content.Shared.Weapons.Melee.Components;
using FluentAssertions;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using RobustoClient.Reflection;
using RobustoClient.Systems;
using Xunit;

namespace RobustoClient.Tests.Systems;

/// <summary>
/// The pure rules behind the radar tags: the gear heuristics behind the hedged antagonist kinds, the
/// criminal statuses and emag flags that raise a tag, and the container rule behind the inventory
/// window. None of it needs a client.
/// </summary>
public class DetectorTests
{
    [Theory]
    [InlineData(SecurityStatus.Wanted, true)]
    [InlineData(SecurityStatus.Hostile, true)]
    // Suspected is upstream's weaker hedge, and the rest are not a threat at all.
    [InlineData(SecurityStatus.Suspected, false)]
    [InlineData(SecurityStatus.None, false)]
    [InlineData(SecurityStatus.Detained, false)]
    [InlineData(SecurityStatus.Paroled, false)]
    [InlineData(SecurityStatus.Discharged, false)]
    [InlineData(SecurityStatus.Eliminated, false)]
    public void IsWantedStatus_CoversWantedAndHostileOnly(SecurityStatus status, bool expected)
    {
        RobustaSyndicateDetectorSystem.IsWantedStatus(status).Should().Be(expected);
    }

    [Theory]
    // An emag item defaults to Interaction, which is what the silicon law hook requires.
    [InlineData(EmagType.Interaction, true)]
    [InlineData(EmagType.All, true)]
    [InlineData(EmagType.Interaction | EmagType.Access, true)]
    // Access-only is an airlock or a console, which says nothing about laws.
    [InlineData(EmagType.Access, false)]
    [InlineData(EmagType.None, false)]
    public void IsEmaggedFlag_NeedsTheInteractionFlag(EmagType type, bool expected)
    {
        RobustaSyndicateDetectorSystem.IsEmaggedFlag(type).Should().Be(expected);
    }

    [Theory]
    // The slot a fixed list used to miss, so the detector reported items the window never showed.
    [InlineData("jumpsuit", true)]
    [InlineData("belt", true)]
    [InlineData("back", true)]
    [InlineData("pocket1", true)]
    // A downstream build's extra slot is listed without any code change.
    [InlineData("gloves_of_holding", true)]
    // The action bar holds button entities, not possessions.
    [InlineData("actions", false)]
    [InlineData("ACTIONS", false)]
    [InlineData("Actions", false)]
    public void ShouldListContainer_CoversSlotsAndRejectsTheActionBar(string containerId, bool expected)
    {
        RobustaInventoryViewerSystem.ShouldListContainer(containerId).Should().Be(expected);
    }

    [Fact]
    public void HeadRevGear_NeedsBothTheFlashAndTheSunglasses()
    {
        // HeadRevGear puts both in the back slot, so either alone is ordinary crew kit.
        RobustaSyndicateDetectorSystem
            .ContainsAllPrototypes(["Flash", "ClothingEyesGlassesSunglasses"], ["Flash", "ClothingEyesGlassesSunglasses"])
            .Should()
            .BeTrue();

        RobustaSyndicateDetectorSystem
            .ContainsAllPrototypes(["Flash"], ["Flash", "ClothingEyesGlassesSunglasses"])
            .Should()
            .BeFalse();

        RobustaSyndicateDetectorSystem
            .ContainsAllPrototypes(["ClothingEyesGlassesSunglasses"], ["Flash", "ClothingEyesGlassesSunglasses"])
            .Should()
            .BeFalse();
    }

    [Fact]
    public void HeadRevGear_IsIgnoredOnAMindshieldedMob()
    {
        // The server cancels a conversion on a mindshielded target, so the gear alone must not name one.
        RobustaSyndicateDetectorSystem.ShouldClassifyHeadRev(hasGear: true, mindshielded: true)
            .Should()
            .BeFalse();

        RobustaSyndicateDetectorSystem.ShouldClassifyHeadRev(hasGear: true, mindshielded: false)
            .Should()
            .BeTrue();

        RobustaSyndicateDetectorSystem.ShouldClassifyHeadRev(hasGear: false, mindshielded: false)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Detector_ReadsMindshieldByNameRatherThanDependency()
    {
        // A [Dependency] field of a type absent on older builds throws TypeLoadException during IoC
        // injection, taking the client down before it can connect.
        var offenders = typeof(RobustaSyndicateDetectorSystem)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.GetCustomAttribute<DependencyAttribute>() != null)
            .Where(field => field.FieldType.FullName?.Contains("MindShield", StringComparison.OrdinalIgnoreCase) == true)
            .Select(field => field.Name);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Detector_DependsOnNoUpstreamSystemThatWasRenamed()
    {
        // A renamed system fails the same way: upstream ships SharedStationSystem and
        // SharedStationRecordsSystem where this code once injected StationSystem and
        // StationRecordsSystem. Reading the components keeps the detector portable.
        var renamed = new[] { "StationSystem", "StationRecordsSystem" };

        var offenders = typeof(RobustaSyndicateDetectorSystem)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.GetCustomAttribute<DependencyAttribute>() != null)
            .Where(field => renamed.Contains(field.FieldType.Name, StringComparer.Ordinal))
            .Select(field => field.Name);

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void MindshieldAndLifeState_AreReadThroughStableMembers()
    {
        // Goob defines HasMind and CurrentState as properties where upstream has fields, so a field
        // load throws MissingFieldException. Mind is a field on both builds, and the mob-state system
        // owns the state comparison.
        typeof(MindContainerComponent)
            .GetField("Mind", BindingFlags.Instance | BindingFlags.Public)
            .Should()
            .NotBeNull();

        typeof(MobStateSystem).GetMethod("IsDead").Should().NotBeNull();
        typeof(MobStateSystem).GetMethod("IsCritical").Should().NotBeNull();
    }

    [Fact]
    public void ComponentNames_MatchTheRegisteredNames()
    {
        // Registration names drop the "Component" suffix (ComponentFactory.CalculateComponentName).
        // A name that keeps it never matches, so the feature quietly stops working on every build.
        static string Registered(string typeName) => typeName.EndsWith("Component", StringComparison.Ordinal)
            ? typeName[..^"Component".Length]
            : typeName;

        RobustaSyndicateDetectorSystem.MindShieldStatusComponentName
            .Should().Be(Registered(nameof(MindShieldStatusComponent)));

        RobustaSyndicateDetectorSystem.MindShieldComponentName
            .Should().Be(Registered(nameof(MindShieldComponent)));

        RobustaSyndicateDetectorSystem.StationRecordsComponentName
            .Should().Be(Registered(nameof(StationRecordsComponent)));

        // The species component is whichever of the two the running build has.
        RobustaSyndicateDetectorSystem.SpeciesComponentNames
            .Should().BeEquivalentTo(
                [Registered("HumanoidProfileComponent"), Registered("HumanoidAppearanceComponent")]);
    }

    [Fact]
    public void ReflectionPaths_ResolveOnThisUpstreamBuild()
    {
        // The Goob paths are fallbacks. These are the primary paths, and they must keep working on this
        // repository's own submodule.
        var status = new MindShieldStatusComponent { IsMindshielded = true };
        RobustaSyndicateDetectorSystem.OptionalMember.Should().NotBeNull();
        Reflect.TryGet(status, "IsMindshielded", out var shielded, RobustaSyndicateDetectorSystem.OptionalMember)
            .Should().BeTrue();
        (shielded is true).Should().BeTrue();

        var profile = new HumanoidProfileComponent { Species = "Skeleton" };
        Reflect.TryGet(profile, "Species", out var species, RobustaSyndicateDetectorSystem.OptionalMember)
            .Should().BeTrue();
        ReflectConvert.TryGetId(species).Should().Be(RobustaSyndicateDetectorSystem.SkeletonSpecies);

        // The record set is read off the component, whose namespace differs from Goob's.
        var records = new StationRecordsComponent();
        (Reflect.Get(records, "Records") is StationRecordSet).Should().BeTrue();
    }

    [Fact]
    public void UpstreamOnlyComponents_AreResolvedByName()
    {
        // These two exist only on this repository's upstream, not on Goob. Naming the type throws
        // TypeLoadException there, so the detector resolves them by registration name.
        RobustaSyndicateDetectorSystem.ChangelingHorrorComponentName
            .Should().Be("ChangelingHorror");

        RobustaSyndicateDetectorSystem.MeleeBatteryHitsLeftComponentName
            .Should().Be("MeleeBatteryHitsLeft");

        // The members read off them must be reachable reflectively on this build.
        var horror = new ChangelingHorrorComponent();
        Reflect.TryGet(horror, "EndTime", out var endTime, RobustaSyndicateDetectorSystem.OptionalMember)
            .Should().BeTrue();
        endTime.Should().BeOfType<TimeSpan>();

        var hits = new MeleeBatteryHitsLeftComponent { HitPowerCost = 5f };
        Reflect.TryGet(hits, "HitPowerCost", out var cost, RobustaSyndicateDetectorSystem.OptionalMember)
            .Should().BeTrue();
        cost.Should().Be(5f);
    }

    [Theory]
    [InlineData("ClothingBeltWand", true)]
    [InlineData("WizardTeleportScroll", true)]
    [InlineData("WizardPDA", true)]
    [InlineData("ClothingHeadsetAltWizard", true)]
    // The fake hat and robe are costume items anyone can wear, so they must not count.
    [InlineData("ClothingHeadHatWizardFake", false)]
    [InlineData("ClothingOuterWizardFake", false)]
    [InlineData("ClothingHeadHatWizardBlue", false)]
    public void WizardItems_MatchOnlyTheUnspoofableGear(string prototype, bool expected)
    {
        RobustaSyndicateDetectorSystem
            .ContainsAnyPrototype([prototype], ["WizardsGrimoire", "ClothingBeltWand", "WizardTeleportScroll", "WizardPDA", "ClothingHeadsetAltWizard"])
            .Should()
            .Be(expected);
    }

    [Theory]
    [InlineData("ActionFireball", true)]
    [InlineData("actionblink", true)]
    [InlineData("ActionMindSwap", true)]
    [InlineData("ActionChangelingStore", false)]
    [InlineData("ActionTogglePhaseCloak", false)]
    public void HasWizardAction_MatchesOnlySpellbookSpells(string action, bool expected)
    {
        RobustaSyndicateDetectorSystem
            .HasWizardAction([new EntProtoId(action)])
            .Should()
            .Be(expected);

        RobustaSyndicateDetectorSystem.IsWizardAction(action).Should().Be(expected);
    }

    [Theory]
    // upstream grants these on assignment.
    [InlineData("ActionChangelingStore", true)]
    [InlineData("ActionChangelingStasis", true)]
    [InlineData("ActionChangelingStasisWeak", true)]
    [InlineData("actionchangelingstore", true)]
    // [GOOB] GoobStation's changeling grants these instead.
    [InlineData("ActionChangelingTransform", true)]
    [InlineData("ActionChangelingTransformCycle", true)]
    // A wizard spell is not a changeling action.
    [InlineData("ActionFireball", false)]
    // Screech is deliberately excluded: the horror form grants it too.
    [InlineData("ActionChangelingScreech", false)]
    [InlineData("ActionDragonsBreath", false)]
    [InlineData("ActionTogglePhaseCloak", false)]
    public void HasChangelingAction_MatchesBothBuilds(string action, bool expected)
    {
        RobustaSyndicateDetectorSystem
            .HasChangelingAction([new EntProtoId(action)])
            .Should()
            .Be(expected);
    }

    [Fact]
    public void ChangelingMarkers_AreResolvedByNameOnBothBuilds()
    {
        // Our upstream has no ChangelingComponent and Goob has no ChangelingSlugComponent, so neither
        // may be bound as a type: a direct HasComp<T> throws TypeLoadException on the build without it.
        RobustaSyndicateDetectorSystem.GoobChangelingComponentName.Should().Be("Changeling");
        RobustaSyndicateDetectorSystem.GoobChangelingIdentityComponentName.Should().Be("ChangelingIdentity");
        RobustaSyndicateDetectorSystem.ChangelingSlugComponentName.Should().Be("ChangelingSlug");

        // The detector must not name either type directly.
        var offenders = typeof(RobustaSyndicateDetectorSystem)
            .Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("RobustoClient", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetMethodBody()?.LocalVariables.Cast<LocalVariableInfo>() ?? [])
            .Select(local => local.LocalType)
            .Where(type => type.Name is "ChangelingComponent" or "ChangelingSlugComponent"
                           or "ChangelingIdentityComponent")
            .Select(type => type.FullName);

        offenders.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AntagonistKind.HeadRevolutionary, true)]
    [InlineData(AntagonistKind.Revolutionary, true)]
    // A clone is only the later half of a shared-forensics pair.
    [InlineData(AntagonistKind.Clone, true)]
    // A wizard is confirmed by a granted spell, so the kind itself is never hedged, gear-only
    // detection sets AntagonistData.Hedged instead.
    [InlineData(AntagonistKind.Wizard, false)]
    [InlineData(AntagonistKind.Ninja, false)]
    [InlineData(AntagonistKind.Zombie, false)]
    [InlineData(AntagonistKind.InitialInfected, false)]
    // A carp is a certainty too: the faction that names it is authored.
    [InlineData(AntagonistKind.Carp, false)]
    public void IsHedged_OnlyForKindsThatAreAGuess(AntagonistKind kind, bool expected)
    {
        kind.IsHedged().Should().Be(expected);
    }

    [Fact]
    public void DragonSide_PrefersTheDragonOverItsOwnFaction()
    {
        // Every carp carries the Dragon faction, so the dragon itself has to win, or it ends up
        // labelled as its own brood.
        RobustaSyndicateDetectorSystem.ClassifyDragonSide(isDragon: true, inDragonFaction: true)
            .Should().Be(AntagonistKind.Dragon);
    }

    [Fact]
    public void DragonSide_NamesAMinionAsACarp()
    {
        RobustaSyndicateDetectorSystem.ClassifyDragonSide(isDragon: false, inDragonFaction: true)
            .Should().Be(AntagonistKind.Carp);
    }

    [Fact]
    public void DragonSide_LeavesEverythingElseAlone()
    {
        RobustaSyndicateDetectorSystem.ClassifyDragonSide(isDragon: false, inDragonFaction: false)
            .Should().BeNull();
    }

    [Fact]
    public void HeldCurrencies_ReadsPositiveBalancesOnly()
    {
        var balance = new Dictionary<ProtoId<CurrencyPrototype>, FixedPoint2>
        {
            [new ProtoId<CurrencyPrototype>("Telecrystal")] = FixedPoint2.New(20),
            [new ProtoId<CurrencyPrototype>("WizCoin")] = FixedPoint2.Zero
        };

        RobustaSyndicateDetectorSystem.HeldCurrencies(balance).Should().BeEquivalentTo("Telecrystal");
    }

    [Fact]
    public void HeldCurrencies_ReadsAFixedPointBalance()
    {
        // The balance is FixedPoint2, which lives in a different assembly on Goob, so it is read
        // numerically rather than through a typed member.
        var balance = new Dictionary<string, FixedPoint2> { ["Telecrystal"] = FixedPoint2.New(0.5f) };

        RobustaSyndicateDetectorSystem.HeldCurrencies(balance).Should().BeEquivalentTo("Telecrystal");
    }

    [Fact]
    public void HeldCurrencies_IgnoresAnUnreadableValue()
    {
        var balance = new Dictionary<string, object> { ["Telecrystal"] = "twelve" };

        RobustaSyndicateDetectorSystem.HeldCurrencies(balance).Should().BeEmpty();
    }
}
