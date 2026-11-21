using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Robust.Client.Input;
using Robust.Shared.GameObjects;
using RobustoClient.Systems.Macros;
using RobustoClient.UI.ImGui.Radial;
using Xunit;

namespace RobustoClient.Tests.Systems.Macros;

/// <summary>
/// Covers the macro registry and the best-item ordering rule. Both are pure: the registry is a
/// static list and <c>SelectBest</c> takes plain tuples, so no client is needed.
/// </summary>
public class MacroTests
{
    [Fact]
    public void Macros_IdsAreUniqueAndPopulated()
    {
        var ids = RobustaMacroSystem.Macros.Select(macro => macro.Id).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));

        RobustaMacroSystem.Macros.Should().OnlyContain(macro => !string.IsNullOrWhiteSpace(macro.Label));
        RobustaMacroSystem.Macros.Should().OnlyContain(macro =>
            macro.Category == "Inventory" || macro.Category == "Security");
    }

    [Fact]
    public void Macros_ShipTheThreeInventoryEntries()
    {
        var ids = RobustaMacroSystem.Macros.Select(macro => macro.Id).ToList();

        ids.Should().Contain(["macro.slippery", "macro.melee", "macro.ranged"]);
    }

    [Fact]
    public void Macros_ShipTheFourSecurityEntries()
    {
        var security = RobustaMacroSystem.Macros.Where(macro => macro.Category == "Security").ToList();

        security.Select(macro => macro.Id)
            .Should().BeEquivalentTo(["macro.baton", "macro.disabler", "macro.cuffs", "macro.flash"]);
    }

    [Fact]
    public void Macros_AreStableAcrossReads()
    {
        // Guards the lazy build: a second read must not rebuild or return a different list.
        RobustaMacroSystem.Macros.Should().BeSameAs(RobustaMacroSystem.Macros);
    }

    [Fact]
    public void TryGetMacro_RoundTripsEveryId()
    {
        foreach (var macro in RobustaMacroSystem.Macros)
        {
            RobustaMacroSystem.TryGetMacro(macro.Id, out var found).Should().BeTrue();
            found.Should().BeSameAs(macro);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("macro.does_not_exist")]
    [InlineData("robusta.hack")]
    public void TryGetMacro_RejectsUnknownIds(string? id)
    {
        RobustaMacroSystem.TryGetMacro(id, out _).Should().BeFalse();
    }

    [Fact]
    public void Display_ShortNamesAreUsedForKnownCommands()
    {
        RadialMenuActions.Display("robusta.hack").Should().Be("Hack");
        RadialMenuActions.Display("robusta.friend_list").Should().Be("Friend List");
        RadialMenuActions.Display("robusta.anti_slip.set_enabled").Should().Be("Anti-Slip");
    }

    [Fact]
    public void Display_MacrosUseTheirBareLabel()
    {
        foreach (var macro in RobustaMacroSystem.Macros)
        {
            RadialMenuActions.Display(macro.Id).Should().Be(macro.Label);
            RadialMenuActions.Display(macro.Id).Should().NotContain("[MACRO]");
        }
    }

    [Fact]
    public void PickerLabel_TagsMacrosSoTheyStandOutFromCommands()
    {
        foreach (var macro in RobustaMacroSystem.Macros)
            RadialMenuActions.PickerLabel(macro.Id).Should().Be($"[MACRO] {macro.Label}");
    }

    [Fact]
    public void PickerLabel_LeavesCommandsUnprefixed()
    {
        RadialMenuActions.PickerLabel("robusta.hack").Should().Be("Hack");
        RadialMenuActions.PickerLabel("robusta.friend_list").Should().Be("Friend List");
    }

    [Fact]
    public void Available_UsesThePickerLabelForMacros()
    {
        var macros = RobustaMacroSystem.Macros
            .Select(macro => macro.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (id, label) in RadialMenuActions.Available)
        {
            if (macros.Contains(id))
                label.Should().StartWith("[MACRO] ");
        }
    }

    [Fact]
    public void Display_UnknownActionFallsBackToTheDerivedForm()
    {
        RadialMenuActions.Display("robusta.some_new_action").Should().Be("Some New Action");
    }

    [Fact]
    public void Display_ShortNamesFitALongestSectorBudget()
    {
        // A wheel sector is narrow, anything past ~16 characters is truncated by the wheel.
        foreach (var macro in RobustaMacroSystem.Macros)
            RadialMenuActions.Display(macro.Id).Length.Should().BeLessThanOrEqualTo(16);
    }

    [Theory]
    [InlineData("R", 1)]
    [InlineData("Shift+R", 2)]
    [InlineData("Shift+Control+R", 3)]
    [InlineData("Shift+Control+Alt+R", 4)]
    public void Specificity_CountsTheKeysInTheCombination(string text, int expected)
    {
        RobustaKeybind.TryParse(text, out var bind).Should().BeTrue();

        bind.Specificity.Should().Be(expected);
    }

    [Fact]
    public void Specificity_UnboundCombinationIsStillOne()
    {
        // Nothing to press, but the count must not go to zero and break comparisons.
        new RobustaKeybind(Keyboard.Key.Unknown).Specificity.Should().Be(1);
    }

    [Fact]
    public void SelectBest_HighestScoreWins()
    {
        var low = new EntityUid(1);
        var high = new EntityUid(2);
        var mid = new EntityUid(3);

        var best = RobustaMacroSystem.SelectBest([(low, 5f), (high, 40f), (mid, 12f)]);

        best.Should().Be(high);
    }

    [Fact]
    public void SelectBest_KeepsTheEarlierCandidateOnATie()
    {
        var first = new EntityUid(7);
        var second = new EntityUid(8);

        var best = RobustaMacroSystem.SelectBest([(first, 10f), (second, 10f)]);

        best.Should().Be(first);
    }

    [Fact]
    public void SelectBest_IgnoresNonPositiveScores()
    {
        var zero = new EntityUid(1);
        var negative = new EntityUid(2);

        RobustaMacroSystem.SelectBest([(zero, 0f), (negative, -5f)]).Should().BeNull();
    }

    [Fact]
    public void SelectBest_ReturnsNullWhenEmpty()
    {
        RobustaMacroSystem.SelectBest([]).Should().BeNull();
    }
}
