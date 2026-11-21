using System;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Canonical JSON key for every color token, plus reflection-based read/write.
/// Adding a color means adding a <see cref="ThemeColors"/> property, a constant here, and a
/// matching entry in both built-in JSON files — <c>ThemeJsonTests</c> enforces all three.
/// </summary>
public static class ThemeColorTokens
{
    public const string Window = "window";
    public const string Header = "header";
    public const string TabStrip = "tabStrip";
    public const string Card = "card";
    public const string Popup = "popup";

    public const string Control = "control";
    public const string ControlHover = "controlHover";
    public const string ControlActive = "controlActive";

    public const string TextPrimary = "textPrimary";
    public const string TextMuted = "textMuted";
    public const string TextDisabled = "textDisabled";

    public const string Border = "border";
    public const string Outline = "outline";

    public const string Primary = "primary";
    public const string PrimaryHover = "primaryHover";
    public const string PrimaryText = "primaryText";

    public const string Secondary = "secondary";
    public const string SecondaryHover = "secondaryHover";

    public const string OnPrimary = "onPrimary";
    public const string OnSecondary = "onSecondary";

    public const string SelectedBg = "selectedBg";
    public const string SelectedAccent = "selectedAccent";

    public const string TabHover = "tabHover";
    public const string TabSelectedBg = "tabSelectedBg";
    public const string TabText = "tabText";
    public const string TabTextSelected = "tabTextSelected";

    public const string Success = "success";
    public const string Warning = "warning";
    public const string Danger = "danger";
    public const string Info = "info";

    public const string RadarBgOuter = "radarBgOuter";
    public const string RadarBgInner = "radarBgInner";
    public const string RadarRing = "radarRing";
    public const string RadarGrid = "radarGrid";

    public const string SeveritySyndicate = "severitySyndicate";
    public const string SeverityGrandTheft = "severityGrandTheft";
    public const string SeverityMajor = "severityMajor";
    public const string SeverityRestricted = "severityRestricted";
    public const string SeverityMinor = "severityMinor";
    public const string SeverityUnknown = "severityUnknown";

    public const string AntagonistNinja = "antagonistNinja";
    public const string AntagonistDragon = "antagonistDragon";
    public const string AntagonistChangeling = "antagonistChangeling";
    public const string AntagonistClone = "antagonistClone";
    public const string AntagonistRatKing = "antagonistRatKing";
    public const string AntagonistNukie = "antagonistNukie";
    public const string AntagonistThief = "antagonistThief";
    public const string AntagonistZombie = "antagonistZombie";
    public const string AntagonistInitialInfected = "antagonistInitialInfected";
    public const string AntagonistHeadRev = "antagonistHeadRev";
    public const string AntagonistRev = "antagonistRev";
    public const string AntagonistWizard = "antagonistWizard";

    public const string ScrollbarBg = "scrollbarBg";
    public const string ScrollbarGrab = "scrollbarGrab";
    public const string ScrollbarGrabHovered = "scrollbarGrabHovered";

    /// <summary>All color tokens, in presentation order. Drives JSON validation and the editor UI.</summary>
    public static readonly string[] All =
    [
        Window, Header, TabStrip, Card, Popup,
        Control, ControlHover, ControlActive,
        TextPrimary, TextMuted, TextDisabled,
        Border, Outline,
        Primary, PrimaryHover, PrimaryText,
        Secondary, SecondaryHover,
        OnPrimary, OnSecondary,
        SelectedBg, SelectedAccent,
        TabHover, TabSelectedBg, TabText, TabTextSelected,
        Success, Warning, Danger, Info,
        RadarBgOuter, RadarBgInner, RadarRing, RadarGrid,
        SeveritySyndicate, SeverityGrandTheft, SeverityMajor,
        SeverityRestricted, SeverityMinor, SeverityUnknown,
        AntagonistNinja, AntagonistDragon, AntagonistChangeling, AntagonistClone,
        AntagonistRatKing, AntagonistNukie, AntagonistThief,
        AntagonistZombie, AntagonistInitialInfected, AntagonistHeadRev, AntagonistRev, AntagonistWizard,
        ScrollbarBg, ScrollbarGrab, ScrollbarGrabHovered
    ];

    /// <summary>Groups tokens for the theme editor and JSON layout.</summary>
    public static readonly (string Section, string[] Tokens)[] Sections =
    [
        ("SURFACES", [Window, Header, TabStrip, Card, Popup]),
        ("CONTROLS", [Control, ControlHover, ControlActive]),
        ("TEXT COLORS", [TextPrimary, TextMuted, TextDisabled]),
        ("LINES", [Border, Outline]),
        ("ACCENTS", [Primary, PrimaryHover, PrimaryText, Secondary, SecondaryHover, OnPrimary, OnSecondary]),
        ("SELECTION", [SelectedBg, SelectedAccent]),
        ("TAB STRIP", [TabHover, TabSelectedBg, TabText, TabTextSelected]),
        ("SEMANTIC", [Success, Warning, Danger, Info]),
        ("RADAR", [RadarBgOuter, RadarBgInner, RadarRing, RadarGrid]),
        ("SEVERITY", [SeveritySyndicate, SeverityGrandTheft, SeverityMajor, SeverityRestricted, SeverityMinor, SeverityUnknown]),
        ("SCROLLBAR", [ScrollbarBg, ScrollbarGrab, ScrollbarGrabHovered])
    ];

    /// <summary>
    /// Sections the VISUALS tab owns instead of the THEMES tab. Antagonist colors are a gameplay
    /// readout, so they sit beside the ESP and role colors rather than in the theme editor.
    /// </summary>
    public static readonly (string Section, string[] Tokens)[] VisualsSections =
    [
        ("ANTAGONISTS", [AntagonistNinja, AntagonistDragon, AntagonistChangeling, AntagonistClone, AntagonistRatKing, AntagonistNukie, AntagonistThief, AntagonistZombie, AntagonistInitialInfected, AntagonistHeadRev, AntagonistRev, AntagonistWizard])
    ];

    /// <summary>
    /// Readable label for a token: <c>antagonistNinja</c> reads as <c>Ninja</c>. The
    /// <c>antagonist</c> prefix is redundant under the ANTAGONISTS heading, so it is dropped.
    /// </summary>
    public static string LabelFor(string token)
    {
        const string prefix = "antagonist";

        var text = token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   token.Length > prefix.Length
            ? token[prefix.Length..]
            : token;

        var builder = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && !char.IsUpper(text[i - 1]))
                builder.Append(' ');

            builder.Append(text[i]);
        }

        return builder.ToString();
    }

    private static readonly Dictionary<string, PropertyInfo> Properties = BuildMap();

    private static Dictionary<string, PropertyInfo> BuildMap()
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in typeof(ThemeColors).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(Vector4))
                continue;

            // The JSON key is the property name with a lower-case first letter.
            var key = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            map[key] = prop;
        }

        return map;
    }

    /// <summary>Resolves a token to its backing property. False when the token is unknown.</summary>
    public static bool TryGetProperty(string token, out PropertyInfo property) =>
        Properties.TryGetValue(token, out property!);

    /// <summary>Every color property on <see cref="ThemeColors"/> as (token, property).</summary>
    public static IEnumerable<(string Token, PropertyInfo Property)> AllProperties() =>
        Properties.Select(kv => (kv.Key, kv.Value));
}
