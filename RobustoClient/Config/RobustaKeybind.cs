using System.Text.Json.Serialization;
using Robust.Client.Input;

namespace RobustoClient;

/// <summary>
/// A key combination: one base key plus up to three held modifiers, mirroring upstream
/// <see cref="KeyBindingRegistration"/>'s <c>BaseKey</c>/<c>Mod1</c>/<c>Mod2</c>/<c>Mod3</c> model.
/// <see cref="Keyboard.Key.Unknown"/> means "no modifier".
/// </summary>
[JsonConverter(typeof(RobustaKeybindJsonConverter))]
public readonly record struct RobustaKeybind
{
    public Keyboard.Key BaseKey { get; }

    public Keyboard.Key Mod1 { get; }

    public Keyboard.Key Mod2 { get; }

    public Keyboard.Key Mod3 { get; }

    public RobustaKeybind(
        Keyboard.Key baseKey,
        Keyboard.Key mod1 = Keyboard.Key.Unknown,
        Keyboard.Key mod2 = Keyboard.Key.Unknown,
        Keyboard.Key mod3 = Keyboard.Key.Unknown)
    {
        // Modifiers are sorted like upstream so the order a user writes them in never changes identity.
        if (mod1 < mod2) (mod1, mod2) = (mod2, mod1);
        if (mod2 < mod3) (mod2, mod3) = (mod3, mod2);
        if (mod1 < mod2) (mod1, mod2) = (mod2, mod1);

        BaseKey = baseKey;
        Mod1 = mod1;
        Mod2 = mod2;
        Mod3 = mod3;
    }

    /// <summary>Whether the combination has a real base key and no repeated keys.</summary>
    public bool IsValid =>
        BaseKey != Keyboard.Key.Unknown
        && Enum.IsDefined(BaseKey)
        && !HasDuplicateKeys();

    /// <summary>True while every key of the combination is held.</summary>
    public bool IsDown(IInputManager input)
    {
        if (BaseKey == Keyboard.Key.Unknown || !input.IsKeyDown(BaseKey))
            return false;

        return (Mod1 == Keyboard.Key.Unknown || input.IsKeyDown(Mod1))
               && (Mod2 == Keyboard.Key.Unknown || input.IsKeyDown(Mod2))
               && (Mod3 == Keyboard.Key.Unknown || input.IsKeyDown(Mod3));
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Mod3 != Keyboard.Key.Unknown) parts.Add(Mod3.ToString());
        if (Mod2 != Keyboard.Key.Unknown) parts.Add(Mod2.ToString());
        if (Mod1 != Keyboard.Key.Unknown) parts.Add(Mod1.ToString());
        parts.Add(BaseKey.ToString());

        return string.Join("+", parts);
    }

    private bool HasDuplicateKeys()
    {
        return Duplicate(BaseKey, Mod1) || Duplicate(BaseKey, Mod2) || Duplicate(BaseKey, Mod3)
            || Duplicate(Mod1, Mod2) || Duplicate(Mod1, Mod3) || Duplicate(Mod2, Mod3);

        static bool Duplicate(Keyboard.Key a, Keyboard.Key b)
            => a != Keyboard.Key.Unknown && a == b;
    }

    /// <summary>
    /// Whether <paramref name="key"/> is a modifier. Modifiers are contiguous in
    /// <see cref="Keyboard.Key"/>, from <see cref="Keyboard.Key.Control"/> to <see cref="Keyboard.Key.RSystem"/>.
    /// </summary>
    public static bool IsModifier(Keyboard.Key key) =>
        key is >= Keyboard.Key.Control and <= Keyboard.Key.RSystem;

    /// <summary>
    /// Keys that a capture session consumes but never binds: modifier presses, the keys used to
    /// clear/cancel a capture, and mouse buttons, which cannot be bound from the Binds table.
    /// </summary>
    public static bool IsModifierOrIgnoredKey(Keyboard.Key key) =>
        key is Keyboard.Key.Unknown
            or Keyboard.Key.Escape
            or Keyboard.Key.BackSpace
            or Keyboard.Key.Delete
            or Keyboard.Key.MouseLeft
        || IsModifier(key);

    /// <summary>
    /// Builds a combination from a captured key press. Modifiers are collected in
    /// Shift/Control/Alt/System order; <see cref="RobustaKeybind"/> sorts them canonically on construction.
    /// </summary>
    public static RobustaKeybind FromCapture(Keyboard.Key key, bool shift, bool control, bool alt, bool system = false)
    {
        var mod1 = Keyboard.Key.Unknown;
        var mod2 = Keyboard.Key.Unknown;
        var mod3 = Keyboard.Key.Unknown;

        void AddMod(Keyboard.Key mod)
        {
            if (mod1 == Keyboard.Key.Unknown) mod1 = mod;
            else if (mod2 == Keyboard.Key.Unknown) mod2 = mod;
            else if (mod3 == Keyboard.Key.Unknown) mod3 = mod;
        }

        if (shift) AddMod(Keyboard.Key.Shift);
        if (control) AddMod(Keyboard.Key.Control);
        if (alt) AddMod(Keyboard.Key.Alt);
        if (system) AddMod(Keyboard.Key.LSystem);

        return new RobustaKeybind(key, mod1, mod2, mod3);
    }

    /// <summary>
    /// Parses a textual combination such as <c>"Shift+H"</c>, accepting <c>Ctrl</c>/<c>Cmd</c>/<c>Win</c>
    /// as aliases. Blank input and <c>none</c>/<c>unbound</c>/<c>unknown</c> all mean "no binding".
    /// </summary>
    /// <remarks>
    /// Only named keys are accepted. Bare integers are rejected even though
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would accept them, because a stray
    /// digit would otherwise silently bind an arbitrary enum value.
    /// </remarks>
    public static bool TryParse(string? text, out RobustaKeybind bind)
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

        var modifiers = new List<Keyboard.Key>(3);
        Keyboard.Key? baseKey = null;

        foreach (var rawPart in parts)
        {
            var part = rawPart;
            if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase))
                part = nameof(Keyboard.Key.Control);
            if (part.Equals("cmd", StringComparison.OrdinalIgnoreCase) || part.Equals("win", StringComparison.OrdinalIgnoreCase))
                part = nameof(Keyboard.Key.LSystem);

            // Reject numeric tokens; Enum.TryParse would otherwise map "7" onto an unrelated key.
            if (part.Length == 0 || char.IsDigit(part[0]))
                return false;

            if (!Enum.TryParse<Keyboard.Key>(part, true, out var key) || !Enum.IsDefined(key))
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

        // A combination needs a non-modifier base key. "Shift+Control" alone cannot trigger anything,
        // so it is refused rather than promoted into one.
        if (baseKey == null)
            return false;

        var mod1 = modifiers.Count > 0 ? modifiers[0] : Keyboard.Key.Unknown;
        var mod2 = modifiers.Count > 1 ? modifiers[1] : Keyboard.Key.Unknown;
        var mod3 = modifiers.Count > 2 ? modifiers[2] : Keyboard.Key.Unknown;

        var candidate = new RobustaKeybind(baseKey.Value, mod1, mod2, mod3);
        if (!candidate.IsValid)
            return false;

        bind = candidate;
        return true;
    }
}
