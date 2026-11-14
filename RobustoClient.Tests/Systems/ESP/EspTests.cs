using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Robust.Shared.Maths;
using RobustoClient.Systems;
using RobustoClient.Systems.ESP;
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
        // every launch would overwrite it.
        EspRegistry.DefaultConfig.Should().Contain("# rob-esp-version: 2");
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
/// colour fallback and JSON round-trip are all pinned here.
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
    [InlineData("not-a-colour")]
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
