using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Physics;
using Content.Shared.StepTrigger.Components;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.GameStates;
using Robust.Client.Timing;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using RobustoClient.Systems.Aim;

namespace RobustoClient.Systems;

/// <summary>
/// Solves where to point a shot so it meets the target after the server has processed it.
/// </summary>
/// <remarks>
/// The aim point is an <see cref="EntityCoordinates"/> parented to the target, so it travels with the
/// target while the request is in flight. That makes network latency cancel out of the intercept for gun
/// shots: the server resolves the point against the target's own position at process time. Projectiles
/// carry the grid's velocity and none of the shooter's, so the relative velocity is measured against the
/// shooter's grid rather than against the shooter. Only aim points that are not attached to the target,
/// namely throws, need a measured network lead.
/// </remarks>
public class RobustaPredictionSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IClientGameTiming _timing = default!;
    [Dependency] private readonly IClientGameStateManager _stateMan = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;

    /// <summary>One-way delay assumed by the manual <see cref="RobustaConfig.PingMode.Stable"/> override.</summary>
    private const float StableDelaySeconds = 0.08f;

    /// <summary>One-way delay assumed by the manual <see cref="RobustaConfig.PingMode.Laggy"/> override.</summary>
    private const float LaggyDelaySeconds = 0.2f;

    private const int DefaultBulletMask = (int) (CollisionGroup.Impassable | CollisionGroup.BulletImpassable);

    /// <summary>Mask a thrown item collides with, matching <see cref="CollisionGroup.ThrownItem"/>.</summary>
    private const int ThrownMask = (int) CollisionGroup.ThrownItem;

    /// <summary>Vertical fraction of the target's box tried when the body centre is occluded.</summary>
    private const float SampleLiftFraction = 0.25f;

    private const float MinSampleLift = 0.1f;
    private const float MaxSampleLift = 0.3f;

    /// <summary>Fallback throw range when the shooter has no hands component.</summary>
    private const float DefaultThrowRange = 8f;

    /// <summary>Fallback step-trigger speed, matching <c>StepTriggerComponent</c>'s default.</summary>
    private const float DefaultTriggerSpeed = 3.5f;

    private readonly RobustaLeadEstimator _lead = new();
    private readonly Dictionary<string, ProtoAimData> _protoCache = new(StringComparer.Ordinal);

    private int _bufferTicks;

    private EntityUid? _dampingTarget;
    private Vector2 _dampingVelocity;
    private float _damping = 1f;

    /// <summary>Most recent solved gun plan, for overlays and diagnostics.</summary>
    public ShotPlan? LastPlan { get; private set; }

    /// <summary>
    /// Drops the cached plan so an overlay never draws an aim point from an earlier tick. Called once per
    /// tick before any solve, which also means the marker only appears while a target is actually acquired.
    /// </summary>
    public void ForgetPlan() => LastPlan = null;

    public override void Initialize()
    {
        base.Initialize();

        RefreshBufferTicks();
        _cfg.OnValueChanged(CVars.NetBufferSize, _ => RefreshBufferTicks(), true);
        _cfg.OnValueChanged(CVars.NetPredictTickBias, _ => RefreshBufferTicks(), true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // The engine builds its prediction target as LastProcessedTick + buffer + ceil(tickRate * rtt)
        // + tick bias, so this difference is the render skew minus the deliberate input cushion.
        var lagTicks = (int) (_timing.CurTick.Value - _timing.LastProcessedTick.Value) - _bufferTicks;
        var starving = _stateMan.GetApplicableStateCount() < _stateMan.TargetBufferSize;

        _lead.Sample(lagTicks, starving);
    }

    /// <summary>Network lead to apply to an aim point that is not attached to the target.</summary>
    public float MeasuredLeadSeconds => _lead.LeadSeconds(_timing.TickPeriod, RobustaConfig.MaxAimLeadSeconds);


    /// <summary>Resolves speed, hitscan flag and collision mask for the given gun.</summary>
    public AimProfile Profile(EntityUid gun)
    {
        var speed = RobustaConfig.DefaultProjectileSpeed;

        if (TryComp<GunComponent>(gun, out var gunComp))
        {
            if (gunComp.ProjectileSpeedModified > 0f)
                speed = gunComp.ProjectileSpeedModified;
            else if (gunComp.ProjectileSpeed > 0f)
                speed = gunComp.ProjectileSpeed;
        }

        var data = ProtoData(gun);
        return new AimProfile(speed, data.Hitscan, data.Mask, data.MaxDistance);
    }

    /// <summary>
    /// Solves a gun shot. The returned offset is parented to the target, so no network lead is applied.
    /// </summary>
    public bool TrySolveGun(EntityUid shooter, EntityUid target, AimProfile profile, out ShotPlan plan)
    {
        var solved = Solve(shooter, target, profile, 0f, out plan);
        LastPlan = solved ? plan : null;
        return solved;
    }

    /// <summary>
    /// Solves a throw. Thrown items land on a point anchored to the world rather than to the target, so the
    /// target's motion during the message transit has to be added here.
    /// </summary>
    public bool TrySolveThrow(EntityUid shooter, EntityUid target, float speed, out Vector2 worldPoint, out EntityUid? blocker)
    {
        var profile = new AimProfile(speed, false, ThrownMask, 0f);

        if (!Solve(shooter, target, profile, NetworkLeadSeconds(), out var plan))
        {
            worldPoint = default;
            blocker = null;
            return false;
        }

        worldPoint = plan.WorldPoint;
        blocker = plan.Blocker;
        return true;
    }

    /// <summary>
    /// Solves where to drop a thrown slip item so the target walks onto it after it has landed.
    /// </summary>
    public bool TrySolveSlipThrow(
        EntityUid shooter,
        EntityUid target,
        EntityUid item,
        out Vector2 worldPoint,
        out EntityUid? blocker,
        out SlipPlanStatus status)
    {
        worldPoint = default;
        blocker = null;
        status = SlipPlanStatus.NoTarget;

        if (!TryComp<TransformComponent>(shooter, out var shooterXform) ||
            !TryComp<TransformComponent>(target, out var targetXform))
        {
            return false;
        }

        if (shooterXform.MapID != targetXform.MapID || shooterXform.MapID == MapId.Nullspace)
            return false;

        var mapId = shooterXform.MapID;
        var shooterPos = _transform.GetWorldPosition(shooterXform);
        var targetPos = _transform.GetWorldPosition(targetXform);

        var throwSpeed = RobustaConfig.DefaultThrowSpeed;
        var maxRange = DefaultThrowRange;
        if (TryComp<HandsComponent>(shooter, out var hands))
        {
            throwSpeed = hands.BaseThrowspeed;
            maxRange = hands.ThrowRange;
        }

        var targetVel = TryComp<PhysicsComponent>(target, out var targetPhys)
            ? _physics.GetMapLinearVelocity(target, targetPhys)
            : Vector2.Zero;

        var minTriggerSpeed = TryComp<StepTriggerComponent>(item, out var step)
            ? step.RequiredTriggeredSpeed
            : DefaultTriggerSpeed;

        var lead = RobustaAimSolver.SolveSlipLead(
            shooterPos,
            targetPos,
            targetVel,
            throwSpeed,
            maxRange,
            NetworkLeadSeconds(),
            minTriggerSpeed,
            RobustaConfig.SlipThrowMinLead);

        if (lead < 0f)
        {
            status = SlipPlanStatus.OutOfRange;
            return false;
        }

        var speed = targetVel.Length();

        // The solver returns a positive lead only when it saw a heading, so the division is safe.
        var point = lead > 0f
            ? targetPos + targetVel / speed * lead
            : targetPos;

        // Same visibility rule as the gun path, with the mask a thrown item actually collides with.
        if (RobustaConfig.AimLineOfSight &&
            !IsPathClear(shooter, target, mapId, shooterPos, point, ThrownMask, out blocker))
        {
            status = SlipPlanStatus.Blocked;
            return false;
        }

        worldPoint = point;
        status = lead <= 0f ? SlipPlanStatus.Underfoot : SlipPlanStatus.Placed;
        return true;
    }

    private bool Solve(EntityUid shooter, EntityUid target, AimProfile profile, float extraLead, out ShotPlan plan)
    {
        plan = default;

        if (!TryComp<TransformComponent>(shooter, out var shooterXform) ||
            !TryComp<TransformComponent>(target, out var targetXform))
        {
            return false;
        }

        if (shooterXform.MapID != targetXform.MapID || shooterXform.MapID == MapId.Nullspace)
            return false;

        var mapId = shooterXform.MapID;
        var shooterPos = _transform.GetWorldPosition(shooterXform);
        var targetOrigin = _transform.GetWorldPosition(targetXform);
        var targetRot = _transform.GetWorldRotation(targetXform);
        var relVel = RelativeVelocity(shooterXform, target);
        var damping = UpdateDamping(target, relVel);

        // The body centre is the best hit, so it is tried first, the offsets cover a partially occluded
        // body such as one standing behind a table edge or a window frame.
        var lift = SampleLift(target, targetXform);
        Span<Vector2> samples = stackalloc Vector2[3];
        samples[0] = Vector2.Zero;
        samples[1] = new Vector2(0f, lift);
        samples[2] = new Vector2(0f, -lift);

        ShotPlan? clear = null;
        ShotPlan? occluded = null;

        foreach (var sample in samples)
        {
            var aimWorld = targetOrigin + targetRot.RotateVec(sample);

            var flightTime = 0f;
            if (!profile.Hitscan)
                RobustaAimSolver.TrySolveFlightTime(aimWorld - shooterPos, relVel, profile.Speed, out flightTime);

            var lead = RobustaAimSolver.LeadOffset(relVel, flightTime + extraLead, damping);
            var worldAim = aimWorld + lead;
            var localOffset = sample + RobustaAimSolver.ToLocalOffset(lead, targetRot);

            EntityUid? blocker = null;
            var blocked = RobustaConfig.AimLineOfSight &&
                          !IsPathClear(shooter, target, mapId, shooterPos, worldAim, profile.Mask, out blocker);

            var candidate = new ShotPlan(
                target,
                localOffset,
                worldAim,
                flightTime,
                extraLead,
                damping,
                blocked,
                blocker);

            if (!blocked)
            {
                clear = candidate;
                break;
            }

            occluded ??= candidate;
        }

        if ((clear ?? occluded) is not { } chosen)
            return false;

        plan = chosen;
        return true;
    }

    private Vector2 RelativeVelocity(TransformComponent shooterXform, EntityUid target)
    {
        // Upstream spawns projectiles with gridVelocity + direction * speed, so the shooter's own walk
        // velocity never enters the bullet. Measuring against the grid is what keeps a strafing shot good.
        var frameVelocity = _physics.GetMapLinearVelocity(shooterXform.Coordinates);
        var targetVelocity = TryComp<PhysicsComponent>(target, out var targetPhys)
            ? _physics.GetMapLinearVelocity(target, targetPhys)
            : Vector2.Zero;

        return targetVelocity - frameVelocity;
    }

    private float UpdateDamping(EntityUid target, Vector2 relVel)
    {
        if (_dampingTarget != target)
        {
            _dampingTarget = target;
            _dampingVelocity = relVel;
            _damping = 1f;
            return _damping;
        }

        _damping = RobustaAimSolver.LeadDamping(relVel, _dampingVelocity, _damping);
        _dampingVelocity = relVel;
        return _damping;
    }

    private float SampleLift(EntityUid target, TransformComponent targetXform)
    {
        var height = _lookup.GetWorldAABB(target, targetXform).Height;
        return Math.Clamp(height * SampleLiftFraction, MinSampleLift, MaxSampleLift);
    }

    /// <summary>
    /// Whether a shot from the shooter to <paramref name="to"/> meets the target before anything else.
    /// </summary>
    private bool IsPathClear(
        EntityUid shooter,
        EntityUid target,
        MapId mapId,
        Vector2 from,
        Vector2 to,
        int mask,
        out EntityUid? blocker)
    {
        blocker = null;

        // Upstream bypasses its own target filter when the shooter sits inside a container, because the
        // shot leaves from inside geometry. Match that rather than reporting a false block.
        if (_container.IsEntityOrParentInContainer(shooter))
            return true;

        if (!RobustaAimSolver.TryBuildRay(from, to, mask, out var ray, out var length))
            return true;

        // Collect every hit inside the segment and take the nearest that actually stops the shot. Asking
        // for the first hit only stops at whichever fixture the tree visits first, and the static tree
        // (walls, windows) is queried before the dynamic one that holds mobs.
        foreach (var candidate in _physics.IntersectRay(mapId, ray, length, shooter, returnOnFirstHit: false))
        {
            if (candidate.HitEntity == target)
                return true;

            if (PassesOver(candidate.HitEntity))
                continue;

            blocker = candidate.HitEntity;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a shot flies over this entity instead of stopping on it.
    /// </summary>
    private bool PassesOver(EntityUid hit)
    {
        return TryComp<RequireProjectileTargetComponent>(hit, out var require) && require.Active;
    }

    /// <summary>Lead for a point that does not travel with the target: manual override or measured skew.</summary>
    private float NetworkLeadSeconds()
    {
        return RobustaConfig.CurrentPingMode switch
        {
            RobustaConfig.PingMode.Local => 0f,
            RobustaConfig.PingMode.Stable => StableDelaySeconds,
            RobustaConfig.PingMode.Laggy => LaggyDelaySeconds,
            _ => MeasuredLeadSeconds,
        };
    }

    private ProtoAimData ProtoData(EntityUid gun)
    {
        if (!TryComp<MetaDataComponent>(gun, out var meta) || meta.EntityPrototype is not { } gunProto)
            return ProtoAimData.Fallback;

        // Cached per gun prototype: the ammo prototype cannot change for a given gun prototype.
        if (_protoCache.TryGetValue(gunProto.ID, out var cached))
            return cached;

        var data = BuildProtoData(gun, gunProto.ID);
        _protoCache[gunProto.ID] = data;
        return data;
    }

    private ProtoAimData BuildProtoData(EntityUid gun, string gunProtoId)
    {
        // The hitscan flag and the collision mask both live on the ammo prototype. Battery guns cover every
        // hitscan weapon in the game and ballistic providers cover the plain bullet guns. Magazine-fed weapons
        // fall through to the standard bullet mask, which is what their cartridges use anyway.
        EntProtoId? ammoId = null;

        if (TryComp<BatteryAmmoProviderComponent>(gun, out var battery))
            ammoId = battery.Prototype;
        else if (TryComp<BallisticAmmoProviderComponent>(gun, out var ballistic))
            ammoId = ballistic.Proto;

        if (ammoId is not { } id || !_proto.TryIndex(id, out EntityPrototype? ammoProto))
            return ProtoAimData.Fallback;

        var hitscan = ammoProto.TryGetComponent<HitscanAmmoComponent>(out _, _factory);
        var mask = DefaultBulletMask;
        var maxDistance = 0f;

        if (ammoProto.TryGetComponent<FixturesComponent>(out var fixtures, _factory))
        {
            var union = 0;
            foreach (var fixture in fixtures.Fixtures.Values)
                union |= fixture.CollisionMask;

            if (union != 0)
                mask = union;
        }

        if (ammoProto.TryGetComponent<HitscanBasicRaycastComponent>(out var raycast, _factory))
        {
            mask = (int) raycast.CollisionMask;
            maxDistance = raycast.MaxDistance;
        }

        Log.Debug($"[Aim] {gunProtoId}: ammo {ammoId}, hitscan={hitscan}, mask={mask}, range={maxDistance}");
        return new ProtoAimData(hitscan, mask, maxDistance);
    }

    private void RefreshBufferTicks()
    {
        _bufferTicks = _cfg.GetCVar(CVars.NetBufferSize) + _cfg.GetCVar(CVars.NetPredictTickBias);
    }

    private readonly record struct ProtoAimData(bool Hitscan, int Mask, float MaxDistance)
    {
        public static readonly ProtoAimData Fallback = new(false, DefaultBulletMask, 0f);
    }
}

