using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using Robust.Client.Input;
using Xunit;

namespace RobustoClient.Tests.Config;

/// <summary>
/// Exercises the dirty-tracking contract behind <see cref="RobustaConfig.MarkDirty"/> /
/// <see cref="RobustaConfig.SaveIfDirty"/>. Each instance points the serializer at a throwaway temp
/// file and mutates one throwaway static field, so the user's real config is never read or written.
/// The dirty flag, the path override and the on-disk snapshot are process-wide, so this class shares a
/// collection with the ImGui tests instead of running in parallel with them.
/// </summary>
[Collection("ProcessWideState")]
public class RobustaConfigSaveTests : IDisposable
{
    private readonly string _tempConfigPath;

    private static readonly FieldInfo DirtyField =
        typeof(RobustaConfig).GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo SnapshotField =
        typeof(RobustaConfig).GetField("_lastFileContents", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo SampleField =
        typeof(RobustaConfig).GetField(nameof(RobustaConfig.NoRecoilEnabled))!;

    public RobustaConfigSaveTests()
    {
        _tempConfigPath = Path.Combine(
            Path.GetTempPath(), "RobustoClient.Tests", Guid.NewGuid().ToString("N"), "config.json");
        RobustaConfig.ConfigPathOverride = _tempConfigPath;
        SnapshotField.SetValue(null, null);
    }

    public void Dispose()
    {
        RobustaConfig.ConfigPathOverride = null;
        ResetDirty();
        SnapshotField.SetValue(null, null);

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

    private static void ResetDirty() => DirtyField.SetValue(null, false);

    private static bool IsDirty() => (bool)DirtyField.GetValue(null)!;

    [Fact]
    public void MarkDirty_SetsFlagWithoutWriting()
    {
        ResetDirty();

        RobustaConfig.MarkDirty();

        IsDirty().Should().BeTrue();
        File.Exists(RobustaConfig.ConfigPath).Should().BeFalse("MarkDirty must not persist");
    }

    [Fact]
    public void SaveIfDirty_WhenClean_DoesNotWrite()
    {
        ResetDirty();

        RobustaConfig.SaveIfDirty();

        File.Exists(RobustaConfig.ConfigPath).Should().BeFalse();
        IsDirty().Should().BeFalse();
    }

    [Fact]
    public void SaveIfDirty_WhenDirty_WritesAndClearsFlag()
    {
        var original = (bool)SampleField.GetValue(null)!;
        try
        {
            ResetDirty();
            RobustaConfig.MarkDirty();

            RobustaConfig.SaveIfDirty();

            File.Exists(RobustaConfig.ConfigPath).Should().BeTrue();
            IsDirty().Should().BeFalse("a successful save clears the dirty flag");
        }
        finally
        {
            SampleField.SetValue(null, original);
            ResetDirty();
        }
    }

    [Fact]
    public void RepeatedMarkDirty_CollapsesIntoSingleSave()
    {
        var original = (bool)SampleField.GetValue(null)!;
        try
        {
            ResetDirty();

            for (int i = 0; i < 10; i++)
                RobustaConfig.MarkDirty();

            IsDirty().Should().BeTrue();

            RobustaConfig.SaveIfDirty();
            IsDirty().Should().BeFalse();

            // A second flush is a no-op because nothing re-marked the config. Deleting the file makes
            // a stray write observable, which a timestamp comparison would not be.
            File.Delete(RobustaConfig.ConfigPath);
            RobustaConfig.SaveIfDirty();
            File.Exists(RobustaConfig.ConfigPath).Should().BeFalse("a clean config must not be written again");
        }
        finally
        {
            SampleField.SetValue(null, original);
            ResetDirty();
        }
    }

    [Fact]
    public void SaveDirect_ClearsDirtyFlag()
    {
        ResetDirty();
        RobustaConfig.MarkDirty();

        RobustaConfig.Save();

        IsDirty().Should().BeFalse("Save() is a full write and must leave the config clean");
    }

    [Fact]
    public void Load_AppliesAHandEditedFile()
    {
        // A user edits config.json by hand and may write only the keys they care about, so missing
        // keys must keep their default and the edited ones must win.
        var originalKey = RobustaConfig.ViewInvKey;
        var originalNoRecoil = RobustaConfig.NoRecoilEnabled;
        try
        {
            var dir = Path.GetDirectoryName(_tempConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_tempConfigPath, """
                {
                  "ViewInvKey": { "key": "J", "mod1": "Shift" },
                  "NoRecoilEnabled": true
                }
                """);

            RobustaConfig.Load();

            RobustaConfig.ViewInvKey.Should().Be(new RobustaKeybind(Keyboard.Key.J, Keyboard.Key.Shift));
            RobustaConfig.NoRecoilEnabled.Should().BeTrue();
            RobustaConfig.HackingKey.Should().Be(new RobustaKeybind(Keyboard.Key.H, Keyboard.Key.Shift),
                "a key the file omits must keep its compiled-in default");
        }
        finally
        {
            RobustaConfig.ViewInvKey = originalKey;
            RobustaConfig.NoRecoilEnabled = originalNoRecoil;
            ResetDirty();
        }
    }

    [Fact]
    public void Save_AdoptsAnEditMadeWhileTheProcessWasRunning()
    {
        // The exit save rewrites the whole file from memory. An edit made in a text editor while the
        // client is open used to be replaced by the stale in-memory value; it has to be folded in.
        var originalKey = RobustaConfig.ViewInvKey;
        try
        {
            RobustaConfig.ViewInvKey = new RobustaKeybind(Keyboard.Key.Unknown);
            RobustaConfig.Save();

            var root = JsonNode.Parse(File.ReadAllText(RobustaConfig.ConfigPath))!.AsObject();
            root["ViewInvKey"] = new JsonObject { ["key"] = "J", ["mod1"] = "Shift" };
            File.WriteAllText(RobustaConfig.ConfigPath, root.ToJsonString());

            RobustaConfig.Save();

            var expected = new RobustaKeybind(Keyboard.Key.J, Keyboard.Key.Shift);
            RobustaConfig.ViewInvKey.Should().Be(expected, "the on-disk edit must reach memory");
            File.ReadAllText(RobustaConfig.ConfigPath).Should().Contain("\"J\"");
        }
        finally
        {
            RobustaConfig.ViewInvKey = originalKey;
            ResetDirty();
        }
    }

    [Fact]
    public void SaveThenLoad_RestoresEveryValueKind()
    {
        // Covers the three shapes persistence has to survive: a plain bool field, a keybind that goes
        // through its own JSON converter, and a nested settings object.
        var originalNoRecoil = RobustaConfig.NoRecoilEnabled;
        var originalKey = RobustaConfig.HackingKey;
        var originalMobs = RobustaConfig.Esp.Mobs.Enabled;
        try
        {
            var key = new RobustaKeybind(Keyboard.Key.J, Keyboard.Key.Control);
            RobustaConfig.NoRecoilEnabled = true;
            RobustaConfig.HackingKey = key;
            RobustaConfig.Esp.Mobs.Enabled = false;
            RobustaConfig.Save();

            // Scramble the live values so a field that the loader skipped is visible in the asserts.
            RobustaConfig.NoRecoilEnabled = false;
            RobustaConfig.HackingKey = new RobustaKeybind(Keyboard.Key.Unknown);
            RobustaConfig.Esp.Mobs.Enabled = true;

            RobustaConfig.Load();

            RobustaConfig.NoRecoilEnabled.Should().BeTrue();
            RobustaConfig.HackingKey.Should().Be(key);
            RobustaConfig.Esp.Mobs.Enabled.Should().BeFalse();
        }
        finally
        {
            RobustaConfig.NoRecoilEnabled = originalNoRecoil;
            RobustaConfig.HackingKey = originalKey;
            RobustaConfig.Esp.Mobs.Enabled = originalMobs;
            ResetDirty();
        }
    }

    [Fact]
    public void Save_WithMalformedExternalEdit_StillSavesInMemoryState()
    {
        var originalNoRecoil = RobustaConfig.NoRecoilEnabled;
        try
        {
            RobustaConfig.NoRecoilEnabled = false;
            RobustaConfig.Save();

            File.WriteAllText(RobustaConfig.ConfigPath, "{ invalid json");

            RobustaConfig.NoRecoilEnabled = true;
            RobustaConfig.MarkDirty();
            RobustaConfig.SaveIfDirty();

            IsDirty().Should().BeFalse();
            var saved = JsonNode.Parse(File.ReadAllText(RobustaConfig.ConfigPath))!.AsObject();
            saved[nameof(RobustaConfig.NoRecoilEnabled)]!.GetValue<bool>().Should().BeTrue();
        }
        finally
        {
            RobustaConfig.NoRecoilEnabled = originalNoRecoil;
            ResetDirty();
        }
    }

    [Fact]
    public void Load_WithNullEspSubConfig_InitializesSubConfigAndAvoidsNullAnyEnabled()
    {
        var originalEsp = RobustaConfig.Esp;
        try
        {
            var dir = Path.GetDirectoryName(_tempConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_tempConfigPath, """
                {
                  "Esp": {
                    "Players": null,
                    "Mobs": { "Enabled": false }
                  }
                }
                """);

            RobustaConfig.Load();

            RobustaConfig.Esp.Players.Should().NotBeNull();
            RobustaConfig.Esp.Mobs.Enabled.Should().BeFalse();
            _ = RobustaConfig.Esp.AnyEnabled;

            var partial = new RobustaConfig.EspSettings { Players = null! };
            partial.AnyEnabled.Should().BeTrue("other features remain enabled by default");
        }
        finally
        {
            RobustaConfig.Esp = originalEsp;
            ResetDirty();
        }
    }

    [Fact]
    public void Load_MigratesLegacyOverlaysDisableAndEspEnabled()
    {
        var originalOverlays = RobustaConfig.DisableVisualOverlays;
        var originalEsp = RobustaConfig.Esp;
        try
        {
            RobustaConfig.DisableVisualOverlays = false;
            RobustaConfig.Esp = new RobustaConfig.EspSettings();

            var dir = Path.GetDirectoryName(_tempConfigPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_tempConfigPath, """
                {
                  "OverlaysDisable": true,
                  "EspEnabled": false
                }
                """);

            RobustaConfig.Load();

            RobustaConfig.DisableVisualOverlays.Should().BeTrue();
            RobustaConfig.Esp.AnyEnabled.Should().BeFalse();
        }
        finally
        {
            RobustaConfig.DisableVisualOverlays = originalOverlays;
            RobustaConfig.Esp = originalEsp;
            ResetDirty();
        }
    }
}


public class RobustaKeybindTests
{
    [Theory]
    [InlineData("H")]
    [InlineData("Shift+H")]
    [InlineData("Control+H")]
    [InlineData("Alt+H")]
    public void ParseThenFormat_RoundTripsExactly(string text)
    {
        RobustaKeybind.TryParse(text, out var bind).Should().BeTrue();

        bind.IsValid.Should().BeTrue();
        bind.ToString().Should().Be(text);
    }

    [Theory]
    [InlineData("Shift+H")]
    [InlineData("Shift+Control+H")]
    [InlineData("Shift+Control+Alt+H")]
    [InlineData("Alt+Control+Shift+H")]
    [InlineData("Alt+Shift+Control+H")]
    public void ParseThenFormat_IsStableUnderReformatting(string text)
    {
        // ToString() emits modifiers in canonical order, so the first format may reorder the text.
        // What must hold is that re-parsing that output is a fixed point.
        RobustaKeybind.TryParse(text, out var first).Should().BeTrue();
        var formatted = first.ToString();

        RobustaKeybind.TryParse(formatted, out var second).Should().BeTrue();

        second.Should().Be(first);
        second.ToString().Should().Be(formatted);
    }

    [Fact]
    public void Parse_SortsModifiersCanonically()
    {
        RobustaKeybind.TryParse("Alt+Control+Shift+H", out var a).Should().BeTrue();
        RobustaKeybind.TryParse("Shift+Control+Alt+H", out var b).Should().BeTrue();

        a.Should().Be(b, "modifier order in the text must not change identity");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("UNBOUND")]
    [InlineData("unknown")]
    public void Parse_BlankOrUnbindKeyword_YieldsUnbound(string? text)
    {
        RobustaKeybind.TryParse(text, out var bind).Should().BeTrue();

        bind.BaseKey.Should().Be(Keyboard.Key.Unknown);
        bind.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("ctrl+H", Keyboard.Key.Control)]
    [InlineData("Ctrl+H", Keyboard.Key.Control)]
    public void Parse_CtrlAlias_MapsToControl(string text, Keyboard.Key expectedMod)
    {
        RobustaKeybind.TryParse(text, out var bind).Should().BeTrue();

        bind.BaseKey.Should().Be(Keyboard.Key.H);
        bind.Mod1.Should().Be(expectedMod);
    }

    [Theory]
    [InlineData("cmd+H")]
    [InlineData("win+H")]
    public void Parse_CmdAndWinAliases_MapToLSystem(string text)
    {
        RobustaKeybind.TryParse(text, out var bind).Should().BeTrue();

        bind.BaseKey.Should().Be(Keyboard.Key.H);
        bind.Mod1.Should().Be(Keyboard.Key.LSystem);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("7")]
    [InlineData("12")]
    [InlineData("Shift+5")]
    public void Parse_NumericTokens_AreRejected(string text)
    {
        // Enum.TryParse alone would map these onto unrelated keys.
        RobustaKeybind.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("NotAKey")]
    [InlineData("Shift+NotAKey")]
    [InlineData("H+J")]
    [InlineData("Shift+Shift+H")]
    public void Parse_InvalidCombinations_AreRejected(string text)
    {
        RobustaKeybind.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_ModifiersOnly_AreRejected()
    {
        // Modifiers alone cannot trigger anything, so they are refused instead of being promoted
        // into a base key.
        RobustaKeybind.TryParse("Shift+Control", out _).Should().BeFalse();
        RobustaKeybind.TryParse("Shift", out _).Should().BeFalse();
        RobustaKeybind.TryParse("Control+Alt", out _).Should().BeFalse();
    }

    [Fact]
    public void FromCapture_CollectsModifiersInShiftControlAltOrder()
    {
        var bind = RobustaKeybind.FromCapture(Keyboard.Key.H, shift: true, control: true, alt: false);

        bind.BaseKey.Should().Be(Keyboard.Key.H);
        bind.Mod1.Should().Be(Keyboard.Key.Shift);
        bind.Mod2.Should().Be(Keyboard.Key.Control);
        bind.Mod3.Should().Be(Keyboard.Key.Unknown);
    }

    [Fact]
    public void FromCapture_NoModifiers_YieldsBareKey()
    {
        var bind = RobustaKeybind.FromCapture(Keyboard.Key.F4, false, false, false);

        bind.BaseKey.Should().Be(Keyboard.Key.F4);
        bind.ToString().Should().Be("F4");
        bind.IsValid.Should().BeTrue();
    }

    [Fact]
    public void FromCapture_ThenFormat_RoundTripsThroughParse()
    {
        var captured = RobustaKeybind.FromCapture(Keyboard.Key.H, shift: true, control: true, alt: true);
        var text = captured.ToString();

        RobustaKeybind.TryParse(text, out var reparsed).Should().BeTrue();
        reparsed.Should().Be(captured);
    }

    [Fact]
    public void FromCapture_WithSystemModifier_IncludesSystem()
    {
        var bind = RobustaKeybind.FromCapture(Keyboard.Key.H, shift: false, control: false, alt: false, system: true);

        bind.Mod1.Should().Be(Keyboard.Key.LSystem);
        RobustaKeybind.TryParse(bind.ToString(), out var reparsed).Should().BeTrue();
        reparsed.Should().Be(bind);
    }

    [Theory]
    [InlineData(Keyboard.Key.Control)]
    [InlineData(Keyboard.Key.Shift)]
    [InlineData(Keyboard.Key.Alt)]
    [InlineData(Keyboard.Key.RSystem)]
    public void IsModifier_RecognizesModifierRange(Keyboard.Key key)
    {
        RobustaKeybind.IsModifier(key).Should().BeTrue();
    }

    [Theory]
    [InlineData(Keyboard.Key.H)]
    [InlineData(Keyboard.Key.F4)]
    [InlineData(Keyboard.Key.Space)]
    [InlineData(Keyboard.Key.Unknown)]
    public void IsModifier_RejectsNonModifiers(Keyboard.Key key)
    {
        RobustaKeybind.IsModifier(key).Should().BeFalse();
    }

    [Theory]
    [InlineData(Keyboard.Key.Unknown)]
    [InlineData(Keyboard.Key.Escape)]
    [InlineData(Keyboard.Key.BackSpace)]
    [InlineData(Keyboard.Key.Delete)]
    [InlineData(Keyboard.Key.MouseLeft)]
    [InlineData(Keyboard.Key.Shift)]
    public void IsModifierOrIgnoredKey_RejectsUnbindableKeys(Keyboard.Key key)
    {
        RobustaKeybind.IsModifierOrIgnoredKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData(Keyboard.Key.H)]
    [InlineData(Keyboard.Key.F4)]
    [InlineData(Keyboard.Key.Space)]
    public void IsModifierOrIgnoredKey_AcceptsBindableKeys(Keyboard.Key key)
    {
        RobustaKeybind.IsModifierOrIgnoredKey(key).Should().BeFalse();
    }
}
