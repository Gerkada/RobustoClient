using FluentAssertions;
using RobustoClient.ImGui;
using RobustoClient.UI.ImGui;
using Xunit;

namespace RobustoClient.Tests.UI;

/// <summary>
/// Collapse state is pure bookkeeping, so it is tested without an ImGui context. The rendering half
/// (a collapsed card shrinking to its title) needs the client and is not covered here.
/// </summary>
public class CheatMenuCollapseTests
{
    [Fact]
    public void Card_IsExpandedByDefault()
    {
        var menu = new CheatMenuImGui();

        menu.IsCardCollapsed("##AnyCard").Should().BeFalse();
    }

    [Fact]
    public void Card_TogglesBothWays()
    {
        var menu = new CheatMenuImGui();

        menu.ToggleCardCollapsed("##Card");
        menu.IsCardCollapsed("##Card").Should().BeTrue();

        menu.ToggleCardCollapsed("##Card");
        menu.IsCardCollapsed("##Card").Should().BeFalse();
    }

    [Fact]
    public void Cards_CollapseIndependently()
    {
        var menu = new CheatMenuImGui();

        menu.SetCardCollapsed("##A", true);

        menu.IsCardCollapsed("##A").Should().BeTrue();
        menu.IsCardCollapsed("##B").Should().BeFalse();
    }

    [Fact]
    public void Header_StartsExpanded()
    {
        var menu = new CheatMenuImGui();

        menu.IsHeaderCollapsed.Should().BeFalse();
    }

    [Fact]
    public void Header_CollapsesAndExpands()
    {
        var menu = new CheatMenuImGui();

        menu.CollapseHeader();
        menu.IsHeaderCollapsed.Should().BeTrue();

        menu.ExpandHeader();
        menu.IsHeaderCollapsed.Should().BeFalse();
    }

    [Fact]
    public void SelectTab_ExpandsCollapsedHeader()
    {
        // Clicking a tab while the header is folded must bring the content back, otherwise the click
        // would appear to do nothing.
        var menu = new CheatMenuImGui();

        menu.CollapseHeader();
        menu.SelectTab(CheatMenuImGui.MenuTab.Visuals);

        menu.IsHeaderCollapsed.Should().BeFalse();
        menu.ActiveTab.Should().Be(CheatMenuImGui.MenuTab.Visuals);
    }

    [Fact]
    public void SelectTab_SwitchesTabWhenExpanded()
    {
        var menu = new CheatMenuImGui();

        menu.SelectTab(CheatMenuImGui.MenuTab.Themes);

        menu.ActiveTab.Should().Be(CheatMenuImGui.MenuTab.Themes);
        menu.IsHeaderCollapsed.Should().BeFalse();
    }

    [Fact]
    public void CollapsingHeader_LeavesCardStateAlone()
    {
        // Folding the window is not the same as folding every card; reopening must restore the view
        // the user had.
        var menu = new CheatMenuImGui();
        menu.SetCardCollapsed("##Card", true);

        menu.CollapseHeader();
        menu.ExpandHeader();

        menu.IsCardCollapsed("##Card").Should().BeTrue();
    }
}


/// <summary>
/// Slider readings are formatted from ImGui-style patterns such as <c>"%.0f px"</c>. Passing those
/// to <see cref="string.Format"/> returns the pattern literally, which left the value column frozen
/// on the pattern text instead of the number. These tests pin the translation.
/// </summary>
public class RobustaImGuiWidgetsTests
{
    [Theory]
    [InlineData("%.0f px", 500f, "500 px")]
    [InlineData("%.0f px", 10f, "10 px")]
    [InlineData("%.1f m", 40f, "40.0 m")]
    [InlineData("%.1f m", 1.5f, "1.5 m")]
    [InlineData("%.0f deg/s", 3600f, "3600 deg/s")]
    [InlineData("%.2f", 1.234f, "1.23")]
    public void FormatReading_SubstitutesTheNumber(string format, float value, string expected)
    {
        RobustaImGuiWidgets.FormatReading(format, value).Should().Be(expected);
    }

    [Theory]
    [InlineData("%.0f px", 123f)]
    [InlineData("%.1f m", 1.5f)]
    [InlineData("%.0f deg/s", 90f)]
    public void FormatReading_NeverReturnsThePatternLiterally(string format, float value)
    {
        // The bug: the column showed "%.0f px" instead of a number, so it appeared frozen.
        var reading = RobustaImGuiWidgets.FormatReading(format, value);

        reading.Should().NotContain("%");
        reading.Should().NotBe(format);
    }

    [Fact]
    public void FormatReading_ChangesAsTheValueChanges()
    {
        // The visible symptom was a value that never updated while dragging.
        var before = RobustaImGuiWidgets.FormatReading("%.0f px", 10f);
        var after = RobustaImGuiWidgets.FormatReading("%.0f px", 250f);

        before.Should().NotBe(after);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("%d")]
    [InlineData("%.f")]
    [InlineData("%.")]
    public void FormatReading_FallsBackToThePlainNumber(string format)
    {
        // Anything that is not a float specifier still has to produce a usable number rather than
        // throwing or echoing the pattern.
        var reading = RobustaImGuiWidgets.FormatReading(format, 7.5f);

        reading.Should().NotContain("%");
        reading.Should().Be("7.5");
    }

    [Fact]
    public void FormatReading_HandlesEmptyFormat()
    {
        RobustaImGuiWidgets.FormatReading("", 3.5f).Should().Be("3.5");
    }
}
