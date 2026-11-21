using System.Numerics;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Security;
using Content.Shared.Silicons.Borgs.Components;
using Hexa.NET.ImGui;
using static Hexa.NET.ImGui.ImGui;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using RobustoClient.ImGui;
using RobustoClient.Systems;
using RobustoClient.Systems.PlayerState;

namespace RobustoClient.UI.ImGui;

public enum RadarMarkerType { Player, Mob }

public sealed class RadarImGui
{
    /// <summary>Window title plus the layout store's id.</summary>
    private const string WindowTitle = "Robusta Tactical Radar##" + WindowLayoutId.Radar;

    public bool IsOpen;

    private float _rangeMeters = 30f;
    // Off by default, and session scoped like the other radar toggles.
    private bool _showMobs;
    private bool _showBorgs = true;
    private bool _showTheftTags = true;
    private bool _showSyndicateTags = true;
    private bool _showLiveStatus = true;
    private bool _showWantedTag = true;
    private bool _showEmaggedTag = true;
    private int _frameCounter;
    private const int UpdateInterval = 10;

    /// <summary>Range band the slider and wheel zoom share.</summary>
    private const float MinRangeMeters = 15f;
    private const float MaxRangeMeters = 60f;

    /// <summary>Meters per wheel notch.</summary>
    private const float ZoomMetersPerNotch = 3f;

    private readonly List<RadarMarker> _markers = new();

    /// <summary>Reused scan buffer. The typed query already returns only mobs.</summary>
    private readonly HashSet<Entity<MobStateComponent>> _mobScanBuffer = new();

    /// <summary>Entities whose scan already threw, so each is logged once.</summary>
    private readonly HashSet<EntityUid> _faultedEntities = new();

    /// <summary>
    /// One blip. Position is read from the transform every frame, so a moving blip cannot lag behind a
    /// throttled scan. <c>Dead</c> is captured because the Mobs switch hides corpses too.
    /// </summary>
    private sealed record RadarMarker(
        EntityUid Uid,
        Vector4 Color,
        string Name,
        IReadOnlyList<(string Text, uint Color)> Tags,
        RadarMarkerType Type,
        bool Interesting,
        bool Dead);

    /// <summary>Blips and labels draw just under body text, so a crowded room stays readable.</summary>
    private const float BlipScale = 0.9375f;

    /// <summary>Gap between tags, and between the hovered name and the first tag.</summary>
    private const float TagGap = 3f;

    /// <summary>Radar window position, so the info panel can dock beside it.</summary>
    public Vector2? WindowPos { get; private set; }

    /// <summary>Radar window size, so the info panel can dock beside it.</summary>
    public Vector2? WindowSize { get; private set; }

    public void Draw()
    {
        if (WindowLayout.Get(WindowLayoutId.Radar) is { } placement)
        {
            SetNextWindowPos(WindowLayout.ClampPosition(placement, GetIO().DisplaySize), ImGuiCond.FirstUseEver);
            SetNextWindowSize(new Vector2(placement.Width, placement.Height), ImGuiCond.FirstUseEver);
        }
        else
        {
            SetNextWindowSize(new Vector2(408f, 493f), ImGuiCond.FirstUseEver);
        }

        SetNextWindowSizeConstraints(new Vector2(204f, 246f), new Vector2(595f, 637f));

        SetNextWindowBgAlpha(RobustaConfig.RadarWindowOpacity);

        if (!Begin(WindowTitle, ref IsOpen, ImGuiWindowFlags.None))
        {
            End();
            return;
        }

        DrawControlStrip();

        var avail = GetContentRegionAvail();

        // Both axes are floored because a collapsed window reports a zero content region, which
        // InvisibleButton asserts on.
        float width = MathF.Max(80f, avail.X);
        float side = MathF.Max(80f, MathF.Min(avail.X, avail.Y));
        var cursor = GetCursorScreenPos();
        var center = cursor + new Vector2(width * 0.5f, side * 0.5f);
        float radius = (side * 0.5f) - 4f;

        InvisibleButton("##RadarCanvas", new Vector2(width, side));

        // ImGui does not swallow this click, so a blip can also be opened by pressing on it.
        var canvasClicked = IsItemClicked();
        var mousePos = GetIO().MousePos;

        // Over the map a notch means distance, not scrolling.
        var wheel = GetIO().MouseWheel;
        if (IsItemHovered() && wheel != 0f)
        {
            _rangeMeters = ZoomedRange(_rangeMeters, wheel);

            // Markers rescan on an interval, a zoom has to move the ring this frame.
            _frameCounter = UpdateInterval;
        }

        var dl = GetWindowDrawList();
        var rc = RobustaImGuiTheme.Colors;
        uint bgOuter = RobustaImGuiTheme.ColU32(rc.RadarBgOuter);
        uint bgInner = RobustaImGuiTheme.ColU32(rc.RadarBgInner);
        uint ringCol = RobustaImGuiTheme.ColU32(rc.RadarRing);
        uint gridCol = RobustaImGuiTheme.ColU32(rc.RadarGrid);

        dl.AddCircleFilled(center, radius + 2f, bgOuter, 64);
        dl.AddCircleFilled(center, radius, bgInner, 64);
        dl.AddCircle(center, radius, ringCol, 64, 1.5f);
        dl.AddCircle(center, radius * 0.5f, gridCol, 48, 1.0f);

        dl.AddLine(center - new Vector2(radius, 0f), center + new Vector2(radius, 0f), gridCol, 1f);
        dl.AddLine(center - new Vector2(0f, radius), center + new Vector2(0f, radius), gridCol, 1f);

        if (!RobustaImGuiManager.TryResolve<IEntityManager>(out var entMan) ||
            !RobustaImGuiManager.TryResolve<IPlayerManager>(out var playerMan) ||
            !RobustaImGuiManager.TryResolve<IEyeManager>(out var eyeMan))
        {
            End();
            return;
        }

        var localPlayer = playerMan.LocalSession?.AttachedEntity;
        if (localPlayer == null || !entMan.TryGetComponent<TransformComponent>(localPlayer.Value, out var xform))
        {
            End();
            return;
        }

        var transformSys = entMan.System<SharedTransformSystem>();
        var xformQuery = entMan.GetEntityQuery<TransformComponent>();

        // A parent that left PVS mid-frame makes GetWorldPosition throw, and an exception inside the
        // ImGui frame aborts the client. Skip the frame instead.
        if (!CanResolveWorldPosition(xformQuery, xform))
        {
            End();
            return;
        }

        var playerPos = transformSys.GetWorldPosition(xform);
        var mapId = xform.MapID;

        _frameCounter++;
        if (_frameCounter >= UpdateInterval)
        {
            _frameCounter = 0;
            UpdateMarkers(entMan, localPlayer.Value, playerPos, mapId);
        }

        var playerRot = transformSys.GetWorldRotation(xform);

        // Same PVS hazard: a grid that left view between the transform read and here makes the
        // throwing rotation lookup blow up mid-frame.
        var gridRot = xform.GridUid is { } gridUid && xformQuery.HasComponent(gridUid)
            ? transformSys.GetWorldRotation(gridUid)
            : Angle.Zero;

        // Camera-up only, a second orientation control was one more click for no extra information.
        var radarRot = eyeMan.CurrentEye?.Rotation ?? Angle.Zero;

        var pCol = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentCyan);
        dl.AddCircleFilled(center, 4f, pCol, 16);

        var relForward = radarRot.RotateVec(playerRot.ToWorldVec());
        var needleVec = new Vector2(relForward.X, -relForward.Y);
        dl.AddLine(center, center + needleVec * 9f, pCol, 2f);

        float scale = radius / _rangeMeters;

        var stationNorthWorld = gridRot.RotateVec(new Vector2(0f, 1f));
        var northDir = radarRot.RotateVec(stationNorthWorld);
        var northPos = center + new Vector2(northDir.X, -northDir.Y) * (radius - 12f);
        var nSize = CalcTextSize("N");
        dl.AddText(northPos - nSize * 0.5f, RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AccentRed), "N");

        EntityUid? hoveredUid = null;

        // Shrinks the dots and their labels, AddText and CalcTextSize both read the pushed size.
        PushFont(RobustaImGuiTheme.FontRegular, RobustaImGuiTheme.Typography.RegularSize * BlipScale);

        foreach (var marker in _markers)
        {
            if (!IsMarkerVisible(marker.Type, marker.Dead, _showMobs))
                continue;

            // Position is read per frame, only the info behind the blip is throttled. A blip that
            // left PVS between scans has no transform and simply is not drawn.
            if (!xformQuery.TryGetComponent(marker.Uid, out var markerXform) ||
                markerXform.MapID != mapId ||
                !CanResolveWorldPosition(xformQuery, markerXform))
            {
                continue;
            }

            var offsetMeters = transformSys.GetWorldPosition(markerXform) - playerPos;
            var (uiOffset, distancePixels) = ProjectToRadar(offsetMeters, radarRot, scale, radius);
            var drawPos = center + uiOffset;

            // Interesting blips get a larger dot so they survive a crowded room.
            float dotRadius = (marker.Interesting ? 5.5f : 4f) * BlipScale;
            dl.AddCircleFilled(drawPos, dotRadius, RobustaImGuiTheme.ColU32(marker.Color), 12);

            var hovered = Vector2.DistanceSquared(mousePos, drawPos) <= 64f;
            if (hovered)
                hoveredUid = marker.Uid;

            // Tags always show, the name only on hover, so a crowded room reads as roles, not names.
            if (distancePixels < radius - 18f && (hovered || marker.Tags.Count > 0))
                DrawMarkerLabel(dl, drawPos, marker, hovered);
        }

        PopFont();

        HandleBlipActivation(hoveredUid, canvasClicked);

        // Captured after the frame's layout so the info panel can dock beside this window.
        var windowPos = GetWindowPos();
        var windowSize = GetWindowSize();
        WindowPos = windowPos;
        WindowSize = windowSize;
        WindowLayout.Remember(WindowLayoutId.Radar, windowPos, windowSize);

        if (IsMouseReleased(ImGuiMouseButton.Left))
            WindowLayout.FlushIfPending();

        End();
    }

    /// <summary>
    /// Whether a blip survives the Mobs switch. The switch hides NPCs, and a dead player is a corpse
    /// rather than a live blip, so it goes with them. Critically injured players stay: they still move.
    /// Pure so the edge case is testable.
    /// </summary>
    internal static bool IsMarkerVisible(RadarMarkerType type, bool dead, bool showMobs) =>
        showMobs || (type != RadarMarkerType.Mob && !dead);

    /// <summary>Maps one wheel notch onto the range: up zooms in, clamped to the slider's band.</summary>
    internal static float ZoomedRange(float range, float wheel) =>
        Math.Clamp(range - wheel * ZoomMetersPerNotch, MinRangeMeters, MaxRangeMeters);

    /// <summary>
    /// Projects a world-space offset onto the radar disc, returning the screen offset and the
    /// unclamped distance so a caller can decide whether a label fits. Anything past the rim is pinned
    /// to it, so the blip stays visible as an edge marker.
    /// </summary>
    internal static (Vector2 Offset, float Distance) ProjectToRadar(
        Vector2 offsetMeters,
        Angle radarRotation,
        float scale,
        float radius)
    {
        var relative = radarRotation.RotateVec(offsetMeters);
        var offset = new Vector2(relative.X, -relative.Y) * scale;
        var distance = offset.Length();
        var rim = radius - 6f;

        return distance > rim ? (Vector2.Normalize(offset) * rim, distance) : (offset, distance);
    }

    /// <summary>
    /// Whether the entity's transform chain is complete, i.e. every parent up to its map is present
    /// client-side. <see cref="SharedTransformSystem.GetWorldPosition(TransformComponent)"/> walks that
    /// chain with a throwing lookup, so a parent that left PVS mid-frame would abort the client.
    /// </summary>
    private static bool CanResolveWorldPosition(
        EntityQuery<TransformComponent> query,
        TransformComponent xform)
    {
        var parent = xform.ParentUid;

        // A malformed chain must not hang the frame, a real one is one or two hops deep.
        for (var depth = 0; depth < 8; depth++)
        {
            if (parent == xform.MapUid || !parent.IsValid())
                return true;

            if (!query.TryGetComponent(parent, out var parentXform))
                return false;

            parent = parentXform.ParentUid;
        }

        return false;
    }

    /// <summary>
    /// The strip under the title bar: range slider, then the options dropdown. One compact row, so the
    /// map gets the rest of the window.
    /// </summary>
    private void DrawControlStrip()
    {
        var style = GetStyle();
        float optionsWidth = CalcTextSize("OPTIONS").X + style.FramePadding.X * 2f;

        // Half-height controls: the strip is a control bar, not a form.
        PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, 0f));

        SetNextItemWidth(MathF.Max(60f, GetContentRegionAvail().X - optionsWidth - style.ItemSpacing.X));
        SliderFloat("##radarRange", ref _rangeMeters, MinRangeMeters, MaxRangeMeters, "Range %.0fm");

        SameLine();
        if (RobustaImGuiWidgets.AccentButton("OPTIONS"))
            OpenPopup("##radarOptions");

        PopStyleVar();

        if (!BeginPopup("##radarOptions"))
            return;

        // Toggles only, so the popup stays a list of switches.
        Checkbox("Mobs", ref _showMobs);
        if (IsItemHovered())
            SetTooltip("Show non-player mobs and corpses, live players are always shown");

        Checkbox("BORG", ref _showBorgs);
        if (IsItemHovered())
            SetTooltip("Tag player-controlled silicon chassis");

        Checkbox("EMAGGED", ref _showEmaggedTag);
        if (IsItemHovered())
            SetTooltip("Tag silicons an emag has subverted");

        Checkbox("THEFT?", ref _showTheftTags);
        if (IsItemHovered())
            SetTooltip("Tag players carrying stolen goods");

        Checkbox("SYNDICATE?", ref _showSyndicateTags);
        if (IsItemHovered())
            SetTooltip("Tag confirmed and suspected syndicate links");

        Checkbox("LIVE STATUS", ref _showLiveStatus);
        if (IsItemHovered())
            SetTooltip("Tag dead and critical mobs");

        Checkbox("WANTED", ref _showWantedTag);
        if (IsItemHovered())
            SetTooltip("Tag players security has flagged wanted or hostile");

        // Fades the window backdrop, so the game stays visible behind the map.
        SetNextItemWidth(-1f);
        if (SliderFloat("##radarOpacity", ref RobustaConfig.RadarWindowOpacity, 0.15f, 1f, "Opacity %.2f"))
            RobustaConfig.MarkDirty();

        EndPopup();
    }

    /// <summary>Draws a blip's label: its tags, prefixed by the name on hover.</summary>
    private static void DrawMarkerLabel(ImDrawListPtr dl, Vector2 drawPos, RadarMarker marker, bool hovered)
    {
        var cursor = drawPos + new Vector2(6f, -6f) * BlipScale;

        if (hovered)
        {
            // Lifted for the same reason as the tags: a dark hue fills a dot but vanishes as text.
            dl.AddText(cursor, RobustaImGuiTheme.ColU32(Lift(marker.Color, 0.35f)), marker.Name);
            cursor.X += CalcTextSize(marker.Name).X + TagGap;
        }

        foreach (var (text, color) in marker.Tags)
        {
            dl.AddText(cursor, color, text);
            cursor.X += CalcTextSize(text).X + TagGap;
        }
    }

    /// <summary>
    /// Opens the Info panel for a blip on click. Click only: a window appearing whenever the pointer
    /// crossed a blip made the map unusable.
    /// </summary>
    private static void HandleBlipActivation(EntityUid? hoveredUid, bool canvasClicked)
    {
        if (!canvasClicked || hoveredUid is not { } uid)
            return;

        // Re-opening an already-open panel for the same target would reset its report every frame
        // the pointer rests on the blip.
        if (!RobustaImGuiManager.RadarInfo.IsOpen || RobustaImGuiManager.RadarInfo.Target != uid)
            RobustaImGuiManager.RadarInfo.Show(uid);
    }

    /// <summary>
    /// Color for a command role key, or null when the key names no role or the configured hex does not
    /// parse. Null keeps the caller on the generic command color.
    /// </summary>
    private static Vector4? RoleColor(string? role)
    {
        if (role == null || RobustaConfig.Esp.ResolveRoleColor(role) is not { } color)
            return null;

        return new Vector4(color.R, color.G, color.B, color.A);
    }

    /// <summary>Tag color for an antagonist, matching the blip it labels.</summary>
    private static uint TagColorFor(AntagonistKind kind) =>
        RobustaImGuiTheme.ColU32(RobustaImGuiTheme.AntagonistColor(kind) ?? RobustaImGuiTheme.Colors.TextPrimary);

    /// <summary>
    /// Whether the hedged <c>[SYNDICATE?]</c> tag belongs on a blip. A recognised antagonist already
    /// carries a tag that names it, so the hedge would only repeat the name. Pure so it is testable.
    /// </summary>
    internal static bool ShouldShowHedgedSyndicateTag(bool hasSyndicateSignal, AntagonistKind? antagonist) =>
        hasSyndicateSignal && antagonist == null;

    /// <summary>
    /// Tag text for a command role. A command item is a costume to anyone who picks it up, so the role
    /// only reads as confirmed when the wearer is mindshielded.
    /// </summary>
    internal static string CommandTag(string? role, bool mindshielded)
    {
        var label = role ?? "COMMAND";
        return mindshielded ? $"[{label}]" : $"[{label}?]";
    }

    /// <summary>
    /// Whether the confirmed <c>[SYNDICATE]</c> tag belongs on a blip. A changeling's DNA currency and
    /// a wizard's <c>WizCoin</c> both trip the uplink signal, and a nuke op is issued one, so the tag
    /// would misdescribe them, their own tag already names them.
    /// </summary>
    internal static bool ShouldShowSyndicateTag(bool isSyndicate, AntagonistKind? antagonist) =>
        isSyndicate &&
        antagonist is not (AntagonistKind.Changeling or AntagonistKind.Wizard or
            AntagonistKind.NukeOperative);

    /// <summary>
    /// Whether the hedged <c>[THEFT?]</c> tag belongs on a blip. A thief carrying stolen goods is the
    /// expected case, and a confirmed syndicate link already outranks it. A chassis can only be a
    /// normal ghost role, so carried contraband never makes one a thief. Pure so it is testable.
    /// </summary>
    internal static bool ShouldShowTheftTag(
        bool hasStolenGear,
        bool isChassis,
        bool confirmedNukie,
        bool syndicateLink,
        AntagonistKind? antagonist) =>
        hasStolenGear &&
        !isChassis &&
        !confirmedNukie &&
        !syndicateLink &&
        antagonist != AntagonistKind.Thief;

    /// <summary>
    /// Tag text for an antagonist. The enum name is the label except where the radar has a shorter call
    /// sign, and a hedged verdict carries a question mark.
    /// </summary>
    private static string TagLabelFor(AntagonistKind kind, bool hedged)
    {
        var label = kind switch
        {
            AntagonistKind.NukeOperative => "NUKIE",
            AntagonistKind.HeadRevolutionary => "HEADREV",
            AntagonistKind.Revolutionary => "REV",
            _ => kind.ToString().ToUpperInvariant()
        };

        return hedged || kind.IsHedged() ? label + "?" : label;
    }

    /// <summary>Pushes a color toward white: a hue dark enough to fill a dot is unreadable as text.</summary>
    private static Vector4 Lift(Vector4 color, float amount) => Vector4.Lerp(color, Vector4.One, amount);

    private void UpdateMarkers(
        IEntityManager entMan,
        EntityUid localPlayer,
        Vector2 playerPos,
        MapId mapId)
    {
        _markers.Clear();
        var lookup = entMan.System<EntityLookupSystem>();
        var playerState = entMan.System<PlayerStateUtility>();

        // The lookup filters by component itself, so this returns the blip set without allocating a set
        // of every wall, pipe and item in range.
        _mobScanBuffer.Clear();
        lookup.GetEntitiesInRange<MobStateComponent>(mapId, playerPos, _rangeMeters, _mobScanBuffer);

        var mobStateSystem = entMan.System<MobStateSystem>();

        foreach (var (uid, mobState) in _mobScanBuffer)
        {
            if (uid == localPlayer)
                continue;

            // One entity that throws must not blank the radar for every other blip.
            try
            {
                AddMarker(entMan, playerState, mobStateSystem, uid, mobState, localPlayer);
            }
            catch (Exception e)
            {
                ReportFaultedEntity(uid, e);
            }
        }
    }

    /// <summary>Logs a throwing entity once, so a broken rule is visible without flooding the log.</summary>
    private void ReportFaultedEntity(EntityUid uid, Exception e)
    {
        if (!_faultedEntities.Add(uid))
            return;

        MarseyLogger.Warn($"[Radar] Scanning {uid} threw, it is skipped: {e}");
    }

    private void AddMarker(
        IEntityManager entMan,
        PlayerStateUtility playerState,
        MobStateSystem mobStateSystem,
        EntityUid uid,
        MobStateComponent mobState,
        EntityUid localPlayer)
    {
        {
            var colPlayer = RobustaImGuiTheme.AccentGreen;
            var colSyndi = RobustaImGuiTheme.AccentRed;
            var colSecurity = RobustaImGuiTheme.Colors.Danger;
            var colCommand = RobustaImGuiTheme.Colors.Success;
            var colSus = RobustaImGuiTheme.Colors.Warning;

            // One red ramp for the three threat levels, each step lifted so the text stays legible on
            // the radar's dark fill.
            var threatRed = RobustaImGuiTheme.Colors.Danger;
            uint colTagSyndicate = RobustaImGuiTheme.ColU32(threatRed);
            uint colTagSyndicateMaybe = RobustaImGuiTheme.ColU32(Lift(threatRed, 0.30f));
            uint colTagSecurity = RobustaImGuiTheme.ColU32(Lift(threatRed, 0.60f));
            uint colTagTheftMaybe = RobustaImGuiTheme.ColU32(Lift(RobustaImGuiTheme.Colors.Warning, 0.30f));
            uint colTagCommand = RobustaImGuiTheme.ColU32(Lift(RobustaImGuiTheme.Colors.Success, 0.40f));
            uint colTagBorg = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.Colors.Info);
            uint colTagDead = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.Colors.TextMuted);
            uint colTagCritical = RobustaImGuiTheme.ColU32(Lift(RobustaImGuiTheme.Colors.Warning, 0.20f));
            uint colTagWanted = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.Colors.Secondary);
            uint colTagEmagged = RobustaImGuiTheme.ColU32(RobustaImGuiTheme.Colors.Warning);

            var name = entMan.GetComponentOrNull<MetaDataComponent>(uid)?.EntityName ?? "Unknown";

            // One snapshot per entity: the flag report, syndicate block, command block and antagonist
            // verdict all come from the same cached scan.
            var snapshot = playerState.GetSnapshot(uid);
            var report = snapshot.Flags;
            var syndicate = snapshot.Syndicate;

            // A mind is what marks a mob as player controlled, every mob carries the container.
            var hasMind = report.Has(PlayerFlag.HasMind);
            var type = hasMind ? RadarMarkerType.Player : RadarMarkerType.Mob;
            var dead = mobStateSystem.IsDead(uid, mobState);

            // Latched by the state layer, so the marking survives a dropped uplink or PVS loss.
            var isSyndicate = syndicate?.UplinkStatus is not null;
            var isSecurity = report.Has(PlayerFlag.IsSecurity);
            var hasSyndicateGear = syndicate?.HasGear ?? false;
            var hasStolenGear = report.Has(PlayerFlag.HasStolenGear);

            var role = snapshot.Command?.Key;
            var roleColor = RoleColor(role);
            var antagonist = snapshot.Antagonist;
            var antagonistKind = antagonist?.Kind;

            // A player-controlled silicon chassis, which reads as a person on the radar otherwise.
            var isChassis = entMan.HasComponent<BorgChassisComponent>(uid);
            var isBorg = hasMind && isChassis;

            // From the station's criminal records, which the client receives in full.
            var isWanted = snapshot.Security?.Status is SecurityStatus.Wanted or SecurityStatus.Hostile;
            var isEmagged = syndicate?.Emagged ?? false;

            // One blip, one color: the highest-priority signal wins. A recognised antagonist outranks
            // everything, because that color names the mob rather than its threat level.
            Vector4 color;
            bool interesting;
            if (antagonistKind is { } kind)
            {
                color = RobustaImGuiTheme.AntagonistColor(kind) ?? colPlayer;
                interesting = kind != AntagonistKind.Skeleton;
            }
            else if (isSyndicate)
            {
                color = colSyndi;
                interesting = true;
            }
            else if (isSecurity)
            {
                color = colSecurity;
                interesting = true;
            }
            else if (snapshot.Command is not null)
            {
                // A head gets their own color so a cluster of command reads as roles, not a wall of
                // identical blips.
                color = roleColor ?? colCommand;
                interesting = true;
            }
            else if (hasSyndicateGear)
            {
                // GrandTheft gear is deliberately not here: a hypospray and a crew monitor are ordinary
                // job tools.
                color = colSus;
                interesting = true;
            }
            else
            {
                color = colPlayer;
                interesting = false;
            }

            // Tags are independent of the color, because a head of security is both, and the name
            // is not one of them: it only appears once the pointer rests on the blip.
            var tags = new List<(string Text, uint Color)>();

            // Life state first: it changes what the rest of the line means. Read through the mob-state
            // system, because CurrentState is a field upstream and a property on Goob.
            if (_showLiveStatus)
            {
                if (dead)
                    tags.Add(("[DEAD]", colTagDead));
                else if (mobStateSystem.IsCritical(uid, mobState))
                    tags.Add(("[CRIT]", colTagCritical));
            }

            if (_showBorgs && isBorg)
                tags.Add(("[BORG]", colTagBorg));

            // An emag rewrites a silicon's laws, so the tag rides beside [BORG], the owner's name never
            // leaves the server.
            if (_showEmaggedTag && isEmagged)
                tags.Add(("[EMAGGED]", colTagEmagged));

            if (antagonistKind is { } tagKind)
                tags.Add(($"[{TagLabelFor(tagKind, antagonist!.Hedged)}]", TagColorFor(tagKind)));

            // Security's own marking outranks the hedged gear tags that follow it.
            if (_showWantedTag && isWanted)
                tags.Add(("[WANTED]", colTagWanted));

            // A nuke op is a certainty, so the hedged tags would only add noise under its own tag.
            var confirmedNukie = antagonistKind == AntagonistKind.NukeOperative;

            // A changeling's DNA currency and a wizard's WizCoin both trip the uplink signal, so the
            // syndicate tag would misdescribe them.
            var syndicateLink = ShouldShowSyndicateTag(isSyndicate, antagonistKind);

            if (_showSyndicateTags)
            {
                if (syndicateLink)
                    tags.Add(("[SYNDICATE]", colTagSyndicate));
                else if (ShouldShowHedgedSyndicateTag(syndicate?.HasHedgedSignal == true, antagonistKind))
                    tags.Add(("[SYNDICATE?]", colTagSyndicateMaybe));
            }

            // GrandTheft gear is a separate question: a hypospray and a crew monitor are ordinary job
            // tools, so they get their own tag.
            if (_showTheftTags &&
                ShouldShowTheftTag(hasStolenGear, isChassis, confirmedNukie, syndicateLink, antagonistKind))
                tags.Add(("[THEFT?]", colTagTheftMaybe));

            if (snapshot.Command is { } command)
            {
                // A stolen ID card reads as command gear, so the role is only confirmed when the
                // wearer's mindshield is.
                var commandTag = CommandTag(command.Key, command.Mindshielded);
                tags.Add(roleColor is { } tagVector
                    ? (commandTag, RobustaImGuiTheme.ColU32(Lift(tagVector, 0.40f)))
                    : (commandTag, colTagCommand));
            }

            if (isSecurity)
                tags.Add(("[SEC]", colTagSecurity));

            _markers.Add(new RadarMarker(uid, color, name, tags, type, interesting, dead));
        }

        // Ordinary blips first, interesting ones last, so the ones worth clicking win the hover test.
        _markers.Sort((a, b) => a.Interesting.CompareTo(b.Interesting));
    }
}
