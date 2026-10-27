using Robust.Client.Input;
using Robust.Shared.ContentPack;

// Based on the original Arabica client by noverd. Massive thanks for the foundational framework.
namespace RobustoClient;

public sealed class EntryPoint : GameShared
{
    [Dependency] private readonly IInputManager _inputManager = default!;

    public override void PostInit()
    {
        RobustaConfig.Load();
        MarseyLogger.Debug("Building graph...");
        IoCManager.BuildGraph();
        MarseyLogger.Debug("Graph success built!");
        IoCManager.InjectDependencies(this);
        var registration = new KeyBindingRegistration
        {
            Function = "robusta.toggle_menu",
            BaseKey = Keyboard.Key.F4,
            Type = KeyBindingType.Command
        };
        _inputManager.RegisterBinding(registration, false);

        var autoConstructRegistration = new KeyBindingRegistration
        {
            Function = "robusta.auto_construct",
            BaseKey = RobustaConfig.AutoConstructKey,
            Type = KeyBindingType.Command
        };
        _inputManager.RegisterBinding(autoConstructRegistration, false);

        var autoDeconstructRegistration = new KeyBindingRegistration
        {
            Function = "robusta.auto_deconstruct",
            BaseKey = RobustaConfig.AutoDeconstructKey,
            Type = KeyBindingType.Command
        };
        _inputManager.RegisterBinding(autoDeconstructRegistration, false);

        var shiftDeconstructRegistration = new KeyBindingRegistration
        {
            Function = "robusta.auto_deconstruct",
            BaseKey = RobustaConfig.AutoConstructKey,
            Mod1 = Keyboard.Key.Shift,
            Priority = 10,
            Type = KeyBindingType.Command
        };
        _inputManager.RegisterBinding(shiftDeconstructRegistration, false);

        _inputManager.RegisterBinding(new KeyBindingRegistration
        {
            Function = "robusta.hack",
            BaseKey = RobustaConfig.HackingKey,
            Type = KeyBindingType.Command
        }, false);

        MarseyLogger.Debug("Setup context.");
    }
}