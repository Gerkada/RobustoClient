using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluentAssertions;
using Robust.Client.Input;
using RobustoClient.ImGui;
using RobustoClient.UI.ImGui;
using RobustoClient.UI.ImGui.Radial;
using Xunit;

namespace RobustoClient.Tests.UI;

/// <summary>
/// The menu's collapse bookkeeping and the radial wheel's pure rules. Both are plain state, so neither
/// needs an ImGui context. The key-ownership cases mutate process-wide config statics, so the classes
/// share the config tests' collection.
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
        // Folding the window is not the same as folding every card, reopening must restore the view
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

    [Theory]
    [InlineData("%.0f px", 137.28f, 137f)]
    [InlineData("%.1f m", 1.26f, 1.3f)]
    [InlineData("%.2f", 1.234f, 1.23f)]
    public void SnapToFormat_RoundsToTheDisplayedPrecision(string format, float value, float expected)
    {
        // Without a step the grid is the precision the reading shows, so the stored value and the
        // column can never disagree.
        RobustaImGuiWidgets.SnapToFormat(value, format, 0f, 1000f).Should().BeApproximately(expected, 1e-5f);
    }

    [Theory]
    [InlineData(137f, 135f)]
    [InlineData(138f, 140f)]
    [InlineData(12f, 10f)]
    [InlineData(500f, 500f)]
    public void SnapToFormat_UsesTheStepWhenGiven(float value, float expected)
    {
        // Aim FOV: 10..500 in 5 px steps.
        RobustaImGuiWidgets.SnapToFormat(value, "%.0f px", 10f, 500f, 5f).Should().Be(expected);
    }

    [Theory]
    [InlineData(1444f, 1440f)]
    [InlineData(1440f, 1440f)]
    [InlineData(90f, 90f)]
    [InlineData(3600f, 3600f)]
    public void SnapToFormat_KeepsTheSpinGridOnItsEndpoints(float value, float expected)
    {
        // Spin Speed: 90..3600 in 10 deg/s steps, so the 1440 default and the maximum stay reachable.
        RobustaImGuiWidgets.SnapToFormat(value, "%.0f deg/s", 90f, 3600f, 10f).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("%d")]
    public void SnapToFormat_LeavesTheValueAloneWithoutAFloatSpecifier(string format)
    {
        RobustaImGuiWidgets.SnapToFormat(1.234f, format, 0f, 10f).Should().Be(1.234f);
    }
}

/// <summary>
/// The radial wheel's pure parts: the sector resolver, the layout table it must agree with, and the
/// ordered action list that picks the layout.
/// </summary>
[Collection("ProcessWideState")]
public class RadialMenuTests
{
    private const float DeadZone = 30f;

    [Theory]
    [InlineData(0, -100f, 0f)]
    [InlineData(1, 100f, 0f)]
    public void Resolve_TwoActions_SplitsLeftAndRight(int expected, float x, float y)
    {
        RadialMenuImGui.Resolve(new Vector2(x, y), DeadZone, 2).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0f, -100f)]
    [InlineData(1, -80f, 60f)]
    [InlineData(2, 80f, 60f)]
    public void Resolve_ThreeActions_UsesUpBottomLeftBottomRight(int expected, float x, float y)
    {
        RadialMenuImGui.Resolve(new Vector2(x, y), DeadZone, 3).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0f, -100f)]
    [InlineData(1, 0f, 100f)]
    [InlineData(2, -100f, 0f)]
    [InlineData(3, 100f, 0f)]
    public void Resolve_FourActions_UsesCardinalSectors(int expected, float x, float y)
    {
        RadialMenuImGui.Resolve(new Vector2(x, y), DeadZone, 4).Should().Be(expected);
    }

    [Fact]
    public void Resolve_InsideDeadZone_Cancels()
    {
        RadialMenuImGui.Resolve(new Vector2(10f, 10f), DeadZone, 4).Should().Be(-1);
        RadialMenuImGui.Resolve(Vector2.Zero, DeadZone, 2).Should().Be(-1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SectorMidpoint_ResolvesToItsOwnSector(int count)
    {
        // The drawn wedge and the hit test must agree, or the wheel lies about what is selected.
        for (int index = 0; index < count; index++)
        {
            var (from, to) = RadialMenuImGui.SectorAngles(index, count);
            float mid = (from + to) * 0.5f;
            var point = new Vector2(MathF.Cos(mid), MathF.Sin(mid)) * 100f;

            RadialMenuImGui.Resolve(point, DeadZone, count).Should().Be(index);
        }
    }

    [Fact]
    public void Display_StripsCommandPrefixAndSplitsWords()
    {
        // The derived form is the fallback for ids without a short name, so this targets an id that
        // deliberately has none.
        RadialMenuActions.DeriveDisplay("robusta.some_new_action").Should().Be("Some New Action");
        RadialMenuActions.DeriveDisplay("ActivateItemInWorld").Should().Be("Activate Item In World");
        RadialMenuActions.DeriveDisplay("").Should().Be("");
    }

    [Fact]
    public void RadialMenuSettings_ActiveCountSkipsEmptyRows()
    {
        var settings = new RobustaConfig.RadialMenuSettings
        {
            Actions = ["robusta.hack", "", "robusta.auto_construct", ""]
        };

        settings.ActiveCount.Should().Be(2);
        settings.ActiveAt(0).Should().Be("robusta.hack");
        settings.ActiveAt(1).Should().Be("robusta.auto_construct");
        settings.ActiveAt(2).Should().Be("");
    }

    [Fact]
    public void RadialMenuSettings_EnsureDefaults_PadsToFourRows()
    {
        var settings = new RobustaConfig.RadialMenuSettings { Actions = ["robusta.hack"] };

        settings.EnsureDefaults();

        settings.Actions.Should().HaveCount(4);
        settings.Actions[0].Should().Be("robusta.hack");
        settings.Actions.Skip(1).Should().OnlyContain(action => action == "");
    }

    [Fact]
    public void Ensure_DropsNullsAndPadsEachWheel()
    {
        int before = RobustaConfig.Radials.Count;
        var radial = new RobustaConfig.RadialMenuSettings { Actions = ["robusta.hack"] };
        RobustaConfig.Radials.Add(null!);
        RobustaConfig.Radials.Add(radial);

        try
        {
            RadialMenuImGui.Ensure();

            RobustaConfig.Radials.Should().NotContainNulls();
            radial.Actions.Should().HaveCount(4);
        }
        finally
        {
            RobustaConfig.Radials.RemoveRange(before, RobustaConfig.Radials.Count - before);
        }
    }

    [Fact]
    public void FindKeyOwners_ReportsDirectBindAndWheelHoldingTheKey()
    {
        var original = RobustaConfig.TargetLockKey;
        var radial = new RobustaConfig.RadialMenuSettings { Key = new RobustaKeybind(Keyboard.Key.Space) };
        RobustaConfig.Radials.Add(radial);

        try
        {
            var (fields, radials) = CheatMenuImGui.FindKeyOwners(new RobustaKeybind(Keyboard.Key.Space), null, null);

            fields.Should().Contain(nameof(RobustaConfig.TargetLockKey));
            radials.Should().Contain(radial);
        }
        finally
        {
            RobustaConfig.Radials.Remove(radial);
            RobustaConfig.TargetLockKey = original;
        }
    }

    [Fact]
    public void FindKeyOwners_SkipsTheCallerAndAnUnboundKey()
    {
        var radial = new RobustaConfig.RadialMenuSettings { Key = new RobustaKeybind(Keyboard.Key.F9) };
        RobustaConfig.Radials.Add(radial);

        try
        {
            var (fields, radials) = CheatMenuImGui.FindKeyOwners(new RobustaKeybind(Keyboard.Key.F9), null, radial);

            fields.Should().BeEmpty();
            radials.Should().BeEmpty();

            var (unboundFields, unboundRadials) =
                CheatMenuImGui.FindKeyOwners(new RobustaKeybind(Keyboard.Key.Unknown), null, null);

            unboundFields.Should().BeEmpty();
            unboundRadials.Should().BeEmpty();
        }
        finally
        {
            RobustaConfig.Radials.Remove(radial);
        }
    }

    [Fact]
    public void OwnsKey_OnlyMatchesAWheelKey()
    {
        var radial = new RobustaConfig.RadialMenuSettings { Key = new RobustaKeybind(Keyboard.Key.F9) };
        RobustaConfig.Radials.Add(radial);

        try
        {
            RadialMenuImGui.OwnsKey(new RobustaKeybind(Keyboard.Key.F9)).Should().BeTrue();
            RadialMenuImGui.OwnsKey(new RobustaKeybind(Keyboard.Key.F8)).Should().BeFalse();
        }
        finally
        {
            RobustaConfig.Radials.Remove(radial);
        }
    }
}

/// <summary>
/// The layout store is pure bookkeeping, so it is tested without an ImGui context. Applying a
/// placement to a real window needs the client and is not covered here.
/// </summary>
[Collection("ProcessWideState")]
public sealed class WindowLayoutTests : IDisposable
{
    private readonly string _tempConfigPath;
    private readonly Dictionary<string, RobustaConfig.WindowPlacement> _originalPlacements;

    public WindowLayoutTests()
    {
        _tempConfigPath = Path.Combine(
            Path.GetTempPath(), "RobustoClient.Tests", Guid.NewGuid().ToString("N"), "config.json");

        RobustaConfig.ConfigPathOverride = _tempConfigPath;
        _originalPlacements = RobustaConfig.WindowPlacements;
        RobustaConfig.WindowPlacements = new Dictionary<string, RobustaConfig.WindowPlacement>();

        // A previous test may have left a write pending. Flush it against the temp path, never the
        // user's real config.
        WindowLayout.FlushIfPending();
    }

    public void Dispose()
    {
        WindowLayout.FlushIfPending();
        RobustaConfig.ConfigPathOverride = null;
        RobustaConfig.WindowPlacements = _originalPlacements;

        try
        {
            var dir = Path.GetDirectoryName(_tempConfigPath);
            if (dir != null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file is not worth failing a test over.
        }
    }

    [Fact]
    public void Remember_StoresTheGeometryAndReportsARealMove()
    {
        WindowLayout.Remember(WindowLayoutId.Radar, new Vector2(120f, 80f), new Vector2(400f, 480f));

        var placement = WindowLayout.Get(WindowLayoutId.Radar);

        placement.Should().NotBeNull();
        placement!.X.Should().Be(120f);
        placement.Y.Should().Be(80f);
        placement.Width.Should().Be(400f);
        placement.Height.Should().Be(480f);

        WindowLayout.Remember(WindowLayoutId.Radar, new Vector2(300f, 200f), new Vector2(400f, 480f))
            .Should().BeTrue();

        WindowLayout.Get(WindowLayoutId.Radar)!.X.Should().Be(300f);
    }

    [Fact]
    public void Remember_IgnoresSubPixelJitter()
    {
        // A window that is not being dragged reports the same geometry every frame, so rewriting it
        // would dirty the config on every frame of the session.
        WindowLayout.Remember(WindowLayoutId.Radar, new Vector2(120f, 80f), new Vector2(400f, 480f));

        var changed = WindowLayout.Remember(
            WindowLayoutId.Radar, new Vector2(120.2f, 80.2f), new Vector2(400.2f, 480.2f));

        changed.Should().BeFalse();
        WindowLayout.Get(WindowLayoutId.Radar)!.X.Should().Be(120f);
    }

    [Fact]
    public void Remember_KeepsWindowsApart()
    {
        WindowLayout.Remember(WindowLayoutId.Radar, new Vector2(10f, 20f), new Vector2(300f, 300f));
        WindowLayout.Remember(WindowLayoutId.AutoChem, new Vector2(600f, 40f), new Vector2(440f, 520f));

        WindowLayout.Get(WindowLayoutId.Radar)!.X.Should().Be(10f);
        WindowLayout.Get(WindowLayoutId.AutoChem)!.X.Should().Be(600f);
    }

    [Fact]
    public void Get_IgnoresAnEmptyPlacement()
    {
        // A hand-edited config with a zero size would otherwise open a window with no content area.
        RobustaConfig.WindowPlacements[WindowLayoutId.Radar] = new RobustaConfig.WindowPlacement
        {
            X = 10f,
            Y = 10f,
            Width = 0f,
            Height = 0f
        };

        WindowLayout.Get(WindowLayoutId.Radar).Should().BeNull();
    }

    [Fact]
    public void ClampPosition_KeepsAnOnScreenWindow()
    {
        var placement = Placement(100f, 100f, 400f, 300f);

        WindowLayout.ClampPosition(placement, new Vector2(1920f, 1080f)).Should().Be(new Vector2(100f, 100f));
    }

    [Fact]
    public void ClampPosition_PullsAWindowBackInsideTheDisplay()
    {
        // Reopened at a smaller resolution, a window left at the bottom right would otherwise come
        // back where it cannot be grabbed.
        var placement = Placement(1800f, 1000f, 400f, 300f);

        WindowLayout.ClampPosition(placement, new Vector2(1920f, 1080f)).Should().Be(new Vector2(1520f, 780f));
    }

    [Fact]
    public void ClampPosition_HandlesADisplaySmallerThanTheWindow()
    {
        var placement = Placement(-50f, -50f, 2000f, 2000f);

        WindowLayout.ClampPosition(placement, new Vector2(1280f, 720f)).Should().Be(Vector2.Zero);
    }

    [Fact]
    public void Placement_SurvivesSaveAndLoad()
    {
        WindowLayout.Remember(
            WindowLayoutId.InventoryViewer, new Vector2(300f, 150f), new Vector2(560f, 520f));

        RobustaConfig.Save();
        RobustaConfig.WindowPlacements = new Dictionary<string, RobustaConfig.WindowPlacement>();
        RobustaConfig.Load();

        var placement = WindowLayout.Get(WindowLayoutId.InventoryViewer);

        placement.Should().NotBeNull();
        placement!.X.Should().BeApproximately(300f, 0.01f);
        placement.Y.Should().BeApproximately(150f, 0.01f);
        placement.Width.Should().BeApproximately(560f, 0.01f);
        placement.Height.Should().BeApproximately(520f, 0.01f);
    }

    [Fact]
    public void FlushIfPending_WritesOncePerGesture()
    {
        WindowLayout.Remember(WindowLayoutId.Radar, new Vector2(120f, 80f), new Vector2(400f, 480f));

        WindowLayout.FlushIfPending();
        File.Exists(_tempConfigPath).Should().BeTrue();

        // Deleting the file makes a stray write observable, so a clean flush must not recreate it.
        File.Delete(_tempConfigPath);
        WindowLayout.FlushIfPending();
        File.Exists(_tempConfigPath).Should().BeFalse();
    }

    private static RobustaConfig.WindowPlacement Placement(float x, float y, float width, float height) =>
        new()
        {
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
}
