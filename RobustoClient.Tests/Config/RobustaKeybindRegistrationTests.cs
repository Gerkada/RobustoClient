using FluentAssertions;
using Robust.Client.Input;
using Robust.Shared.Input;
using Xunit;

namespace RobustoClient.Tests.Config;

/// <summary>
/// Pins the registration <see cref="RobustaKeybindRegistration.Create"/> builds, including the
/// <c>CommandWhenUIFocused</c> write that has to survive engines older than v269.
/// </summary>
public class RobustaKeybindRegistrationTests
{
    [Fact]
    public void Create_CopiesTheCombinationAndPriority()
    {
        var bind = new RobustaKeybind(Keyboard.Key.H, Keyboard.Key.Shift);

        var registration = RobustaKeybindRegistration.Create("robusta.test", bind, 10);

        registration.Function.Should().Be(new BoundKeyFunction("robusta.test"));
        registration.BaseKey.Should().Be(Keyboard.Key.H);
        registration.Mod1.Should().Be(Keyboard.Key.Shift);
        registration.Mod2.Should().Be(Keyboard.Key.Unknown);
        registration.Mod3.Should().Be(Keyboard.Key.Unknown);
        registration.Priority.Should().Be(10);
        registration.Type.Should().Be(KeyBindingType.Command);
    }

    [Fact]
    public void Create_DisablesTheCommandWhileUIFocused()
    {
        var registration = RobustaKeybindRegistration.Create(
            "robusta.test", new RobustaKeybind(Keyboard.Key.H), 10);

        registration.CommandWhenUIFocused.Should().BeFalse();
    }
}
