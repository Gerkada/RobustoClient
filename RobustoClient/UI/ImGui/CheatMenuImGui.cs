using System.Globalization;
using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Robust.Client.Input;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using RobustoClient.ImGui;
using RobustoClient.Systems;
using RobustoClient.Systems.Construction;
using RobustoClient.Systems.Construction.Blueprints;
using RobustoClient.Systems.ESP;

namespace RobustoClient.UI.ImGui;

public sealed class CheatMenuImGui
{
    private enum MenuTab
    {
        Combat,
        Visuals,
        Utility,
        Binds
    }

    private sealed class DiscoveredBind(FieldInfo field, KeybindConfigAttribute attr)
    {
        public FieldInfo Field { get; } = field;
        public KeybindConfigAttribute Attribute { get; } = attr;
        public string EditBuffer = "";
    }

    private readonly List<DiscoveredBind> _discoveredBinds = new();
    private DiscoveredBind? _activeListeningTarget;
    private MenuTab _activeTab = MenuTab.Combat;

    private string _blueprintName = "";
    private bool _savePending;
    private float _saveDebounceTimer;
    private const float SaveDebounceDelay = 0.5f;

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;
            _isOpen = value;
            if (!_isOpen)
            {
                StopListening();
                FlushPendingSave();
            }
        }
    }

    public bool IsListeningForKeybind => _activeListeningTarget != null;

    public void Toggle() => IsOpen = !IsOpen;

    public void Initialize()
    {
        DiscoverKeybinds();
    }

    public void TickTimers(float dt)
    {
        if (_savePending)
        {
            _saveDebounceTimer -= dt;
            if (_saveDebounceTimer <= 0f)
                FlushPendingSave();
        }
    }

    private void QueueDebouncedSave()
    {
        _savePending = true;
        _saveDebounceTimer = SaveDebounceDelay;
    }

    private void FlushPendingSave()
    {
        if (!_savePending)
            return;

        _savePending = false;
        _saveDebounceTimer = 0f;
        RobustaConfig.Save();
    }

    public void Draw()
    {
        Hexa.NET.ImGui.ImGui.SetNextWindowSize(new Vector2(780f, 490f), ImGuiCond.FirstUseEver);
        Hexa.NET.ImGui.ImGui.SetNextWindowSizeConstraints(new Vector2(720f, 430f), new Vector2(1400f, 1000f));

        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = _isOpen;
        if (!Hexa.NET.ImGui.ImGui.Begin(
                "ROBUSTA CLIENT | PREMIUM##MainWindow",
                ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            Hexa.NET.ImGui.ImGui.End();
            Hexa.NET.ImGui.ImGui.PopStyleVar();
            IsOpen = open;
            return;
        }
        Hexa.NET.ImGui.ImGui.PopStyleVar();
        IsOpen = open;

        if (_activeTab != MenuTab.Binds && _activeListeningTarget != null)
            StopListening();

        DrawSidebar();
        Hexa.NET.ImGui.ImGui.SameLine(0f, 0f);
        DrawContentArea();

        Hexa.NET.ImGui.ImGui.End();
    }

    private void DrawSidebar()
    {
        Hexa.NET.ImGui.ImGui.PushStyleColor(ImGuiCol.ChildBg, RobustaImGuiTheme.BgSidebar);
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 14f));
        Hexa.NET.ImGui.ImGui.BeginChild("##Sidebar", new Vector2(185f, 0f), ImGuiChildFlags.Borders);

        if (!RobustaImGuiTheme.FontBold.IsNull)
            Hexa.NET.ImGui.ImGui.PushFont(RobustaImGuiTheme.FontBold, 17f);
        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.AccentVioletHover, "ROBUSTA");
        if (!RobustaImGuiTheme.FontBold.IsNull)
            Hexa.NET.ImGui.ImGui.PopFont();

        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "PREMIUM | v2.1.0");
        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.Spacing();

        DrawNavTab("COMBAT", MenuTab.Combat);
        DrawNavTab("VISUALS", MenuTab.Visuals);
        DrawNavTab("UTILITY", MenuTab.Utility);
        DrawNavTab("BINDS", MenuTab.Binds);

        Hexa.NET.ImGui.ImGui.Spacing();
        Hexa.NET.ImGui.ImGui.Separator();
        Hexa.NET.ImGui.ImGui.Spacing();

        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "WINDOWS");
        var btnSize = new Vector2(-1f, 30f);
        if (RobustaImGuiWidgets.AccentButton("Tactical Radar", btnSize, RobustaImGuiManager.Radar.IsOpen))
            RobustaImGuiManager.Radar.IsOpen = !RobustaImGuiManager.Radar.IsOpen;

        if (RobustaImGuiWidgets.AccentButton("Inventory Viewer", btnSize, RobustaImGuiManager.InventoryViewer.IsOpen))
        {
            if (RobustaImGuiManager.InventoryViewer.IsOpen)
                RobustaImGuiManager.InventoryViewer.IsOpen = false;
            else if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
                entMan.System<RobustaInventoryViewerSystem>().OpenForHoverOrTarget();
        }

        if (RobustaImGuiWidgets.AccentButton("AutoChem Bot", btnSize, RobustaImGuiManager.AutoChem.IsOpen))
            RobustaImGuiManager.AutoChem.IsOpen = !RobustaImGuiManager.AutoChem.IsOpen;

        float footerSpace = Hexa.NET.ImGui.ImGui.GetContentRegionAvail().Y - 28f;
        if (footerSpace > 0f)
            Hexa.NET.ImGui.ImGui.Dummy(new Vector2(1f, footerSpace));

        var dl = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
        var cursor = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        dl.AddCircleFilled(cursor + new Vector2(6f, 9f), 4.5f, RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentGreen));
        Hexa.NET.ImGui.ImGui.SetCursorPosX(Hexa.NET.ImGui.ImGui.GetCursorPosX() + 16f);
        Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Status: Active");

        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.PopStyleVar();
        Hexa.NET.ImGui.ImGui.PopStyleColor();
    }

    private void DrawNavTab(string label, MenuTab tab)
    {
        bool selected = _activeTab == tab;
        var pos = Hexa.NET.ImGui.ImGui.GetCursorScreenPos();
        var size = new Vector2(Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X, 34f);

        if (RobustaImGuiWidgets.AccentButton(label, size, selected))
            _activeTab = tab;

        if (selected)
        {
            var dl = Hexa.NET.ImGui.ImGui.GetWindowDrawList();
            dl.AddRectFilled(
                pos,
                pos + new Vector2(3.5f, size.Y),
                RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentCyan),
                2f);
        }
    }

    private void DrawContentArea()
    {
        Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 14f));
        Hexa.NET.ImGui.ImGui.BeginChild("##ContentArea", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);

        switch (_activeTab)
        {
            case MenuTab.Combat:
                DrawCombatTab();
                break;
            case MenuTab.Visuals:
                DrawVisualsTab();
                break;
            case MenuTab.Utility:
                DrawUtilityTab();
                break;
            case MenuTab.Binds:
                DrawBindsTab();
                break;
        }

        Hexa.NET.ImGui.ImGui.EndChild();
        Hexa.NET.ImGui.ImGui.PopStyleVar();
    }

    private void DrawCombatTab()
    {
        if (Hexa.NET.ImGui.ImGui.BeginTable("##CombatGrid", 2, ImGuiTableFlags.SizingStretchSame))
        {
            Hexa.NET.ImGui.ImGui.TableNextColumn();
            RobustaImGuiWidgets.BeginSectionCard("##CombatTargets", "TARGET ACQUISITION");

            if (RobustaImGuiWidgets.ToggleSwitch("Ranged Aimbot", ref RobustaConfig.RangedAimbotEnabled))
                RobustaConfig.Save();
            if (RobustaImGuiWidgets.ToggleSwitch("Aim Prediction", ref RobustaConfig.UsePrediction))
                RobustaConfig.Save();
            if (RobustaImGuiWidgets.ToggleSwitch("Melee Aimbot", ref RobustaConfig.MeleeAimbotEnabled))
                RobustaConfig.Save();
            if (RobustaImGuiWidgets.ToggleSwitch("Melee Triggerbot", ref RobustaConfig.MeleeTriggerbotEnabled))
                RobustaConfig.Save();
            if (RobustaImGuiWidgets.ToggleSwitch("Throw Aimbot", ref RobustaConfig.ThrowAimbotEnabled))
                RobustaConfig.Save();
            if (RobustaImGuiWidgets.ToggleSwitch("No Recoil", ref RobustaConfig.NoRecoilEnabled))
                RobustaConfig.Save();

            RobustaImGuiWidgets.EndSectionCard();

            Hexa.NET.ImGui.ImGui.TableNextColumn();
            RobustaImGuiWidgets.BeginSectionCard("##CombatTuning", "TUNING & SPINBOT");

            if (RobustaImGuiWidgets.ToggleSwitch("AutoSpin (SpinBot)", ref RobustaConfig.SpinBotEnabled))
                RobustaConfig.Save();

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Aim FOV (px)");
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
            if (Hexa.NET.ImGui.ImGui.SliderFloat("##AimFov", ref RobustaConfig.AimFovPixels, 10f, 500f, "%.0f px"))
                QueueDebouncedSave();

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Max Lock Distance (m)");
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
            if (Hexa.NET.ImGui.ImGui.SliderFloat("##LockDist", ref RobustaConfig.MaxLockDistance, 1f, 40f, "%.1f m"))
                QueueDebouncedSave();

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Spin Speed (°/s)");
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
            if (Hexa.NET.ImGui.ImGui.SliderFloat("##SpinSpeed", ref RobustaConfig.SpinBotDegreesPerSecond, 90f, 3600f, "%.0f °/s"))
                QueueDebouncedSave();

            RobustaImGuiWidgets.EndSectionCard();
            Hexa.NET.ImGui.ImGui.EndTable();
        }
    }

    private void DrawVisualsTab()
    {
        RobustaImGuiWidgets.BeginSectionCard("##VisualsCard", "ESP & RENDERING OVERLAYS");

        if (RobustaImGuiWidgets.ToggleSwitch("ESP (Name Tags)", ref RobustaConfig.EspEnabled))
            RobustaConfig.Save();
        if (RobustaImGuiWidgets.ToggleSwitch("Fullbright", ref RobustaConfig.FullbrightEnabled))
            RobustaConfig.Save();
        if (RobustaImGuiWidgets.ToggleSwitch("FOV Disabler (Draw Occlusion)", ref RobustaConfig.FOVDisable))
            RobustaConfig.Save();
        if (RobustaImGuiWidgets.ToggleSwitch("Overlays Disabler", ref RobustaConfig.OverlaysDisable))
            RobustaConfig.Save();

        Hexa.NET.ImGui.ImGui.Spacing();
        if (RobustaImGuiWidgets.AccentButton("Reload ESP Config", new Vector2(-1f, 32f)))
        {
            if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
            {
                var esp = entMan.System<EspEvaluator>();
                esp.Registry.LoadConfigs();
                esp.ClearCache();
            }
        }

        RobustaImGuiWidgets.EndSectionCard();
    }

    private void DrawUtilityTab()
    {
        if (Hexa.NET.ImGui.ImGui.BeginTable("##UtilityGrid", 2, ImGuiTableFlags.SizingStretchSame))
        {
            Hexa.NET.ImGui.ImGui.TableNextColumn();
            RobustaImGuiWidgets.BeginSectionCard("##UtilityMove", "MOVEMENT & DETECTION");

            if (RobustaImGuiWidgets.ToggleSwitch("AntiSlip", ref RobustaConfig.AntiSlipEnabled))
            {
                if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) &&
                    entMan.EntitySysManager.TryGetEntitySystem<RobustaAntiSlipSystem>(out var antiSlip))
                {
                    antiSlip.SetEnabled(RobustaConfig.AntiSlipEnabled);
                }
                RobustaConfig.Save();
            }

            if (RobustaImGuiWidgets.ToggleSwitch("Syndicate Detector", ref RobustaConfig.SyndicateDetector))
                RobustaConfig.Save();

            if (RobustaImGuiWidgets.ToggleSwitch("Contraband Detector", ref RobustaConfig.ContrabandDetector))
                RobustaConfig.Save();

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Ping Compensation");
            DrawPingCompensationSelector();

            Hexa.NET.ImGui.ImGui.Spacing();
            if (RobustaImGuiWidgets.AccentButton("Open Station Map (GPS)", new Vector2(-1f, 30f)))
                RobustaMapOpener.Show();
            if (RobustaImGuiWidgets.AccentButton("Open Tactical Radar", new Vector2(-1f, 30f), RobustaImGuiManager.Radar.IsOpen))
                RobustaImGuiManager.Radar.IsOpen = !RobustaImGuiManager.Radar.IsOpen;

            RobustaImGuiWidgets.EndSectionCard();

            Hexa.NET.ImGui.ImGui.TableNextColumn();
            RobustaImGuiWidgets.BeginSectionCard("##UtilityConstruct", "AUTOMATION & BLUEPRINTS");

            float thirdW = (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 16f) / 3f;
            if (RobustaImGuiWidgets.AccentButton("Auto-Build", new Vector2(thirdW, 28f)))
            {
                if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) &&
                    entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoSys))
                {
                    autoSys.TriggerAtCursor(forceDeconstruct: false);
                }
            }
            Hexa.NET.ImGui.ImGui.SameLine();
            if (RobustaImGuiWidgets.AccentButton("Auto-Decon", new Vector2(thirdW, 28f)))
            {
                if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) &&
                    entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoSys))
                {
                    autoSys.TriggerAtCursor(forceDeconstruct: true);
                }
            }
            Hexa.NET.ImGui.ImGui.SameLine();
            if (RobustaImGuiWidgets.DangerButton("Stop", new Vector2(thirdW, 28f)))
            {
                if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) &&
                    entMan.EntitySysManager.TryGetEntitySystem<RobustaAutoConstructSystem>(out var autoSys))
                {
                    autoSys.StopJob();
                }
            }

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.TextColored(RobustaImGuiTheme.TextMuted, "Blueprint Name");
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
            Hexa.NET.ImGui.ImGui.InputTextWithHint("##BlueprintName", "Blueprint name...", ref _blueprintName, 128);

            float halfW = (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 8f) * 0.5f;
            if (RobustaImGuiWidgets.AccentButton("Save Area (2 Clicks)", new Vector2(halfW, 28f)))
            {
                if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) &&
                    entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpMan))
                {
                    bpMan.StartAreaExportSelection(_blueprintName);
                }
            }
            Hexa.NET.ImGui.ImGui.SameLine();
            if (RobustaImGuiWidgets.AccentButton("Reload Blueprints", new Vector2(halfW, 28f)))
            {
                if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) &&
                    entMan.EntitySysManager.TryGetEntitySystem<RobustaBlueprintManager>(out var bpMan))
                {
                    bpMan.LoadAllBlueprints();
                }
            }

            Hexa.NET.ImGui.ImGui.Spacing();
            Hexa.NET.ImGui.ImGui.Separator();
            Hexa.NET.ImGui.ImGui.Spacing();

            RobustaImGuiWidgets.ToggleSwitch("Highlight Searched Items", ref RobustaConfig.ItemSearchEnabled);
            Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
            Hexa.NET.ImGui.ImGui.InputTextWithHint("##ItemSearch", "Item Search Query...", ref RobustaConfig.ItemSearchQuery, 128);

            RobustaImGuiWidgets.EndSectionCard();
            Hexa.NET.ImGui.ImGui.EndTable();
        }
    }

    private static void DrawPingCompensationSelector()
    {
        ReadOnlySpan<(string Label, RobustaConfig.PingMode Mode)> modes =
        [
            ("Local", RobustaConfig.PingMode.Local),
            ("Stable", RobustaConfig.PingMode.Stable),
            ("Laggy", RobustaConfig.PingMode.Laggy),
            ("Auto", RobustaConfig.PingMode.Auto)
        ];

        float btnW = (Hexa.NET.ImGui.ImGui.GetContentRegionAvail().X - 24f) / 4f;
        for (int i = 0; i < modes.Length; i++)
        {
            if (i > 0)
                Hexa.NET.ImGui.ImGui.SameLine();

            var (label, mode) = modes[i];
            if (RobustaImGuiWidgets.AccentButton(label, new Vector2(btnW, 28f), RobustaConfig.CurrentPingMode == mode))
            {
                RobustaConfig.CurrentPingMode = mode;
                RobustaConfig.Save();
            }
        }
    }

    private void DrawBindsTab()
    {
        RobustaImGuiWidgets.BeginSectionCard("##BindsCard", "KEYBINDINGS");
        Hexa.NET.ImGui.ImGui.TextColored(
            RobustaImGuiTheme.TextMuted,
            "Click 'Bind' to capture keys (Backspace/Delete clears, Escape cancels), or edit the keybind text directly.");
        Hexa.NET.ImGui.ImGui.Spacing();

        if (Hexa.NET.ImGui.ImGui.BeginTable(
                "##BindsTable",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Key Combination", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Capture", ImGuiTableColumnFlags.WidthFixed, 100f);
            Hexa.NET.ImGui.ImGui.TableSetupColumn("Reset", ImGuiTableColumnFlags.WidthFixed, 68f);
            Hexa.NET.ImGui.ImGui.TableHeadersRow();

            foreach (var item in _discoveredBinds)
            {
                var currentVal = (RobustaKeybind)item.Field.GetValue(null)!;
                bool isListening = _activeListeningTarget == item;

                Hexa.NET.ImGui.ImGui.TableNextRow();
                Hexa.NET.ImGui.ImGui.TableNextColumn();
                Hexa.NET.ImGui.ImGui.AlignTextToFramePadding();
                Hexa.NET.ImGui.ImGui.TextUnformatted(item.Attribute.DisplayName);

                Hexa.NET.ImGui.ImGui.TableNextColumn();
                if (!Hexa.NET.ImGui.ImGui.IsItemActive())
                    item.EditBuffer = currentVal.IsValid ? currentVal.ToString() : "";

                var hint = item.Attribute.DefaultKey != Keyboard.Key.Unknown
                    ? $"Default: {new RobustaKeybind(item.Attribute.DefaultKey, item.Attribute.DefaultMod1)}"
                    : "Unassigned";

                Hexa.NET.ImGui.ImGui.SetNextItemWidth(-1f);
                if (Hexa.NET.ImGui.ImGui.InputTextWithHint(
                        $"##bind_in_{item.Field.Name}",
                        hint,
                        ref item.EditBuffer,
                        64,
                        ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    if (TryParseKeybind(item.EditBuffer, out var parsed))
                        ApplyBind(item, parsed);
                }
                else if (Hexa.NET.ImGui.ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (TryParseKeybind(item.EditBuffer, out var parsed))
                        ApplyBind(item, parsed);
                }

                Hexa.NET.ImGui.ImGui.TableNextColumn();
                string bindBtnLabel = isListening
                    ? $"[Press Key...]##bind_btn_{item.Field.Name}"
                    : $"Bind##bind_btn_{item.Field.Name}";
                if (RobustaImGuiWidgets.AccentButton(bindBtnLabel, new Vector2(-1f, 0f), isListening))
                {
                    if (isListening)
                        StopListening();
                    else
                        _activeListeningTarget = item;
                }

            Hexa.NET.ImGui.ImGui.TableNextColumn();
            Hexa.NET.ImGui.ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4f, 4f));
            bool resetPressed = RobustaImGuiWidgets.AccentButton($"Reset##reset_btn_{item.Field.Name}", new Vector2(-1f, 0f));
            Hexa.NET.ImGui.ImGui.PopStyleVar();
            if (resetPressed)
            {
                var defaultBind = new RobustaKeybind(item.Attribute.DefaultKey, item.Attribute.DefaultMod1);
                ApplyBind(item, defaultBind);
            }
            }

            Hexa.NET.ImGui.ImGui.EndTable();
        }

        Hexa.NET.ImGui.ImGui.Spacing();
        if (RobustaImGuiWidgets.DangerButton("Reset All Binds to Default", new Vector2(-1f, 32f)))
            ResetAllBinds();

        RobustaImGuiWidgets.EndSectionCard();
    }

    private void DiscoverKeybinds()
    {
        _discoveredBinds.Clear();
        var fields = typeof(RobustaConfig).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            if (field.FieldType != typeof(RobustaKeybind))
                continue;

            var attr = field.GetCustomAttribute<KeybindConfigAttribute>();
            if (attr == null)
                continue;

            var bind = new DiscoveredBind(field, attr);
            var currentVal = (RobustaKeybind)field.GetValue(null)!;
            bind.EditBuffer = currentVal.IsValid ? currentVal.ToString() : "";
            _discoveredBinds.Add(bind);
        }
    }

    public void StopListening()
    {
        _activeListeningTarget = null;
    }

    public bool TryHandleKeybindListening(KeyEventArgs ev)
    {
        if (_activeListeningTarget == null)
            return false;

        if (ev.Key == Keyboard.Key.Escape)
        {
            StopListening();
            return true;
        }

        if (ev.Key is Keyboard.Key.BackSpace or Keyboard.Key.Delete)
        {
            ApplyBind(_activeListeningTarget, new RobustaKeybind(Keyboard.Key.Unknown));
            StopListening();
            return true;
        }

        if (IsModifierOrIgnoredKey(ev.Key))
            return true;

        var mod1 = Keyboard.Key.Unknown;
        var mod2 = Keyboard.Key.Unknown;
        var mod3 = Keyboard.Key.Unknown;

        void AddMod(Keyboard.Key mod)
        {
            if (mod1 == Keyboard.Key.Unknown) mod1 = mod;
            else if (mod2 == Keyboard.Key.Unknown) mod2 = mod;
            else if (mod3 == Keyboard.Key.Unknown) mod3 = mod;
        }

        if (ev.Shift) AddMod(Keyboard.Key.Shift);
        if (ev.Control) AddMod(Keyboard.Key.Control);
        if (ev.Alt) AddMod(Keyboard.Key.Alt);

        var newBind = new RobustaKeybind(ev.Key, mod1, mod2, mod3);
        ApplyBind(_activeListeningTarget, newBind);
        StopListening();
        return true;
    }

    private void ApplyBind(DiscoveredBind target, RobustaKeybind bind)
    {
        target.Field.SetValue(null, bind);
        target.EditBuffer = bind.IsValid ? bind.ToString() : "";

        if (!string.IsNullOrEmpty(target.Attribute.Command) &&
            RobustaImGuiManager.TryResolve<IInputManager>(out var inputManager))
        {
            UnregisterCommandBinding(inputManager, target.Attribute.Command);

            if (bind.IsValid && bind.BaseKey != Keyboard.Key.Unknown)
            {
                try
                {
                    inputManager.RegisterBinding(new KeyBindingRegistration
                    {
                        Function = target.Attribute.Command,
                        BaseKey = bind.BaseKey,
                        Mod1 = bind.Mod1,
                        Mod2 = bind.Mod2,
                        Mod3 = bind.Mod3,
                        Priority = 10,
                        Type = KeyBindingType.Command,
                        CommandWhenUIFocused = false
                    }, false);
                }
                catch (Exception ex)
                {
                    MarseyLogger.Warn($"Failed to register binding for {target.Attribute.Command}: {ex.Message}");
                }
            }
        }

        QueueDebouncedSave();
    }

    private static void UnregisterCommandBinding(IInputManager inputManager, string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var function = new BoundKeyFunction(command);
        while (true)
        {
            try
            {
                var existing = inputManager.GetKeyBinding(function);
                inputManager.RemoveBinding(existing);
            }
            catch (KeyNotFoundException)
            {
                break;
            }
            catch (Exception ex)
            {
                MarseyLogger.Warn($"Failed while removing binding for {command}: {ex.Message}");
                break;
            }
        }
    }

    private void ResetAllBinds()
    {
        foreach (var item in _discoveredBinds)
        {
            var defaultBind = new RobustaKeybind(item.Attribute.DefaultKey, item.Attribute.DefaultMod1);
            ApplyBind(item, defaultBind);
        }
        FlushPendingSave();
    }

    private static bool IsModifier(Keyboard.Key key) =>
        key is >= Keyboard.Key.Control and <= Keyboard.Key.RSystem;

    private static bool IsModifierOrIgnoredKey(Keyboard.Key key) =>
        key is Keyboard.Key.Unknown or Keyboard.Key.Escape or Keyboard.Key.BackSpace or Keyboard.Key.Delete or Keyboard.Key.MouseLeft
        || IsModifier(key);

    private static bool TryParseKeybind(string? text, out RobustaKeybind bind)
    {
        bind = new RobustaKeybind(Keyboard.Key.Unknown);
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var clean = text.Trim();
        if (clean.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("unbound", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = clean.Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var modifiers = new List<Keyboard.Key>();
        Keyboard.Key? baseKey = null;

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase)) p = "Control";
            if (p.Equals("cmd", StringComparison.OrdinalIgnoreCase) || p.Equals("win", StringComparison.OrdinalIgnoreCase)) p = "LSystem";

            if (!Enum.TryParse<Keyboard.Key>(p, true, out var key) || !Enum.IsDefined(key))
                return false;

            if (IsModifier(key))
            {
                modifiers.Add(key);
            }
            else
            {
                if (baseKey != null)
                    return false;
                baseKey = key;
            }
        }

        if (baseKey == null)
        {
            if (modifiers.Count > 0)
            {
                baseKey = modifiers[^1];
                modifiers.RemoveAt(modifiers.Count - 1);
            }
            else
            {
                return false;
            }
        }

        var m1 = modifiers.Count > 0 ? modifiers[0] : Keyboard.Key.Unknown;
        var m2 = modifiers.Count > 1 ? modifiers[1] : Keyboard.Key.Unknown;
        var m3 = modifiers.Count > 2 ? modifiers[2] : Keyboard.Key.Unknown;

        var candidate = new RobustaKeybind(baseKey.Value, m1, m2, m3);
        if (!candidate.IsValid)
            return false;

        bind = candidate;
        return true;
    }
}
