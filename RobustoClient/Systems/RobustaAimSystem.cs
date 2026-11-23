using System;
using System.Collections.Generic;
using System.Numerics;
using Content.Shared.CombatMode;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using RobustoClient.Systems.Aim;

namespace RobustoClient.Systems;

public record struct AimOutput(EntityUid Entity, MapCoordinates Position, Vector2? Velocity);

public class RobustaAimSystem : EntitySystem
{
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    
    [Dependency] private readonly RobustaFriendSystem _friend = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private EntityUid? _lockedTarget;
    public EntityUid? LockedTarget => _lockedTarget;

    /// <summary>Cursor movement below this squared distance reuses the previous scan for the same tick.</summary>
    private const float ScanMemoDistanceSq = 64f;

    private GameTick _scanTick;
    private bool _scanned;
    private ScreenCoordinates _scanMouse;
    private AimOutput? _scanResult;

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // Melee counts: a melee-only setup still needs the lock, and locking does not require a gun.
        if (!RobustaConfig.RangedAimbotEnabled && !RobustaConfig.ThrowAimbotEnabled && !RobustaConfig.MeleeAimbotEnabled)
        {
            _lockedTarget = null;
            return;
        }

        var inputMan = IoCManager.Resolve<IInputManager>();
        
        if (!RobustaConfig.TargetLockKey.IsDown(inputMan))
        {
            _lockedTarget = null;
            return;
        }

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return;

        // Locking is a combat action unless No Combat Lock is on, which exists so a throw target can be
        // picked before combat mode (and its FOV ring) is engaged.
        if (!RobustaConfig.NoCombatLock &&
            (!TryComp<CombatModeComponent>(localPlayer.Value, out var combat) || !combat.IsInCombatMode))
        {
            _lockedTarget = null;
            return;
        }

        if (!_lockedTarget.HasValue)
        {
            // Shares the per-tick scan cache with the shot solve and the overlay.
            if (TryAcquireAim(inputMan.MouseScreenPosition, out var acquired))
            {
                _lockedTarget = acquired.Entity;
            }
        }
        else
        {
            if (!Exists(_lockedTarget.Value) || !FilterEntity(_lockedTarget.Value, Transform(_lockedTarget.Value)) || !IsInRange(localPlayer.Value, _lockedTarget.Value, RobustaConfig.MaxLockDistance))
            {
                _lockedTarget = null;
            }
        }
    }
    // --- Utility Methods ---
    public AimOutput? GetAimOutputFromEnt(EntityUid ent)
    {
        if (!Exists(ent)) return null;
        if (!FilterEntity(ent, Transform(ent))) return null;

        var transform = Transform(ent);
        var mapCoords = _transform.GetMapCoordinates(transform);

        Vector2? velocity = null;
        if (TryComp<PhysicsComponent>(ent, out var phys))
            velocity = phys.LinearVelocity;

        return new AimOutput { Entity = ent, Position = mapCoords, Velocity = velocity };
    }
    /// <summary>
    /// The locked target's aim point, ignoring weapon range. A lock outranks a nearer body, so a melee swing
    /// aims at the lock and leaves the range check to upstream rather than retargeting someone else.
    /// </summary>
    public AimOutput? GetLockedAimOutput()
    {
        return _lockedTarget is { } locked ? GetAimOutputFromEnt(locked) : null;
    }

    public bool IsInRange(EntityUid ent, EntityUid target, float range)
    {
        if (!Exists(ent) || !Exists(target)) return false; // got deleted o algo

        var entTransform = Transform(ent);
        var targetTransform = Transform(target);
        if (entTransform.MapID != targetTransform.MapID) return false; // different maps

        var entMapPos = _transform.GetMapCoordinates(entTransform);
        var targetMapPos = _transform.GetMapCoordinates(targetTransform);

        var distance = (entMapPos.Position - targetMapPos.Position).Length();
        return distance <= range;
    }

    // --- Target Methods ---

    /// <summary>
    /// Resolves the entity a shot should be aimed at
    /// </summary>
    public bool TryAcquireAim(ScreenCoordinates mouse, out AimOutput output)
    {
        output = default;

        if (_lockedTarget is { } locked && Exists(locked))
        {
            output = new AimOutput
            {
                Entity = locked,
                Position = _transform.GetMapCoordinates(Transform(locked)),
                Velocity = null
            };

            return true;
        }

        if (_scanned && _scanTick == _timing.CurTick &&
            (mouse.Position - _scanMouse.Position).LengthSquared() <= ScanMemoDistanceSq)
        {
            if (_scanResult is not { } memo)
                return false;

            output = memo;
            return true;
        }

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null)
            return false;

        var exclude = new HashSet<EntityUid> { localPlayer.Value };
        var result = GetClosestInScreenFov(mouse, RobustaConfig.AimFovPixels, exclude);

        _scanned = true;
        _scanTick = _timing.CurTick;
        _scanMouse = mouse;
        _scanResult = result;

        if (result is not { } found)
            return false;

        output = found;
        return true;
    }

    // ==========================================================
    // SEARCH TARGET BY DISTANCE IN PIXELS ON SCREEN
    // ==========================================================
    /// <summary>
    /// Nearest body to the cursor inside the FOV circle, optionally limited to weapon reach.
    /// </summary>
    /// <param name="rangeFrom">
    /// Optional origin for a world-space reach limit. Melee passes the attacker so the FOV circle cannot
    /// reach past the weapon, which is what keeps a swing from picking a body the cursor never covered.
    /// </param>
    /// <param name="maxRange">Reach limit in metres, applied only when <paramref name="rangeFrom"/> is set.</param>
    public AimOutput? GetClosestInScreenFov(
        ScreenCoordinates mouseScreenPos,
        float fovRadiusPixels,
        HashSet<EntityUid>? exclude = null,
        EntityUid? rangeFrom = null,
        float maxRange = 0f)
    {
        var mapCoords = _eyeManager.ScreenToMap(mouseScreenPos);
        if (mapCoords.MapId == MapId.Nullspace) return null;

        var originPos = default(MapCoordinates);
        var hasRange = false;
        if (rangeFrom is { } origin && maxRange > 0f)
        {
            hasRange = true;
            originPos = _transform.GetMapCoordinates(Transform(origin));
        }

        var edgeScreen = new ScreenCoordinates(mouseScreenPos.Position + new Vector2(fovRadiusPixels, 0), mouseScreenPos.Window);
        var edgeMapCoords = _eyeManager.PixelToMap(edgeScreen);
        float worldRadius = edgeMapCoords.MapId == mapCoords.MapId
            ? Math.Clamp((edgeMapCoords.Position - mapCoords.Position).Length() + 1.5f, 2f, 60f)
            : 20f;
        var entitiesInRange = _lookup.GetEntitiesInRange(mapCoords, worldRadius, LookupFlags.Uncontained);
        if (exclude != null) entitiesInRange.ExceptWith(exclude);

        MapCoordinates? bestCoordinates = null;
        EntityUid? bestEntity = null;
        
        // Initial limit is our FOV radius. Anyone further away will not be considered.
        float closestPixelDistance = fovRadiusPixels; 

        foreach (var ent in entitiesInRange)
        {
            var transform = Transform(ent);
            if (!FilterEntity(ent, transform)) continue;

            var entityMapPos = _transform.GetMapCoordinates(transform);

            if (hasRange && !RobustaAimSolver.WithinReach(entityMapPos, originPos, maxRange))
            {
                continue;
            }
            
            // Translate mob coordinates to screen coordinates
            var entityScreenPos = _eyeManager.CoordinatesToScreen(transform.Coordinates);
            
            // Calculate distance from cursor to mob IN PIXELS
            var pixelDistance = (mouseScreenPos.Position - entityScreenPos.Position).Length();

            // If the mob is within our FOV and closer to the cursor than the previous one found
            if (pixelDistance <= closestPixelDistance)
            {
                closestPixelDistance = pixelDistance;
                bestCoordinates = entityMapPos;
                bestEntity = ent;
            }
        }

        if (bestEntity == null || bestCoordinates == null) return null;

        Vector2? velocity = null;
        if (TryComp<PhysicsComponent>(bestEntity, out var phys))
            velocity = phys.LinearVelocity;

        return new AimOutput { Entity = bestEntity.Value, Position = bestCoordinates.Value, Velocity = velocity };
    }

    private bool FilterEntity(EntityUid uid, TransformComponent transform)
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return false;
        if (transform.MapID != Transform(localPlayer.Value).MapID) return false;
        if (!TryComp<MobStateComponent>(uid, out var state)) return false;

        // Critical targets stay valid, they are hittable once the shot names them in RequestShootEvent.Target.
        if (state.CurrentState is MobState.Dead or MobState.Invalid) return false;

        if (_friend != null && _friend.IsFriend(uid)) return false;
        return true;
    }
}