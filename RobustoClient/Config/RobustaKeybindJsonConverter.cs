using System.Text.Json;
using System.Text.Json.Serialization;
using Robust.Client.Input;

namespace RobustoClient;

/// <summary>
/// Persists a <see cref="RobustaKeybind"/> as <c>{ "key": "H", "mod1": "Shift" }</c>.
/// Also accepts a bare key number (the old config format) or a bare key name, so existing
/// config files keep loading unchanged.
/// </summary>
public sealed class RobustaKeybindJsonConverter : JsonConverter<RobustaKeybind>
{
    public override RobustaKeybind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Legacy and shorthand forms: "HackingKey": 17 or "HackingKey": "H".
        if (reader.TokenType is JsonTokenType.Number or JsonTokenType.String)
            return Validate(new RobustaKeybind(ReadKeyToken(ref reader)));

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected a key bind object, key name or key number, found {reader.TokenType}.");

        Keyboard.Key? baseKey = null;
        var mod1 = Keyboard.Key.Unknown;
        var mod2 = Keyboard.Key.Unknown;
        var mod3 = Keyboard.Key.Unknown;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Malformed key bind object.");

            var name = reader.GetString();
            reader.Read();

            switch (name?.ToLowerInvariant())
            {
                case "key":
                    baseKey = ReadKeyToken(ref reader);
                    break;
                case "mod1":
                    mod1 = ReadKeyToken(ref reader);
                    break;
                case "mod2":
                    mod2 = ReadKeyToken(ref reader);
                    break;
                case "mod3":
                    mod3 = ReadKeyToken(ref reader);
                    break;
                default:
                    reader.Skip(); // Unknown properties stay forward compatible.
                    break;
            }
        }

        if (baseKey == null)
            throw new JsonException("Key bind is missing a 'key'.");

        return Validate(new RobustaKeybind(baseKey.Value, mod1, mod2, mod3));
    }

    /// <summary>
    /// Validates the key combination. Allows Keyboard.Key.Unknown to represent unbound keys,
    /// while rejecting repeated modifier keys.
    /// </summary>
    private static RobustaKeybind Validate(RobustaKeybind bind)
    {
        if (bind.BaseKey == Keyboard.Key.Unknown)
            return bind;

        if (!bind.IsValid)
            throw new JsonException($"Invalid key bind '{bind}': repeated modifier keys detected.");

        return bind;
    }

    public override void Write(Utf8JsonWriter writer, RobustaKeybind value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("key", value.BaseKey.ToString());

        if (value.Mod1 != Keyboard.Key.Unknown)
            writer.WriteString("mod1", value.Mod1.ToString());
        if (value.Mod2 != Keyboard.Key.Unknown)
            writer.WriteString("mod2", value.Mod2.ToString());
        if (value.Mod3 != Keyboard.Key.Unknown)
            writer.WriteString("mod3", value.Mod3.ToString());

        writer.WriteEndObject();
    }

    private static Keyboard.Key ReadKeyToken(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var raw = reader.GetInt32();
            if (raw is < byte.MinValue or > byte.MaxValue)
                throw new JsonException($"Key number '{raw}' is out of range.");

            var key = (Keyboard.Key)(byte)raw;
            if (!Enum.IsDefined(key))
                throw new JsonException($"Unknown key number '{raw}'.");

            return key;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var name = reader.GetString();
            if (Enum.TryParse(name, true, out Keyboard.Key key) && Enum.IsDefined(key))
                return key;

            throw new JsonException($"Unknown key name '{name}'.");
        }

        throw new JsonException($"Expected a key name or number, found {reader.TokenType}.");
    }
}