using System.Reflection;
using Robust.Client.Input;
using Robust.Shared.ContentPack;
using RobustoClient.UI.ImGui.Radial;

// Based on the original Arabica client by noverd. Massive thanks for the foundational framework.
namespace RobustoClient;

public sealed class EntryPoint : GameShared
{
    [Dependency] private readonly IInputManager _inputManager = default!;

    // Overwrites upstream similar keybinds
    private const int CommandBindingPriority = 10;

    public override void PostInit()
    {
        RobustaConfig.Load();
        MarseyLogger.Info($"Config: {RobustaConfig.ConfigPath}");
        MarseyLogger.Debug("Building graph...");
        IoCManager.BuildGraph();
        MarseyLogger.Debug("Graph success built!");
        IoCManager.InjectDependencies(this);

        // Register all command keybinds from RobustaConfig
        RegisterDiscoveredCommands();

        MarseyLogger.Debug("Setup context.");
    }

    private void RegisterDiscoveredCommands()
    {
        var fields = typeof(RobustaConfig).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var field in fields)
        {
            if (field.FieldType != typeof(RobustaKeybind))
                continue;

            var attr = field.GetCustomAttribute<KeybindConfigAttribute>();
            if (attr == null || string.IsNullOrEmpty(attr.Command))
                continue;

            var bind = (RobustaKeybind)field.GetValue(null)!;
            if (!bind.IsValid || bind.BaseKey == Keyboard.Key.Unknown)
                continue;

            // A wheel owns its key, registering the command too would fire both on key-down.
            if (RadialMenuImGui.OwnsKey(bind))
                continue;

            RegisterCommand(attr.Command, bind);
        }
    }

    private void RegisterCommand(string function, RobustaKeybind bind)
    {
        try
        {
            _inputManager.RegisterBinding(RobustaKeybindRegistration.Create(function, bind, CommandBindingPriority), false);
            MarseyLogger.Info($"Bound {function} to {bind}.");
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"Failed to bind {function} to {bind}: {ex.Message}");
        }
    }
}