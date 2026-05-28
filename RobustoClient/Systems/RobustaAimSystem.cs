using System.Collections.Generic;
using System.Numerics;
using RobustoClient.Components; 
using Content.Shared.Hands.Components;
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

namespace RobustoClient.Systems;

public record struct AimOutput(EntityUid Entity, MapCoordinates Position, Vector2? Velocity);

public class RobustaAimSystem : EntitySystem
{
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    
    [Dependency] private readonly RobustaFriendSystem _friend = default!;
    [Dependency] private readonly RobustaPredictionSystem _prediction = default!; 

    private EntityUid? _lockedTarget;
    public EntityUid? LockedTarget => _lockedTarget;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!RobustaConfig.RangedAimbotEnabled && !RobustaConfig.ThrowAimbotEnabled)
        {
            _lockedTarget = null;
            return;
        }

        var inputMan = IoCManager.Resolve<IInputManager>();
        
        if (!inputMan.IsKeyDown(RobustaConfig.TargetLockKey))
        {
            _lockedTarget = null;
            return;
        }

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return;

        if (!_lockedTarget.HasValue)
        {
            // USE PIXEL FOV
            var target = GetClosestInScreenFov(inputMan.MouseScreenPosition, RobustaConfig.AimFovPixels, new HashSet<EntityUid> { localPlayer.Value });
            if (target != null)
            {
                _lockedTarget = target.Value.Entity;
            }
        }
        else
        {
            if (!Exists(_lockedTarget.Value) || !FilterEntity(_lockedTarget.Value, Transform(_lockedTarget.Value)))
            {
                _lockedTarget = null;
            }
        }
    }

    public AimOutput? GetSilentAimTarget(ScreenCoordinates mousePos, float bulletSpeed)
    {
        if (!RobustaConfig.RangedAimbotEnabled) return null;

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return null;

        if (_lockedTarget.HasValue)
        {
            var lockTransform = Transform(_lockedTarget.Value);
            var lockedPredictedPos = _prediction.GetPredictedAimPoint(localPlayer.Value, _lockedTarget.Value, bulletSpeed, true);
            
            return new AimOutput 
            { 
                Entity = _lockedTarget.Value, 
                Position = new MapCoordinates(lockedPredictedPos, lockTransform.MapID),
                Velocity = null 
            };
        }

        // USE PIXEL FOV
        var target = GetClosestInScreenFov(mousePos, RobustaConfig.AimFovPixels, new HashSet<EntityUid> { localPlayer.Value });
        if (target == null) return null;

        var predictedPos = _prediction.GetPredictedAimPoint(localPlayer.Value, target.Value.Entity, bulletSpeed, true);
        
        var finalOutput = target.Value;
        finalOutput.Position = new MapCoordinates(predictedPos, target.Value.Position.MapId);
        return finalOutput;
    }

    public AimOutput? GetThrowAimTarget(ScreenCoordinates mousePos)
    {
        if (!RobustaConfig.ThrowAimbotEnabled) return null;

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return null;

        // Determine throw speed dynamically
        float throwSpeed = RobustaConfig.DefaultThrowSpeed;
        if (TryComp<HandsComponent>(localPlayer.Value, out var hands))
        {
            throwSpeed = hands.BaseThrowspeed;
        }

        if (_lockedTarget.HasValue)
        {
            var lockTransform = Transform(_lockedTarget.Value);
            var lockedPredictedPos = _prediction.GetPredictedAimPoint(localPlayer.Value, _lockedTarget.Value, throwSpeed, false);
            
            return new AimOutput 
            { 
                Entity = _lockedTarget.Value, 
                Position = new MapCoordinates(lockedPredictedPos, lockTransform.MapID),
                Velocity = null 
            };
        }

        // USE PIXEL FOV
        var target = GetClosestInScreenFov(mousePos, RobustaConfig.AimFovPixels, new HashSet<EntityUid> { localPlayer.Value });
        if (target == null) return null;

        var predictedPos = _prediction.GetPredictedAimPoint(localPlayer.Value, target.Value.Entity, throwSpeed, false);
        
        var finalOutput = target.Value;
        finalOutput.Position = new MapCoordinates(predictedPos, target.Value.Position.MapId);
        return finalOutput;
    }

    // ==========================================================
    // SEARCH TARGET BY DISTANCE IN PIXELS ON SCREEN
    // ==========================================================
    public AimOutput? GetClosestInScreenFov(ScreenCoordinates mouseScreenPos, float fovRadiusPixels, HashSet<EntityUid>? exclude = null)
    {
        var mapCoords = _eyeManager.ScreenToMap(mouseScreenPos);
        if (mapCoords.MapId == MapId.Nullspace) return null;

        // Get entities in a wide world radius (20 meters is enough to cover the whole screen)
        var entitiesInRange = _lookup.GetEntitiesInRange(mapCoords, 20f, LookupFlags.Uncontained);
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

    // --- Methods kept for compatibility with other systems (e.g., melee aimbot) ---
    public AimOutput? GetClosestInRange(ScreenCoordinates screenCoordinates, float range, HashSet<EntityUid>? exclude = null)
    {
        var mapCoords = _eyeManager.ScreenToMap(screenCoordinates);
        if (mapCoords.MapId == MapId.Nullspace) return null;
        return GetClosestInRange(mapCoords, range, exclude);
    }

    public AimOutput? GetClosestInRange(MapCoordinates coordinates, float range, HashSet<EntityUid>? exclude = null)
    {
        var entitiesInRange = _lookup.GetEntitiesInRange(coordinates, range, LookupFlags.Uncontained);
        if (exclude != null) entitiesInRange.ExceptWith(exclude);
        return GetClosestTo(coordinates, entitiesInRange);
    }

    public AimOutput? GetClosestToEntInRange(EntityUid ent, float range, HashSet<EntityUid>? exclude = null)
    {
        var mapCords = _transform.GetMapCoordinates(Transform(ent));
        var entitiesInRange = _lookup.GetEntitiesInRange(mapCords, range, LookupFlags.Uncontained);
        if (exclude != null) entitiesInRange.ExceptWith(exclude);
        return GetClosestTo(mapCords, entitiesInRange);
    }

    private AimOutput? GetClosestTo(MapCoordinates coordinates, HashSet<EntityUid> entities)
    {
        MapCoordinates? closestCoordinates = null;
        EntityUid? closestEntity = null;
        var closestDistance = float.MaxValue;
        
        foreach (var ent in entities)
        {
            var transform = Transform(ent);
            if (!FilterEntity(ent, transform)) continue;
                
            var entityMapPos = _transform.GetMapCoordinates(transform);
            var distance = (coordinates.Position - entityMapPos.Position).Length();
            
            if (distance < closestDistance)
            {
                closestCoordinates = entityMapPos;
                closestDistance = distance;
                closestEntity = ent;
            }
        }

        if (closestEntity == null || closestCoordinates == null) return null;

        Vector2? velocity = null;
        if (TryComp<PhysicsComponent>(closestEntity, out var phys))
            velocity = phys.LinearVelocity;

        return new AimOutput { Entity = closestEntity.Value, Position = closestCoordinates.Value, Velocity = velocity };
    }

    private bool FilterEntity(EntityUid uid, TransformComponent transform)
    {
        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null) return false;
        if (transform.MapID != Transform(localPlayer.Value).MapID) return false;
        if (!TryComp<MobStateComponent>(uid, out var state)) return false;
        if (state.CurrentState == MobState.Dead || state.CurrentState == MobState.Invalid) return false;
        if (_friend != null && _friend.IsFriend(uid)) return false;
        return true;
    }
}