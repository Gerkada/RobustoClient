using System;
using System.Numerics;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using RobustoClient.ImGui;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.UI.ImGui.Radial;

/// <summary>
/// Hold-to-open action wheel for a user-created radial menu. Centred on the cursor when the key goes
/// down, the release fires the sector under the pointer against the captured target.
/// </summary>
public sealed class RadialMenuImGui
{
    private const float MinRadius = 32f;
    private const float RadiusPerLine = 6f;
    private const float DeadZoneRatio = 0.15f;

    /// <summary>Overall wheel size relative to the text-derived radius.</summary>
    private const float WheelScale = .45f;

    /// <summary>Upper bound on a sector label, relative to the body text size.</summary>
    private const float MaxLabelScale = 2f;

    /// <summary>Lower bound, so a long label shrinks to fit but stays legible.</summary>
    private const float MinLabelScale = 0.6f;

    /// <summary>Fraction of the sector a label may fill, leaving a margin at the edges.</summary>
    private const float LabelFit = 0.8f;

    private bool _active;
    private RobustaConfig.RadialMenuSettings? _settings;
    private RobustaKeybind _ownerBind;
    private Vector2 _center;
    private float _radius;
    private float _deadZone;
    private int _count;

    public bool IsActive => _active;

    /// <summary>Drops null wheels and gives every remaining wheel its four editable rows.</summary>
    internal static void Ensure()
    {
        RobustaConfig.Radials ??= new();

        for (int i = RobustaConfig.Radials.Count - 1; i >= 0; i--)
        {
            if (RobustaConfig.Radials[i] == null)
                RobustaConfig.Radials.RemoveAt(i);
            else
                RobustaConfig.Radials[i].EnsureDefaults();
        }
    }

    /// <summary>Whether a wheel already owns this combination.</summary>
    internal static bool OwnsKey(RobustaKeybind key)
    {
        foreach (var radial in RobustaConfig.Radials ?? [])
        {
            if (radial != null && radial.Key == key)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Opens while a radial bind is held and commits on release. Runs before the frame loop's
    /// visibility early-out, because nothing renders while no window is open.
    /// </summary>
    public void PollHold(IInputManager input, IUserInterfaceManager ui)
    {
        if (_active)
        {
            if (_ownerBind.IsDown(input))
                return;

            var screen = input.MouseScreenPosition;
            if (screen.IsValid)
                Commit(screen.Position);
            else
                Cancel();

            return;
        }

        if (ui.KeyboardFocused != null ||
            RobustaImGuiManager.CheatMenu.IsListeningForKeybind ||
            (RobustaImGuiManager.IsInitialized && GetIO().WantTextInput))
        {
            return;
        }

        // A plain key also reads as down while a modified form is held, so the most specific match
        // has to win, or a Shift+R wheel never opens while a plain R wheel sits earlier in the list.
        RobustaConfig.RadialMenuSettings? match = null;
        int matchSpecificity = 0;

        foreach (var radial in RobustaConfig.Radials)
        {
            if (radial == null || radial.ActiveCount < 2 || !radial.Key.IsValid)
                continue;

            if (!radial.Key.IsDown(input))
                continue;

            int specificity = radial.Key.Specificity;
            if (match != null && specificity <= matchSpecificity)
                continue;

            match = radial;
            matchSpecificity = specificity;
        }

        if (match != null)
            Begin(match, match.Key, input.MouseScreenPosition);
    }

    /// <summary>Drops an open session without firing anything.</summary>
    public void Cancel()
    {
        if (!_active)
            return;

        ReleasePointer();
        _active = false;
        _settings = null;
    }

    public void Draw()
    {
        if (!_active || _settings == null)
            return;

        int hovered = Resolve(GetIO().MousePos - _center, _deadZone, _count);
        var drawList = GetForegroundDrawList();
        var colors = RobustaImGuiTheme.Colors;

        // Backing under each sector, so the semi-transparent fills read over bright game content.
        // Drawn per sector rather than as one full ring: a single full-turn polygon has to close with
        // a slit, which left a wedge of the ring uncovered on the right. The per-sector seams land
        // under the separator lines drawn below.
        for (int i = 0; i < _count; i++)
        {
            var (from, to) = SectorAngles(i, _count);
            FillAnnulus(from, to, RobustaImGuiTheme.ColU32(WithAlpha(colors.Card, 0.92f)));
        }

        for (int i = 0; i < _count; i++)
        {
            var (from, to) = SectorAngles(i, _count);
            var fill = i == hovered ? colors.Primary : colors.Control;
            FillAnnulus(
                from,
                to,
                RobustaImGuiTheme.ColU32(WithAlpha(fill, i == hovered ? 0.85f : 0.55f)));
        }

        // Separators sit on the sector boundaries, so the wheel reads as distinct choices.
        for (int i = 0; i < _count; i++)
        {
            var (from, _) = SectorAngles(i, _count);
            var direction = new Vector2(MathF.Cos(from), MathF.Sin(from));
            drawList.AddLine(
                _center + direction * _deadZone,
                _center + direction * _radius,
                RobustaImGuiTheme.ColU32(colors.Border),
                1.5f);
        }

        bool inGap = hovered < 0;
        drawList.AddCircle(
            _center,
            _deadZone,
            RobustaImGuiTheme.ColU32(inGap ? colors.Danger : colors.Border),
            48,
            2f);

        float cross = _deadZone * 0.45f;
        var crossColor = RobustaImGuiTheme.ColU32(inGap ? colors.Danger : colors.TextMuted);
        drawList.AddLine(_center + new Vector2(-cross, -cross), _center + new Vector2(cross, cross), crossColor, 2f);
        drawList.AddLine(_center + new Vector2(-cross, cross), _center + new Vector2(cross, -cross), crossColor, 2f);

        drawList.AddCircle(_center, _radius, RobustaImGuiTheme.ColU32(colors.Border), 64, 2f);

        // Each label is sized to its own sector: the chord bounds the width, the ring width bounds
        // the height, and the result is clamped so a short label does not grow past MaxLabelScale.
        bool hasFont = !RobustaImGuiTheme.FontRegular.IsNull;
        float labelRadius = (_deadZone + _radius) * 0.5f;
        float ringWidth = _radius - _deadZone;

        for (int i = 0; i < _count; i++)
        {
            var (from, to) = SectorAngles(i, _count);
            string text = RadialMenuActions.Display(_settings.ActiveAt(i));
            if (string.IsNullOrEmpty(text))
                continue;

            float chord = 2f * labelRadius * MathF.Sin((to - from) * 0.5f);
            float fontSize = LabelFontSize(text, chord, ringWidth, hasFont);

            if (hasFont)
                PushFont(RobustaImGuiTheme.FontRegular, fontSize);

            string label = FitLabel(text, chord);
            var measured = CalcTextSize(label);
            float mid = (from + to) * 0.5f;
            var position = _center + new Vector2(MathF.Cos(mid), MathF.Sin(mid)) * labelRadius;
            drawList.AddText(
                position - measured * 0.5f,
                RobustaImGuiTheme.ColU32(i == hovered ? colors.TextPrimary : colors.TextMuted),
                label);

            if (hasFont)
                PopFont();
        }

        // Annular sector as one concave polygon. Filling it as separate quads leaves an
        // anti-aliased seam along every shared edge, which reads as faint lines across the sector.
        // Callers pass a sector, never a full turn: a full turn would have to repeat its first point.
        unsafe void FillAnnulus(float from, float to, uint color, int segments = 16)
        {
            int pointCount = (segments + 1) * 2;
            Vector2* points = stackalloc Vector2[pointCount];

            for (int i = 0; i <= segments; i++)
            {
                float angle = from + (to - from) * i / segments;
                var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                points[i] = _center + direction * _radius;
                points[pointCount - 1 - i] = _center + direction * _deadZone;
            }

            drawList.AddConcavePolyFilled(points, pointCount, color);
        }
    }

    /// <summary>
    /// Sector index under <paramref name="delta"/>, or -1 inside the close gap. The layout depends
    /// on the action count, so the same point can map to different sectors for different wheels.
    /// </summary>
    internal static int Resolve(Vector2 delta, float deadZone, int count)
    {
        if (delta.LengthSquared() < deadZone * deadZone)
            return -1;

        if (count <= 2)
            return delta.X >= 0f ? 1 : 0;

        if (count == 3)
        {
            float degrees = (MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI) + 360f) % 360f;
            if (degrees >= 210f && degrees < 330f)
                return 0;
            if (degrees >= 90f && degrees < 210f)
                return 1;
            return 2;
        }

        return MathF.Abs(delta.X) > MathF.Abs(delta.Y)
            ? (delta.X > 0f ? 3 : 2)
            : (delta.Y > 0f ? 1 : 0);
    }

    /// <summary>Screen-space angles for one sector, 0 = right, increasing clockwise.</summary>
    internal static (float From, float To) SectorAngles(int index, int count)
    {
        const float deg = MathF.PI / 180f;

        return count switch
        {
            2 => index == 0 ? (90f * deg, 270f * deg) : (270f * deg, 450f * deg),
            3 => index switch
            {
                0 => (210f * deg, 330f * deg),
                1 => (90f * deg, 210f * deg),
                _ => (330f * deg, 450f * deg)
            },
            _ => index switch
            {
                0 => (225f * deg, 315f * deg),
                1 => (45f * deg, 135f * deg),
                2 => (135f * deg, 225f * deg),
                _ => (315f * deg, 405f * deg)
            }
        };
    }

    private void Begin(RobustaConfig.RadialMenuSettings settings, RobustaKeybind bind, ScreenCoordinates screen)
    {
        if (!screen.IsValid)
            return;

        _settings = settings;
        _ownerBind = bind;
        _center = screen.Position;
        float lineHeight = RobustaImGuiTheme.Typography.RegularSize * 1.2f;
        _radius = MathF.Max(MinRadius, lineHeight * RadiusPerLine) * WheelScale;
        _deadZone = MathF.Max(RobustaImGuiScale.MinTargetSize * 0.5f, _radius * DeadZoneRatio);
        _count = Math.Clamp(settings.ActiveCount, 2, 4);
        _active = true;

        if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
            entMan.System<MouseUtilitySystem>().CapturePointer();
    }

    private void Commit(Vector2 cursor)
    {
        int index = Resolve(cursor - _center, _deadZone, _count);
        string action = index >= 0 && _settings != null ? _settings.ActiveAt(index) : "";

        try
        {
            if (!string.IsNullOrWhiteSpace(action))
                RadialMenuActions.Execute(action);
        }
        finally
        {
            ReleasePointer();
            _active = false;
            _settings = null;
        }
    }

    private static void ReleasePointer()
    {
        if (RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan))
            entMan.System<MouseUtilitySystem>().ReleasePointer();
    }

    /// <summary>
    /// Largest font size at which <paramref name="text"/> fits a sector of the given chord and ring
    /// width, clamped to <see cref="MinLabelScale"/>..<see cref="MaxLabelScale"/> of the body size.
    /// </summary>
    private static float LabelFontSize(string text, float maxWidth, float maxHeight, bool hasFont)
    {
        float baseSize = RobustaImGuiTheme.Typography.RegularSize;
        if (!hasFont)
            return baseSize;

        PushFont(RobustaImGuiTheme.FontRegular, baseSize);
        var measured = CalcTextSize(text);
        PopFont();

        if (measured.X <= 0f || measured.Y <= 0f)
            return baseSize;

        float scale = MathF.Min(maxWidth / measured.X, maxHeight / measured.Y) * LabelFit;
        return baseSize * Math.Clamp(scale, MinLabelScale, MaxLabelScale);
    }

    private static string FitLabel(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || CalcTextSize(text).X <= maxWidth)
            return text;

        for (int length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + "..";
            if (CalcTextSize(candidate).X <= maxWidth)
                return candidate;
        }

        // Nothing fits with an ellipsis. One character beats an empty sector.
        return text[..1];
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);
}
