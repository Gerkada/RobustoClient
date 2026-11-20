using System.Numerics;
using FluentAssertions;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using RobustoClient.Systems;
using RobustoClient.UI.ImGui;
using Xunit;

namespace RobustoClient.Tests.UI;

/// <summary>
/// The radar's pure rules: when the hedged syndicate and theft tags belong on a blip, how a blip is
/// projected, and when the Info panel re-docks. All of it is bookkeeping, so none needs an ImGui
/// context.
/// </summary>
public class RadarWindowTests
{
    [Fact]
    public void HedgedSyndicateTag_ShowsForASignalWithNoNamedAntagonist()
    {
        // Syndicate gear and a major-contraband chemical load are the two hedged signals.
        RadarImGui.ShouldShowHedgedSyndicateTag(hasSyndicateSignal: true, antagonist: null).Should().BeTrue();
        RadarImGui.ShouldShowHedgedSyndicateTag(hasSyndicateSignal: false, antagonist: null).Should().BeFalse();
    }

    [Fact]
    public void HedgedSyndicateTag_IsSuppressedByANamedAntagonist()
    {
        // A ninja carries syndicate contraband, so the hedge used to sit under [NINJA] and repeat it.
        RadarImGui.ShouldShowHedgedSyndicateTag(true, AntagonistKind.Ninja).Should().BeFalse();
        RadarImGui.ShouldShowHedgedSyndicateTag(true, AntagonistKind.NukeOperative).Should().BeFalse();
        RadarImGui.ShouldShowHedgedSyndicateTag(true, AntagonistKind.Thief).Should().BeFalse();
    }

    [Fact]
    public void SyndicateTag_ShowsForAConfirmedLinkOnly()
    {
        RadarImGui.ShouldShowSyndicateTag(isSyndicate: true, antagonist: null).Should().BeTrue();
        RadarImGui.ShouldShowSyndicateTag(true, AntagonistKind.Ninja).Should().BeTrue();
        RadarImGui.ShouldShowSyndicateTag(isSyndicate: false, antagonist: null).Should().BeFalse();
    }

    [Fact]
    public void SyndicateTag_IsSuppressedByAChangelingWizardOrNukie()
    {
        // All three hold an antagonist-only store currency or a kit uplink, which trips the uplink
        // signal, so the tag would misdescribe them, their own tag already names them.
        RadarImGui.ShouldShowSyndicateTag(true, AntagonistKind.Changeling).Should().BeFalse();
        RadarImGui.ShouldShowSyndicateTag(true, AntagonistKind.Wizard).Should().BeFalse();
        RadarImGui.ShouldShowSyndicateTag(true, AntagonistKind.NukeOperative).Should().BeFalse();
    }

    [Fact]
    public void CommandTag_IsHedgedWithoutAMindshield()
    {
        RadarImGui.CommandTag("QM", mindshielded: true).Should().Be("[QM]");
        RadarImGui.CommandTag("QM", mindshielded: false).Should().Be("[QM?]");

        // A command job with no attributable item still gets the generic tag.
        RadarImGui.CommandTag(null, mindshielded: false).Should().Be("[COMMAND?]");
    }

    [Fact]
    public void ZoomedRange_MovesTheSliderValueAndStaysInItsBand()
    {
        // Wheel up zooms in, lowering the range the slider shows.
        RadarImGui.ZoomedRange(30f, 1f).Should().BeLessThan(30f);
        RadarImGui.ZoomedRange(30f, -1f).Should().BeGreaterThan(30f);
        RadarImGui.ZoomedRange(30f, 0f).Should().Be(30f);

        // Clamped to the slider's own band, however many notches arrive at once.
        RadarImGui.ZoomedRange(15f, 5f).Should().Be(15f);
        RadarImGui.ZoomedRange(60f, -5f).Should().Be(60f);
    }

    /// <summary>
    /// The Mobs switch hides NPCs, and a dead player reads as a corpse, so it goes with them. A
    /// critically injured player still moves, so it stays.
    /// </summary>
    [Theory]
    [InlineData(RadarMarkerType.Player, false, false, true)]
    [InlineData(RadarMarkerType.Player, true, false, false)]
    [InlineData(RadarMarkerType.Mob, false, false, false)]
    [InlineData(RadarMarkerType.Mob, true, false, false)]
    // With the switch on, everything is drawn.
    [InlineData(RadarMarkerType.Player, false, true, true)]
    [InlineData(RadarMarkerType.Player, true, true, true)]
    [InlineData(RadarMarkerType.Mob, false, true, true)]
    [InlineData(RadarMarkerType.Mob, true, true, true)]
    public void IsMarkerVisible_HidesCorpsesWithTheMobsSwitch(
        RadarMarkerType type,
        bool dead,
        bool showMobs,
        bool expected)
    {
        RadarImGui.IsMarkerVisible(type, dead, showMobs).Should().Be(expected);
    }

    [Fact]
    public void ProjectToRadar_PlacesANearBlipExactlyWhereTheScaleSays()
    {
        // 3 m north of the player, 10 px per metre, camera-up: up is negative Y on screen.
        var (offset, distance) = RadarImGui.ProjectToRadar(new Vector2(0f, 3f), Angle.Zero, 10f, 200f);

        offset.Should().Be(new Vector2(0f, -30f));
        distance.Should().BeApproximately(30f, 1e-4f);
    }

    [Fact]
    public void ProjectToRadar_PinsABlipPastTheRimAndKeepsTheTrueDistance()
    {
        // The dot sits on the rim so it stays visible, but the unclamped distance is reported so the
        // caller can still hide a label that would not fit.
        var (offset, distance) = RadarImGui.ProjectToRadar(new Vector2(0f, 100f), Angle.Zero, 10f, 200f);

        offset.Length().Should().BeApproximately(194f, 1e-3f);
        distance.Should().BeApproximately(1000f, 1e-3f);
    }

    [Fact]
    public void TheftTag_ShowsForStolenGoodsWithNoNamedAntagonist()
    {
        RadarImGui.ShouldShowTheftTag(
            hasStolenGear: true,
            isChassis: false,
            confirmedNukie: false,
            syndicateLink: false,
            antagonist: null).Should().BeTrue();

        RadarImGui.ShouldShowTheftTag(false, isChassis: false, confirmedNukie: false, syndicateLink: false,
            antagonist: null).Should().BeFalse();
    }

    [Fact]
    public void TheftTag_NeverShowsOnAChassis()
    {
        // A chassis can only ever be a normal ghost role, so carried contraband cannot make it a thief.
        RadarImGui.ShouldShowTheftTag(true, isChassis: true, confirmedNukie: false, syndicateLink: false,
            antagonist: null).Should().BeFalse();
    }

    [Fact]
    public void TheftTag_IsSuppressedByANamedAntagonistOrASyndicateLink()
    {
        // Both already carry a tag that names them, so the hedge would only repeat it.
        RadarImGui.ShouldShowTheftTag(true, false, confirmedNukie: false, syndicateLink: false,
            antagonist: AntagonistKind.Thief).Should().BeFalse();

        RadarImGui.ShouldShowTheftTag(true, false, confirmedNukie: true, syndicateLink: false,
            antagonist: AntagonistKind.NukeOperative).Should().BeFalse();

        RadarImGui.ShouldShowTheftTag(true, isChassis: false, confirmedNukie: false, syndicateLink: true,
            antagonist: null).Should().BeFalse();
    }

    [Fact]
    public void InfoPanel_DocksWhenItOpens()
    {
        var panel = new RadarInfoImGui();

        panel.Show(new EntityUid(1));

        panel.IsOpen.Should().BeTrue();
        panel.PendingDock.Should().BeTrue();
        panel.PendingUncollapse.Should().BeFalse();
    }

    [Fact]
    public void InfoPanel_KeepsItsPositionForANewTarget()
    {
        var panel = new RadarInfoImGui();
        panel.Show(new EntityUid(1));

        // The first draw consumes the dock request.
        panel.MarkDocked();

        panel.Show(new EntityUid(2));

        panel.Target.Should().Be(new EntityUid(2));
        panel.IsOpen.Should().BeTrue();
        panel.PendingDock.Should().BeFalse("an open panel must not jump back beside the radar");
        panel.PendingUncollapse.Should().BeTrue("a collapsed panel re-expands where it sits");
    }

    [Fact]
    public void InfoPanel_RepeatedTargetIsANoOp()
    {
        var panel = new RadarInfoImGui();
        panel.Show(new EntityUid(1));

        panel.Show(new EntityUid(1));

        panel.PendingUncollapse.Should().BeFalse();
    }

    [Fact]
    public void InfoPanel_DocksAgainAfterBeingClosed()
    {
        var panel = new RadarInfoImGui();
        panel.Show(new EntityUid(1));
        panel.IsOpen = false;

        panel.Show(new EntityUid(2));

        panel.IsOpen.Should().BeTrue();
        panel.PendingDock.Should().BeTrue();
    }
}
