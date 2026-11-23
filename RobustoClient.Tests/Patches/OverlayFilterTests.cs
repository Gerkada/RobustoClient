using System;
using System.Linq;
using FluentAssertions;
using Robust.Client.Graphics;
using Robust.Shared.GameObjects;
using RobustoClient.Patches;
using RobustoClient.Reflection;
using Xunit;

namespace RobustoClient.Tests.Patches;

/// <summary>
/// The overlay filter is an explicit list of simple names, so these pin what is on it and, more
/// importantly, what is not. An overlay this build has never heard of must keep drawing: a fork that
/// ships an effect we do not know about should not have it hidden by accident.
/// </summary>
/// <remarks>
/// Overlay instances cannot be built here: <see cref="Overlay"/>'s constructor resolves the overlay
/// manager from IoC, which needs a running client. The tests therefore assert the type-level rule.
/// </remarks>
public class OverlayFilterTests
{
    [Theory]
    [InlineData("FlashOverlay")]
    [InlineData("BlindOverlay")]
    [InlineData("BlurryVisionOverlay")]
    [InlineData("DrunkOverlay")]
    [InlineData("RainbowOverlay")]
    [InlineData("DrowsinessOverlay")]
    [InlineData("BlackAndWhiteOverlay")]
    [InlineData("NoirOverlay")]
    [InlineData("NightVisionOverlay")]
    // The welding mask. Paints a texture over the viewport without sampling it, so no capability rule
    // would have caught it.
    [InlineData("ImageOverlay")]
    public void FilteredEffects_AreSuppressed(string simpleName)
    {
        var type = Reflect.FindTypeByName(simpleName);

        type.Should().NotBeNull($"{simpleName} must resolve in this build");
        OverlaysPatch.IsFiltered(type!).Should().BeTrue($"{simpleName} is on the filter list");
    }

    [Theory]
    // HUD elements that share ImageOverlay's shape: ScreenSpace, no screen texture, no BeforeDraw.
    [InlineData("PopupOverlay")]
    [InlineData("ShowHandItemOverlay")]
    [InlineData("CombatModeIndicatorsOverlay")]
    [InlineData("MapTextOverlay")]
    // World-awareness effects that must keep drawing.
    [InlineData("GasTileHeatBlurOverlay")]
    [InlineData("GasTileVacuumOverlay")]
    [InlineData("ScreechShockWaveOverlay")]
    [InlineData("RadiationPulseOverlay")]
    [InlineData("SingularityOverlay")]
    public void UnlistedOverlays_KeepDrawing(string simpleName)
    {
        var type = Reflect.FindTypeByName(simpleName);

        type.Should().NotBeNull($"{simpleName} must resolve in this build");
        OverlaysPatch.IsFiltered(type!).Should().BeFalse($"{simpleName} is not on the filter list");
    }

    [Fact]
    public void OurOwnOverlays_AreNeverFiltered()
    {
        var ours = typeof(OverlaysPatch).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(Overlay).IsAssignableFrom(t))
            .ToArray();

        ours.Should().NotBeEmpty("the mod ships its own overlays");

        foreach (var type in ours)
            OverlaysPatch.IsFiltered(type).Should().BeFalse($"{type.Name} belongs to the mod");
    }

    [Fact]
    public void UnknownType_IsNotFiltered()
    {
        // The fork-safety case: a type this build has never seen keeps drawing.
        OverlaysPatch.IsFiltered(typeof(OverlayFilterTests)).Should().BeFalse();
    }
}

/// <summary>
/// The local player's own status icons are hidden only while Job Icons is on and the user has not asked
/// to see them. Both switches are global config, so the class restores them and shares the serialized
/// collection with the other process-wide tests.
/// </summary>
[Collection("ProcessWideState")]
public class SelfStatusIconTests : IDisposable
{
    private readonly bool _icons = RobustaConfig.Esp.Icons.Enabled;
    private readonly bool _showOwn = RobustaConfig.ShowOwnStatusIcons;

    public void Dispose()
    {
        RobustaConfig.Esp.Icons.Enabled = _icons;
        RobustaConfig.ShowOwnStatusIcons = _showOwn;
    }

    [Theory]
    // icons, showOwn, uid, localPlayer, expected
    [InlineData(true, false, 1, 1, true)]   // the default: own icons hidden
    [InlineData(true, true, 1, 1, false)]   // the new toggle keeps them
    [InlineData(false, false, 1, 1, false)] // Job Icons off draws nothing, so nothing to hide
    [InlineData(true, false, 2, 1, false)]  // other players are never hidden
    [InlineData(true, false, 1, 0, false)]  // no local player attached
    public void ShouldHideOwnIcons_MatchesTheSwitches(
        bool icons, bool showOwn, int uid, int localPlayer, bool expected)
    {
        RobustaConfig.Esp.Icons.Enabled = icons;
        RobustaConfig.ShowOwnStatusIcons = showOwn;

        var local = localPlayer == 0 ? (EntityUid?)null : new EntityUid(localPlayer);
        SelfStatusIconPatch.ShouldHideOwnIcons(new EntityUid(uid), local).Should().Be(expected);
    }
}

/// <summary>
/// The health bar follows the same rule as the status icons: hidden on the local player only while
/// Health Bars is on and Show Own Icons is off. The two patches share one predicate, so this pins the
/// health-bar side of it.
/// </summary>
[Collection("ProcessWideState")]
public class SelfHealthBarTests : IDisposable
{
    private readonly bool _health = RobustaConfig.Esp.Health.Enabled;
    private readonly bool _icons = RobustaConfig.Esp.Icons.Enabled;
    private readonly bool _showOwn = RobustaConfig.ShowOwnStatusIcons;

    public void Dispose()
    {
        RobustaConfig.Esp.Health.Enabled = _health;
        RobustaConfig.Esp.Icons.Enabled = _icons;
        RobustaConfig.ShowOwnStatusIcons = _showOwn;
    }

    [Theory]
    // health, showOwn, uid, localPlayer, expected
    [InlineData(true, false, 1, 1, true)]   // the default: own bar hidden
    [InlineData(true, true, 1, 1, false)]   // Show Own Icons keeps it
    [InlineData(false, false, 1, 1, false)] // Health Bars off draws nothing, so nothing to hide
    [InlineData(true, false, 2, 1, false)]  // other players are never hidden
    [InlineData(true, false, 1, 0, false)]  // no local player attached
    public void ShouldHideOwnHealthBar_MatchesTheSwitches(
        bool health, bool showOwn, int uid, int localPlayer, bool expected)
    {
        RobustaConfig.Esp.Health.Enabled = health;
        RobustaConfig.ShowOwnStatusIcons = showOwn;

        var local = localPlayer == 0 ? (EntityUid?)null : new EntityUid(localPlayer);
        SelfOverlayFilter.ShouldHideOwn(health, new EntityUid(uid), local).Should().Be(expected);
    }

    [Fact]
    public void ShowOwnIcons_IsIndependentOfTheJobIconsSwitch()
    {
        // The toggle is shared, so turning Job Icons off must not change what the health bar does.
        RobustaConfig.Esp.Icons.Enabled = false;
        RobustaConfig.Esp.Health.Enabled = true;
        RobustaConfig.ShowOwnStatusIcons = false;

        SelfOverlayFilter.ShouldHideOwn(RobustaConfig.Esp.Health.Enabled, new EntityUid(1), new EntityUid(1))
            .Should().BeTrue();
    }
}
