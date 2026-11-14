using System.Numerics;

namespace RobustoClient.ImGui.Theme;

/// <summary>
/// Every colour used by any Robusta window. Grouped by role so a theme author can reason about
/// the palette without reading drawing code.
/// </summary>
public sealed class ThemeColors
{
    // Surfaces, from furthest back to closest
    public Vector4 Window { get; init; }
    public Vector4 Header { get; init; }
    public Vector4 TabStrip { get; init; }
    public Vector4 Card { get; init; }
    public Vector4 Popup { get; init; }

    // Controls
    public Vector4 Control { get; init; }
    public Vector4 ControlHover { get; init; }
    public Vector4 ControlActive { get; init; }

    // Text
    public Vector4 TextPrimary { get; init; }
    public Vector4 TextMuted { get; init; }
    public Vector4 TextDisabled { get; init; }

    // Lines
    public Vector4 Border { get; init; }
    public Vector4 Outline { get; init; }

    // Primary accent: split because a colour dark enough for white text is too dark to read as text.
    public Vector4 Primary { get; init; }
    public Vector4 PrimaryHover { get; init; }
    public Vector4 PrimaryText { get; init; }

    // Secondary accent, reserved for destructive actions so it stays meaningful
    public Vector4 Secondary { get; init; }
    public Vector4 SecondaryHover { get; init; }

    public Vector4 OnPrimary { get; init; }
    public Vector4 OnSecondary { get; init; }

    // Selection
    public Vector4 SelectedBg { get; init; }
    public Vector4 SelectedAccent { get; init; }

    // Tab strip
    public Vector4 TabHover { get; init; }
    public Vector4 TabSelectedBg { get; init; }
    public Vector4 TabText { get; init; }
    public Vector4 TabTextSelected { get; init; }

    // Semantic
    public Vector4 Success { get; init; }
    public Vector4 Warning { get; init; }
    public Vector4 Danger { get; init; }
    public Vector4 Info { get; init; }

    // Radar overlay
    public Vector4 RadarBgOuter { get; init; }
    public Vector4 RadarBgInner { get; init; }
    public Vector4 RadarRing { get; init; }
    public Vector4 RadarGrid { get; init; }

    // Contraband severity scale
    public Vector4 SeveritySyndicate { get; init; }
    public Vector4 SeverityGrandTheft { get; init; }
    public Vector4 SeverityMajor { get; init; }
    public Vector4 SeverityRestricted { get; init; }
    public Vector4 SeverityMinor { get; init; }
    public Vector4 SeverityUnknown { get; init; }

    // Scrollbar
    public Vector4 ScrollbarBg { get; init; }
    public Vector4 ScrollbarGrab { get; init; }
    public Vector4 ScrollbarGrabHovered { get; init; }

    /// <summary>Independent copy, so palette edits cannot leak back into a shared instance.</summary>
    public ThemeColors Copy() => (ThemeColors)MemberwiseClone();
}
