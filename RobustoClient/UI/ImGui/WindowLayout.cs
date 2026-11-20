using System;
using System.Collections.Generic;
using System.Numerics;

namespace RobustoClient.UI.ImGui;

/// <summary>
/// Window ids the layout store keys on. Each window builds its ImGui title from the same constant, so
/// a placement can never be filed under an id no window uses.
/// </summary>
internal static class WindowLayoutId
{
    internal const string Main = "MainWindow";
    internal const string Radar = "RadarWin";
    internal const string InventoryViewer = "InvViewerWin";
    internal const string AutoChem = "AutoChemWin";
}

/// <summary>
/// Remembers where each window was left. <c>io.IniFilename</c> is null, so ImGui persists nothing and
/// every window would reopen at its first-run default.
/// </summary>
internal static class WindowLayout
{
    /// <summary>Movement below this many pixels is drag jitter, not a new position.</summary>
    private const float Tolerance = 0.5f;

    /// <summary>A drag or resize changed the geometry and has not been written yet.</summary>
    private static bool _pending;

    private static Dictionary<string, RobustaConfig.WindowPlacement> Placements =>
        RobustaConfig.WindowPlacements ??= new Dictionary<string, RobustaConfig.WindowPlacement>();

    /// <summary>The saved placement for a window, or null when it has never been placed.</summary>
    internal static RobustaConfig.WindowPlacement? Get(string windowId) =>
        Placements.TryGetValue(windowId, out var placement) &&
        placement.Width > 0f && placement.Height > 0f
            ? placement
            : null;

    /// <summary>
    /// Stores the window's geometry when it moved. Returns true when anything changed.
    /// </summary>
    internal static bool Remember(string windowId, Vector2 position, Vector2 size)
    {
        var placement = Get(windowId);

        if (placement != null &&
            MathF.Abs(placement.X - position.X) < Tolerance &&
            MathF.Abs(placement.Y - position.Y) < Tolerance &&
            MathF.Abs(placement.Width - size.X) < Tolerance &&
            MathF.Abs(placement.Height - size.Y) < Tolerance)
        {
            return false;
        }

        Placements[windowId] = new RobustaConfig.WindowPlacement
        {
            X = position.X,
            Y = position.Y,
            Width = size.X,
            Height = size.Y
        };

        _pending = true;
        RobustaConfig.MarkDirty();
        return true;
    }

    /// <summary>
    /// Keeps a placement inside the display. A window dragged partly off screen and reopened at a
    /// smaller resolution would otherwise come back where it cannot be grabbed.
    /// </summary>
    internal static Vector2 ClampPosition(RobustaConfig.WindowPlacement placement, Vector2 displaySize) =>
        new(
            Math.Clamp(placement.X, 0f, MathF.Max(0f, displaySize.X - placement.Width)),
            Math.Clamp(placement.Y, 0f, MathF.Max(0f, displaySize.Y - placement.Height)));

    /// <summary>Writes the config once a drag or resize has finished, not once per frame of it.</summary>
    internal static void FlushIfPending()
    {
        if (!_pending)
            return;

        _pending = false;
        RobustaConfig.SaveIfDirty();
    }
}
