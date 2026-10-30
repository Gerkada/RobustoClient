using System;
using System.Collections.Generic;
using System.Reflection;
using Robust.Client.Input;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace RobustoClient.UI;

public sealed partial class CheatMenuWindow
{
    private readonly struct DiscoveredBind
    {
        public readonly FieldInfo Field;
        public readonly KeybindConfigAttribute Attribute;
        public readonly LineEdit Input;
        public readonly Button BindButton;

        public DiscoveredBind(FieldInfo field, KeybindConfigAttribute attribute, LineEdit input, Button bindButton)
        {
            Field = field;
            Attribute = attribute;
            Input = input;
            BindButton = bindButton;
        }
    }

    private readonly List<DiscoveredBind> _discoveredBinds = new();
    private DiscoveredBind? _activeListeningTarget;
    private float _listeningCooldown;

    private void DiscoverAndBuildKeybinds()
    {
        BindsListContainer.RemoveAllChildren();
        _discoveredBinds.Clear();

        var fields = typeof(RobustaConfig).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            if (field.FieldType != typeof(RobustaKeybind))
                continue;

            var attr = field.GetCustomAttribute<KeybindConfigAttribute>();
            if (attr == null)
                continue;

            var currentVal = (RobustaKeybind)field.GetValue(null)!;

            var row = new BoxContainer
            {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                SeparationOverride = 8
            };

            var label = new Label
            {
                Text = attr.DisplayName + ":",
                MinWidth = 160,
                FontColorOverride = Color.FromHex("#CDD6F4"),
                VerticalAlignment = VAlignment.Center
            };

            var input = new LineEdit
            {
                Text = currentVal.IsValid ? currentVal.ToString() : "",
                MinWidth = 110,
                HorizontalExpand = true,
                PlaceHolder = attr.DefaultKey != Keyboard.Key.Unknown
                    ? $"Default: {new RobustaKeybind(attr.DefaultKey, attr.DefaultMod1)}"
                    : "Unassigned"
            };

            var bindBtn = new Button { Text = "Bind", MinWidth = 75 };
            var resetBtn = new Button { Text = "Reset", MinWidth = 55 };

            var discovered = new DiscoveredBind(field, attr, input, bindBtn);
            _discoveredBinds.Add(discovered);

            bindBtn.OnPressed += _ => StartListening(discovered);

            resetBtn.OnPressed += _ =>
            {
                var defaultBind = new RobustaKeybind(attr.DefaultKey, attr.DefaultMod1);
                ApplyBind(discovered, defaultBind);
            };

            input.OnTextEntered += args =>
            {
                if (TryParseKeybind(args.Text, out var parsed))
                {
                    ApplyBind(discovered, parsed);
                }
                else
                {
                    var current = (RobustaKeybind)discovered.Field.GetValue(null)!;
                    input.Text = current.IsValid ? current.ToString() : "";
                }
            };

            input.OnFocusExit += args =>
            {
                if (TryParseKeybind(args.Text, out var parsed))
                {
                    ApplyBind(discovered, parsed);
                }
                else
                {
                    var current = (RobustaKeybind)discovered.Field.GetValue(null)!;
                    input.Text = current.IsValid ? current.ToString() : "";
                }
            };

            row.AddChild(label);
            row.AddChild(input);
            row.AddChild(bindBtn);
            row.AddChild(resetBtn);

            BindsListContainer.AddChild(row);
        }
    }

    private void StartListening(DiscoveredBind target)
    {
        if (_activeListeningTarget?.Field == target.Field)
        {
            StopListening();
            return;
        }

        StopListening();
        _activeListeningTarget = target;
        _listeningCooldown = 0.2f;

        target.BindButton.Text = "[Press Key...]";
        target.BindButton.AddStyleClass("ButtonColorGreen");
    }

    private void StopListening()
    {
        if (_activeListeningTarget == null)
            return;

        _activeListeningTarget.Value.BindButton.Text = "Bind";
        _activeListeningTarget.Value.BindButton.RemoveStyleClass("ButtonColorGreen");
        _activeListeningTarget = null;
        _listeningCooldown = 0f;
    }

    private void UnregisterCommandBinding(string command)
    {
        if (string.IsNullOrEmpty(command))
            return;

        var function = new BoundKeyFunction(command);

        // Remove any and all existing engine bindings for this command function
        while (true)
        {
            try
            {
                var existing = _inputManager.GetKeyBinding(function);
                _inputManager.RemoveBinding(existing);
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

    private void ApplyBind(DiscoveredBind target, RobustaKeybind bind)
    {
        target.Field.SetValue(null, bind);
        target.Input.Text = bind.IsValid ? bind.ToString() : "";

        if (!string.IsNullOrEmpty(target.Attribute.Command))
        {
            UnregisterCommandBinding(target.Attribute.Command);

            if (bind.IsValid && bind.BaseKey != Keyboard.Key.Unknown)
            {
                try
                {
                    _inputManager.RegisterBinding(new KeyBindingRegistration
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

        var parts = clean.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        var modifiers = new List<Keyboard.Key>();
        Keyboard.Key? baseKey = null;

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Equals("ctrl", StringComparison.OrdinalIgnoreCase)) p = "Control";
            if (p.Equals("cmd", StringComparison.OrdinalIgnoreCase) || p.Equals("win", StringComparison.OrdinalIgnoreCase)) p = "LSystem";
            // Should probably add something else too

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

    private void UpdateKeybindListening(FrameEventArgs args)
    {
        if (_activeListeningTarget == null)
            return;

        if (!BindsSection.VisibleInTree)
        {
            StopListening();
            return;
        }

        if (_listeningCooldown > 0f)
        {
            _listeningCooldown -= args.DeltaSeconds;
            return;
        }
        /*
        if (_inputManager.IsKeyDown(Keyboard.Key.Escape))
        {
            StopListening();
            return;
        }
        */ // Figure out how to not make this exit the menu
        if (_inputManager.IsKeyDown(Keyboard.Key.BackSpace) || _inputManager.IsKeyDown(Keyboard.Key.Delete))
        {
            ApplyBind(_activeListeningTarget.Value, new RobustaKeybind(Keyboard.Key.Unknown));
            StopListening();
            return;
        }

        var mod1 = Keyboard.Key.Unknown;
        var mod2 = Keyboard.Key.Unknown;
        var mod3 = Keyboard.Key.Unknown;

        void AddMod(Keyboard.Key mod)
        {
            if (mod1 == Keyboard.Key.Unknown) mod1 = mod;
            else if (mod2 == Keyboard.Key.Unknown) mod2 = mod;
            else if (mod3 == Keyboard.Key.Unknown) mod3 = mod;
        }

        if (_inputManager.IsKeyDown(Keyboard.Key.Shift)) AddMod(Keyboard.Key.Shift);
        if (_inputManager.IsKeyDown(Keyboard.Key.Control)) AddMod(Keyboard.Key.Control);
        if (_inputManager.IsKeyDown(Keyboard.Key.Alt)) AddMod(Keyboard.Key.Alt);

        foreach (Keyboard.Key key in Enum.GetValues<Keyboard.Key>())
        {
            if (IsModifierOrIgnoredKey(key))
                continue;

            if (_inputManager.IsKeyDown(key))
            {
                var newBind = new RobustaKeybind(key, mod1, mod2, mod3);
                ApplyBind(_activeListeningTarget.Value, newBind);
                StopListening();
                break;
            }
        }
    }
}