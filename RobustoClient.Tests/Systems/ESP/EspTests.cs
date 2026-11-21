using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Robust.Shared.Maths;
using RobustoClient.Systems;
using RobustoClient.Systems.ESP;
using RobustoClient.Systems.PlayerState;
using Xunit;

namespace RobustoClient.Tests.Systems.ESP;

/// <summary>
/// The <c>feature</c> key in esp.yml is what maps a category to a menu toggle, and it has to keep
/// working for files written before the key existed.
/// </summary>
public class EspRegistryTests
{

    [Fact]
    public void EspRegistry_MobCategoryUsesTheRuleTheOverlayRegisters()
    {
        // The Mobs category is the only one whose condition lives in code rather than in a component
        // name, so a rename on either side would silently stop every non-player mob from matching.
        // LoadFromString keeps the test off the per-machine esp.yml the constructor reads.
        var registry = new EspRegistry();
        registry.LoadFromString(EspRegistry.DefaultConfig);

        var mob = registry.Categories.Single(c => c.Id == "MobName");

        mob.Feature.Should().Be(EspFeature.Mobs);
        mob.Conditions.CustomRule.Should().Equal(RobustaEspOverlay.NonPlayerMobRule);
    }

    [Fact]
    public void EspRegistry_DefaultConfigIsStampedWithTheCurrentVersion()
    {
        // An unstamped file is rewritten on load, so the stamp has to be in the shipped default or
        // every launch would overwrite it. Derived from the constant so a version bump cannot leave
        // the two out of step.
        EspRegistry.DefaultConfig.Should().Contain($"# rob-esp-version: {EspRegistry.ConfigVersion}");
    }

    [Fact]
    public void EspRegistry_CommandAndSecurityCategoriesUseTheFlagRules()
    {
        // Both categories moved off job-title substrings onto the player flag rules, so a renamed job
        // title no longer decides whether someone reads as command or security. A malformed entry is
        // skipped silently by the parser, which is why this asserts on the parsed result.
        var registry = new EspRegistry();
        registry.LoadFromString(EspRegistry.DefaultConfig);

        var command = registry.Categories.Single(c => c.Id == "JobCommand");
        command.Conditions.CustomRule.Should().Equal("IsCommand");
        command.Feature.Should().Be(EspFeature.Players);

        var security = registry.Categories.Single(c => c.Id == "JobSecurity");
        security.Conditions.CustomRule.Should().Equal("IsSecurity");
        security.Conditions.JobContains.Should().BeEmpty();
    }

    [Fact]
    public void EspRegistry_ThiefCategoryUsesTheLatchRule()
    {
        // The thief's gear is gone long before the round ends, so the category reads the detector's
        // latch rather than a component on the mob. A rename on either side would silently stop it.
        var registry = new EspRegistry();
        registry.LoadFromString(EspRegistry.DefaultConfig);

        var thief = registry.Categories.Single(c => c.Id == "ThiefTag");

        thief.Conditions.CustomRule.Should().Equal("IsThief");
        thief.Feature.Should().Be(EspFeature.Syndicate);
    }

    [Fact]
    public void EspRegistry_ParsesFeatureKey()
    {
        // Feature mapping is what connects a YAML category to a menu toggle.
        const string yaml = """
            - id: TestTag
              priority: 50
              color: "#FF0000"
              textFormat: "TEST"
              feature: Syndicate
              dock: Right
              conditions:
                customRule: [ "IsAntag" ]

            - id: Defaulted
              priority: 10
              color: "#FFFFFF"
              textFormat: "X"
              dock: Top
              conditions:
                customRule: [ "IsAlive" ]
            """;

        var registry = new EspRegistry();
        registry.LoadFromString(yaml);

        var syndicate = registry.Categories.Single(c => c.Id == "TestTag");
        syndicate.Feature.Should().Be(EspFeature.Syndicate);

        // A category with no feature key belongs to players, so older esp.yml files keep working.
        registry.Categories.Single(c => c.Id == "Defaulted").Feature.Should().Be(EspFeature.Players);
    }

    [Fact]
    public void EspRegistry_UnknownFeatureFallsBackToPlayers()
    {
        const string yaml = """
            - id: Bogus
              priority: 10
              color: "#FFFFFF"
              textFormat: "X"
              feature: NotAFeature
              dock: Top
              conditions:
                customRule: [ "IsAlive" ]
            """;

        var registry = new EspRegistry();
        registry.LoadFromString(yaml);

        registry.Categories.Single(c => c.Id == "Bogus").Feature.Should().Be(EspFeature.Players);
    }
}


/// <summary>
/// Per-feature ESP settings are the persistence layer behind the Visuals tab rows, so their defaults,
/// color fallback and JSON round-trip are all pinned here.
/// </summary>
public class EspSettingsTests
{
    [Fact]
    public void EveryFeature_DefaultsToEnabled()
    {
        var settings = new RobustaConfig.EspSettings();

        foreach (var feature in Enum.GetValues<EspFeature>())
            settings.IsEnabled(feature).Should().BeTrue($"{feature} should start enabled");
    }

    [Fact]
    public void For_EveryEnumMember_MapsToItsOwnConfig()
    {
        // A fallback arm would silently route a new feature to another feature's switch, so every
        // enum member must resolve to a distinct instance.
        var settings = new RobustaConfig.EspSettings();

        var configs = Enum.GetValues<EspFeature>().Select(settings.For).ToList();

        configs.Should().NotContainNulls("every enum member needs a settings property");
        configs.Should().OnlyHaveUniqueItems("a fallback would reuse another feature's config");
    }

    [Fact]
    public void For_UnmappedFeature_ReturnsNoConfigAndIsNotDrawn()
    {
        // An out-of-range value can only come from a cast or a drifted enum. It must not borrow the
        // Players switch, and the overlay must skip it.
        var settings = new RobustaConfig.EspSettings();
        var unmapped = (EspFeature)999;
        var fallback = new Color(0f, 1f, 0f);

        settings.For(unmapped).Should().BeNull();
        settings.IsEnabled(unmapped).Should().BeFalse("an unmapped feature must not be drawn");
        settings.ResolveColor(unmapped, fallback).Should().Be(fallback);
    }

    [Fact]
    public void AnyEnabled_TracksTheFeatures()
    {
        var settings = new RobustaConfig.EspSettings();
        settings.AnyEnabled.Should().BeTrue();

        foreach (var feature in Enum.GetValues<EspFeature>())
        {
            var config = settings.For(feature);
            config.Should().NotBeNull();
            config!.Enabled = false;
        }

        settings.AnyEnabled.Should().BeFalse("every feature is off, so the overlay can skip drawing");
    }

    [Fact]
    public void ResolveColor_UsesFallbackWhenOverrideIsEmpty()
    {
        var settings = new RobustaConfig.EspSettings();
        var fallback = new Color(1f, 0f, 0f);

        settings.ResolveColor(EspFeature.Syndicate, fallback).Should().Be(fallback);
    }

    [Fact]
    public void ResolveColor_ParsesHexOverride()
    {
        var settings = new RobustaConfig.EspSettings();
        settings.Syndicate.Color = "#00FF00";

        var resolved = settings.ResolveColor(EspFeature.Syndicate, Color.White);

        resolved.R.Should().BeApproximately(0f, 0.01f);
        resolved.G.Should().BeApproximately(1f, 0.01f);
        resolved.B.Should().BeApproximately(0f, 0.01f);
    }

    [Theory]
    [InlineData("not-a-color")]
    [InlineData("#")]
    [InlineData("")]
    public void ResolveColor_FallsBackOnUnusableOverride(string hex)
    {
        // A hand-edited config must not produce a black or thrown result.
        var settings = new RobustaConfig.EspSettings();
        settings.Players.Color = hex;
        var fallback = new Color(0f, 1f, 0f);

        settings.ResolveColor(EspFeature.Players, fallback).Should().Be(fallback);
    }

    [Fact]
    public void ResolveColor_ReusesTheParseUntilTheOverrideChanges()
    {
        // The overlay asks once per result per entity per frame, so the parse is memoised on the raw
        // string. A stale entry would paint every blip the old color after an edit.
        var settings = new RobustaConfig.EspSettings();
        settings.Syndicate.Color = "#00FF00";

        var first = settings.ResolveColor(EspFeature.Syndicate, Color.White);
        settings.ResolveColor(EspFeature.Syndicate, Color.White).Should().Be(first);

        settings.Syndicate.Color = "#0000FF";
        var edited = settings.ResolveColor(EspFeature.Syndicate, Color.White);

        edited.B.Should().BeApproximately(1f, 0.01f);
        edited.G.Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public void TheftExemption_CoversTheSeededCrewMonitorCase()
    {
        // A handheld crew monitor is CMO gear, so a paramedic carrying one is doing their job rather
        // than stealing.
        PlayerStateUtility.IsTheftExempt("HandheldCrewMonitor", "Paramedic").Should().BeTrue();
        PlayerStateUtility.IsTheftExempt("HandheldCrewMonitor", "Chief Medical Officer").Should().BeTrue();
        PlayerStateUtility.IsTheftExempt("HandheldCrewMonitor", "Passenger").Should().BeFalse();
    }

    [Fact]
    public void TheftExemption_MatchesThePrototypeFragmentAndIgnoresCase()
    {
        PlayerStateUtility.IsTheftExempt("HandheldCrewMonitorEmpty", "paramedic").Should().BeTrue();
        PlayerStateUtility.IsTheftExempt("Hypospray", "Paramedic").Should().BeFalse();
    }

    [Fact]
    public void TheftExemption_NeedsBothAPrototypeAndAJob()
    {
        // An empty title is what an entity with no ID card reports.
        PlayerStateUtility.IsTheftExempt("HandheldCrewMonitor", "").Should().BeFalse();
        PlayerStateUtility.IsTheftExempt("", "Paramedic").Should().BeFalse();
    }

    [Fact]
    public void Settings_RoundTripThroughJson()
    {
        // Guards the properties-versus-fields trap: System.Text.Json ignores public fields, so a
        // field-based settings type would persist as an empty object and silently lose every toggle.
        var settings = new RobustaConfig.EspSettings();
        settings.Mobs.Enabled = false;
        settings.Syndicate.Color = "#123456";

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<RobustaConfig.EspSettings>(json)!;

        // AnyEnabled is derived, not stored: a value written for it would be a picker that does nothing.
        json.Should().NotContain(nameof(RobustaConfig.EspSettings.AnyEnabled));

        restored.Mobs.Enabled.Should().BeFalse();
        restored.Syndicate.Color.Should().Be("#123456");
        restored.Players.Enabled.Should().BeTrue();
    }
}

/// <summary>
/// The text path runs per matching category per entity per frame, so the token strings are built once
/// at registration rather than per evaluation.
/// </summary>
public class EspEvaluatorTests
{
    [Fact]
    public void ResolveText_LeavesAFormatWithNoTokenAlone()
    {
        // Most shipped formats carry no token, and the scan is skipped entirely for them.
        var evaluator = new EspEvaluator();
        evaluator.RegisterVariable("PlayerName", _ => "@someone");

        evaluator.ResolveText(default, "  SECURITY  ").Should().Be("SECURITY");
    }

    [Fact]
    public void ResolveText_SubstitutesEveryRegisteredToken()
    {
        var evaluator = new EspEvaluator();
        evaluator.RegisterVariable("PlayerName", _ => "@someone");
        evaluator.RegisterVariable("JobTitle", _ => "Captain");

        evaluator.ResolveText(default, "{PlayerName} ({JobTitle})").Should().Be("@someone (Captain)");
    }
}

/// <summary>
/// The category index is what keeps a viewport full of loose items from paying for every mob rule.
/// Its contract is that a gate is only ever a condition the category cannot match without, so a
/// wrong entry here silently drops a rule rather than merely slowing it down.
/// </summary>
public class EspCategoryIndexTests
{
    private static readonly Dictionary<string, string> NoGates = new();

    private static EspCategoryDefinition Category(
        string id,
        string[]? hasComponent = null,
        string[]? jobContains = null,
        string[]? customRule = null,
        string[]? prototypeContains = null) =>
        new()
        {
            Id = id,
            Conditions = new EspConditions
            {
                HasComponent = hasComponent?.ToList() ?? new List<string>(),
                JobContains = jobContains?.ToList() ?? new List<string>(),
                CustomRule = customRule?.ToList() ?? new List<string>(),
                PrototypeContains = prototypeContains?.ToList() ?? new List<string>()
            }
        };

    [Fact]
    public void GateComponent_PrefersTheDeclaredComponent()
    {
        // hasComponent is a conjunction, so any one of its names is necessary. Taking the first keeps
        // one bucket per category, which is what stops a category running twice for one entity.
        var category = Category("Item", hasComponent: ["Item", "Sprite"]);

        EspCategoryIndex.GateComponent(category, NoGates).Should().Be("Item");
    }

    [Fact]
    public void GateComponent_UsesInventoryForAJobCategory()
    {
        // A job title comes off the ID card in an inventory slot, so an entity without an inventory
        // can never match.
        var category = Category("Job", jobContains: ["Security"]);

        EspCategoryIndex.GateComponent(category, NoGates).Should().Be("Inventory");
    }

    [Fact]
    public void GateComponent_UsesTheRegisteredRuleGate()
    {
        var category = Category("Mob", customRule: ["IsNonPlayerMob"]);
        var gates = new Dictionary<string, string> { ["IsNonPlayerMob"] = "MobState" };

        EspCategoryIndex.GateComponent(category, gates).Should().Be("MobState");
    }

    [Fact]
    public void GateComponent_LeavesAnUngatedRuleUngated()
    {
        // A rule whose gate the caller did not declare has to stay ungated rather than be dropped,
        // and a prototype-only category has no component to gate on at all.
        EspCategoryIndex.GateComponent(Category("Antag", customRule: ["IsAntag"]), NoGates).Should().BeNull();
        EspCategoryIndex.GateComponent(Category("Admin", prototypeContains: ["Admin"]), NoGates).Should().BeNull();
    }

    [Fact]
    public void Build_BucketsAnItemRuleUnderItsComponent()
    {
        // The reason the index exists rather than a mob filter: adding item ESP is a rule in esp.yml
        // declaring hasComponent: ["Item"], and it has to land in its own bucket with no code change.
        var item = Category("ItemName", hasComponent: ["Item"]);
        var mob = Category("MobName", customRule: ["IsNonPlayerMob"]);
        var gates = new Dictionary<string, string> { ["IsNonPlayerMob"] = "MobState" };

        var (byComponent, ungated) = EspCategoryIndex.Build([item, mob], gates);

        byComponent.Should().ContainKey("Item").WhoseValue.Should().ContainSingle().Which.Should().BeSameAs(item);
        byComponent.Should().ContainKey("MobState").WhoseValue.Should().ContainSingle().Which.Should().BeSameAs(mob);
        ungated.Should().BeEmpty();
    }

    [Fact]
    public void Build_CoversEveryCategoryExactlyOnce()
    {
        // A category in two buckets would emit its line twice for an entity that has both components.
        var registry = new EspRegistry();
        registry.LoadFromString(EspRegistry.DefaultConfig);

        var (byComponent, ungated) = EspCategoryIndex.Build(registry.Categories, RobustaEspOverlay.RuleGates);
        var all = byComponent.Values.SelectMany(list => list).Concat(ungated).ToList();

        all.Should().HaveCount(registry.Categories.Count);
        all.Should().OnlyHaveUniqueItems();

        // AdminESP matches on a prototype id substring, so it has no component to gate on.
        ungated.Should().Contain(c => c.Id == "AdminESP");
    }

    [Fact]
    public void RuleGates_CoverEveryShippedCustomRule()
    {
        // A rule with no gate runs for every entity in view, and each of those asks the player-state
        // layer for a snapshot. That is what made a crowded room unplayable.
        var registry = new EspRegistry();
        registry.LoadFromString(EspRegistry.DefaultConfig);

        var rules = registry.Categories
            .SelectMany(category => category.Conditions.CustomRule)
            .Distinct()
            .ToList();

        RobustaEspOverlay.RuleGates.Keys.Should().Contain(rules);
    }

    [Fact]
    public void RuleGates_BucketTheAntagonistRulesByWhatTheyCanObserve()
    {
        var registry = new EspRegistry();
        registry.LoadFromString(EspRegistry.DefaultConfig);

        var (byComponent, _) = EspCategoryIndex.Build(registry.Categories, RobustaEspOverlay.RuleGates);

        byComponent["MindContainer"].Select(c => c.Id)
            .Should().Contain(["AntagonistTag", "AntagonistKindTag", "CloneTag", "ThiefTag"]);
        byComponent["ContainerManager"].Select(c => c.Id).Should().Contain("ContrabandTag");
    }
}
