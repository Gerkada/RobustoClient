using System.Linq;
using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths; 
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Network;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;

namespace ArabicaCliento.Systems;

public class ArabicaPredictionSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!; 
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly INetManager _net = default!;

    private float GetPingSeconds()
    {
        switch (ArabicaConfig.CurrentPingMode)
        {
            case ArabicaConfig.PingMode.Local: return 0f;
            case ArabicaConfig.PingMode.Stable: return 0.04f; // ~80ms RTT
            case ArabicaConfig.PingMode.Laggy: return 0.1f;   // ~200ms RTT
            case ArabicaConfig.PingMode.Auto:
            default:
                // Пинг в SS14 — это RTT (туда-обратно). Нам нужно время до сервера.
                if (_net.IsClient && _net.Channels.Any())
                {
                    return _net.Channels.First().Ping / 2000f;
                }
                return 0f;
        }
    }

    public Vector2 GetPredictedAimPoint(EntityUid shooter, EntityUid target, float projectileSpeed = 20f, bool compensateShooterVelocity = true)
    {
        var shooterXform = Transform(shooter);
        var targetXform = Transform(target);

        Vector2 shooterPos = _transform.GetWorldPosition(shooterXform);
        Vector2 targetPos = _transform.GetWorldPosition(targetXform);

        // ==========================================
        // ФИКС ДЛЯ ЛЕЖАЧИХ И КРИТОВЫХ ЦЕЛЕЙ
        // ==========================================
        try 
        {
            var aabb = _lookup.GetWorldAABB(target);
            targetPos = aabb.Center;
        }
        catch 
        {
            bool isLying = false;
            if (TryComp<MobStateComponent>(target, out var state) && state.CurrentState == MobState.Critical) 
                isLying = true;

            if (isLying) 
            {
                targetPos.Y -= 0.35f; 
            }
        } 

        // ==========================================
        // ФИКС ДЛЯ ХИТСКАНА (Лазеров)
        // ==========================================
        if (projectileSpeed >= 999f)
        {
            return targetPos;
        }

        // ==========================================
        // ОСНОВНОЙ ПРЕДИКТ
        // ==========================================
        Vector2 targetVel = Vector2.Zero;
        Vector2 shooterBaseVel = Vector2.Zero;
        
        if (TryComp<PhysicsComponent>(target, out var targetPhys))
        {
            targetVel = _physics.GetMapLinearVelocity(target, targetPhys);
        }

        if (compensateShooterVelocity)
        {
            // Для ПУЛЬ: снаряд наследует полную скорость игрока (бег + грид)
            if (TryComp<PhysicsComponent>(shooter, out var shooterPhys))
            {
                shooterBaseVel = _physics.GetMapLinearVelocity(shooter, shooterPhys);
            }
        }
        else 
        {
            // Для БРОСКОВ: предмет наследует ТОЛЬКО скорость грида (станции), 
            // теряя скорость бега игрока в момент отделения от рук.
            if (shooterXform.GridUid.HasValue)
            {
                shooterBaseVel = _physics.GetMapLinearVelocity(shooterXform.GridUid.Value);
            }
        }

        Vector2 relVel = targetVel - shooterBaseVel;

        if (relVel.LengthSquared() < 0.1f)
        {
            // Добавляем упреждение по пингу даже для "стоячей" цели на случай микро-движений
            float pingT = GetPingSeconds();
            return targetPos + (targetVel * pingT);
        }

        Vector2 relPos = targetPos - shooterPos;

        float a = Vector2.Dot(relVel, relVel) - (projectileSpeed * projectileSpeed);
        float b = 2f * Vector2.Dot(relVel, relPos);
        float c = Vector2.Dot(relPos, relPos);

        float t = SolveQuadratic(a, b, c);

        if (t < 0f)
        {
            t = relPos.Length() / projectileSpeed;
        }

        // КОРРЕКЦИЯ ПИНГА: добавляем задержку сети к времени полета
        t += GetPingSeconds();

        t = Math.Clamp(t, 0.01f, 1.5f);
        t += 0.05f;

        return targetPos + (relVel * t);
    }

    private float SolveQuadratic(float a, float b, float c)
    {
        if (Math.Abs(a) < 1e-6f)
        {
            if (Math.Abs(b) < 1e-6f) return -1f;
            return -c / b;
        }

        float discriminant = (b * b) - (4f * a * c);
        
        if (discriminant < 0f) return -1f;

        float sqrtD = MathF.Sqrt(discriminant);
        float t1 = (-b - sqrtD) / (2f * a);
        float t2 = (-b + sqrtD) / (2f * a);

        if (t1 > 0f && t2 > 0f) return Math.Min(t1, t2);
        if (t1 > 0f) return t1;
        if (t2 > 0f) return t2;

        return -1f;
    }
}