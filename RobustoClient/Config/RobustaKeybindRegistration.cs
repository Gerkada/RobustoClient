using Robust.Client.Input;
using RobustoClient.Reflection;

namespace RobustoClient;

/// <summary>
/// Builds an upstream <see cref="KeyBindingRegistration"/> from a <see cref="RobustaKeybind"/>.
/// </summary>
public static class RobustaKeybindRegistration
{
    public static KeyBindingRegistration Create(string function, RobustaKeybind bind, int priority)
    {
        var registration = new KeyBindingRegistration
        {
            Function = function,
            BaseKey = bind.BaseKey,
            Mod1 = bind.Mod1,
            Mod2 = bind.Mod2,
            Mod3 = bind.Mod3,
            Priority = priority,
            Type = KeyBindingType.Command
        };

        // Older engine compat.
        Reflect.TrySet(registration, "CommandWhenUIFocused", false, ReflectOptions.Any);

        return registration;
    }
}
