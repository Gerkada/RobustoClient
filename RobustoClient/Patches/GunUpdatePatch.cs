using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using RobustoClient.Systems;
using Content.Client.Weapons.Ranged.Systems; 
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using HarmonyLib;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Log;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using System.Collections;
using RobustoClient.Reflection;

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(GunSystem), nameof(GunSystem.Update))]
public class GunUpdatePatch
{
    private static IEntityManager? _entMan;
    private static IInputManager? _input;
    private static IPlayerManager? _player;
    private static RobustaAimSystem? _aim;

    private static readonly string[] HitscanKeywords = ["hitscan", "beam", "instant", "raycast"];
    private static readonly string[] NameKeywords = ["laser", "pulse", "xray", "ray", "beam"];
    private static readonly string[] PotentialSpeedNames = ["ProjectileSpeedModified", "ProjectileSpeed", "speed", "_projectileSpeed"];
    private static readonly HashSet<string> HitscanPrototypes =
    [
        "WeaponAntiqueLaser", "WeaponLaserPistol", "WeaponLaserGun", "WeaponLaserCarbine",
        "WeaponLaserCarbinePractice", "WeaponAdvancedLaser", "WeaponLaserCannon",
        "WeaponLaserCannonXenoborg", "WeaponLaserGunXenoborg", "WeaponLaserSvalinn",
        "WeaponMakeshiftLaser", "WeaponBehonkerLaser",
        "WeaponPulsePistol", "WeaponPulseCarbine", "WeaponPulseRifle",
        "WeaponHitscanDebug", "WeaponHitscanDebugGib", "RedShuttleLaser"
    ];

    private static readonly Dictionary<string, bool> ProtoHitscanCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, (bool IsHitscanComp, bool HasSpeedMember)> CompAccessorCache = new();
    private static readonly HashSet<string> LoggedWeapons = new(StringComparer.OrdinalIgnoreCase);

    public static NetCoordinates Patch(NetCoordinates originalCoords)
    {
        _entMan ??= IoCManager.Resolve<IEntityManager>();
        _input ??= IoCManager.Resolve<IInputManager>();
        _player ??= IoCManager.Resolve<IPlayerManager>();
        _aim ??= _entMan.System<RobustaAimSystem>();

        if (!RobustaConfig.RangedAimbotEnabled) return originalCoords;

        var aimResult = _aim.GetSilentAimTarget(_input.MouseScreenPosition, 1000f);
        if (aimResult == null) return originalCoords; 

        var localPlayer = _player.LocalEntity;
        if (localPlayer == null) return originalCoords;

        var targetUid = aimResult.Value.Entity;
        var transformSystem = _entMan.System<SharedTransformSystem>();
        
        var currentCenterPos = aimResult.Value.Position.Position;
        var targetWorldPos = transformSystem.GetWorldPosition(targetUid);
        var finalOffset = currentCenterPos - targetWorldPos;

        if (RobustaConfig.UsePrediction)
        {
            try 
            {
                EntityUid? gunUid = null;
                var handsSystem = _entMan.System<SharedHandsSystem>();

                if (_entMan.TryGetComponent<HandsComponent>(localPlayer.Value, out var hands))
                {
                    var activeItem = handsSystem.GetActiveItem((localPlayer.Value, hands));
                    if (activeItem != null) gunUid = activeItem;
                }

                if (gunUid != null && gunUid.Value.IsValid())
                {
                    _entMan.TryGetComponent<MetaDataComponent>(gunUid.Value, out var meta);
                    string cacheKey = meta?.EntityPrototype?.ID ?? meta?.EntityName ?? _entMan.ToPrettyString(gunUid.Value);

                    bool isHitscan = false;
                    bool speedDetected = false;
                    string detectedInComp = "";
                    float bulletSpeed = RobustaConfig.DefaultProjectileSpeed;

                    if (ProtoHitscanCache.TryGetValue(cacheKey, out var cachedProtoHitscan) && cachedProtoHitscan)
                    {
                        isHitscan = true;
                        detectedInComp = "ProtoCache";
                    }
                    else if (!ProtoHitscanCache.ContainsKey(cacheKey))
                    {
                        bool protoHitscan = false;
                        if (meta?.EntityPrototype != null)
                        {
                            string pId = meta.EntityPrototype.ID.ToLowerInvariant();
                            string pName = meta.EntityName.ToLowerInvariant();

                            if (NameKeywords.Any(kw => pId.Contains(kw) || pName.Contains(kw)))
                            {
                                protoHitscan = true;
                                detectedInComp = "NameMatch";
                            }
                            else if (HitscanPrototypes.Contains(meta.EntityPrototype.ID))
                            {
                                protoHitscan = true;
                                detectedInComp = "Whitelist";
                            }
                        }

                        ProtoHitscanCache[cacheKey] = protoHitscan;
                        if (protoHitscan)
                            isHitscan = true;
                    }

                    // Inspect components using cached accessors so runtime ProjectileSpeedModified changes are respected
                    foreach (var comp in _entMan.GetComponents(gunUid.Value))
                    {
                        var t = comp.GetType();
                        if (!CompAccessorCache.TryGetValue(t, out var accessor))
                        {
                            string cName = t.Name.ToLowerInvariant();
                            bool isHitscanComp = HitscanKeywords.Any(kw => cName.Contains(kw));
                            accessor = (isHitscanComp, Reflect.HasAnyMember(t, PotentialSpeedNames, ReflectOptions.Any));
                            CompAccessorCache[t] = accessor;
                        }

                        if (!isHitscan && accessor.IsHitscanComp)
                        {
                            isHitscan = true;
                            detectedInComp = $"Weapon:{t.Name}";
                            break;
                        }

                        if (!isHitscan && !speedDetected && accessor.HasSpeedMember)
                        {
                            if (ReflectConvert.TryToDouble(Reflect.GetFirst(comp, PotentialSpeedNames), out var val) && val > 0d)
                            {
                                bulletSpeed = (float)val;
                                speedDetected = true;
                                detectedInComp = t.Name;
                            }
                        }
                    }

                    if (bulletSpeed > 150f) isHitscan = true;

                    if (LoggedWeapons.Add(cacheKey))
                    {
                        string gunName = _entMan.ToPrettyString(gunUid.Value);
                        string source = isHitscan ? detectedInComp : (speedDetected ? detectedInComp : "FALLBACK");
                        if (isHitscan)
                            Logger.GetSawmill("RobustaAim").Info($"> DETECT: {gunName} | Type: HITSCAN | Src: {source}");
                        else
                            Logger.GetSawmill("RobustaAim").Info($"> DETECT: {gunName} | Type: BALLISTIC | Speed: {bulletSpeed}m/s ({source})");
                    }

                    if (isHitscan)
                    {
                        return _entMan.GetNetCoordinates(new EntityCoordinates(targetUid, finalOffset));
                    }

                    var predictedResult = _aim.GetSilentAimTarget(_input.MouseScreenPosition, bulletSpeed);
                    if (predictedResult != null)
                    {
                        finalOffset = predictedResult.Value.Position.Position - targetWorldPos;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.GetSawmill("RobustaAim").Error($"Predict crash: {e.Message}");
            }
        }

        return _entMan.GetNetCoordinates(new EntityCoordinates(targetUid, finalOffset));
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var requestShootCoordinatesField = AccessTools.Field(typeof(RequestShootEvent), nameof(RequestShootEvent.Coordinates));
        var methodInfo = AccessTools.Method(typeof(GunUpdatePatch), nameof(Patch));
        var codes = new List<CodeInstruction>(instructions);
        var index = codes.FindIndex(c => c.StoresField(requestShootCoordinatesField));
        if (index != -1) codes.Insert(index, new CodeInstruction(OpCodes.Call, methodInfo));
        return codes.AsEnumerable();
    }
}