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

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(GunSystem), nameof(GunSystem.Update))]
public class GunUpdatePatch
{
    private static IEntityManager? _entMan;
    private static IInputManager? _input;
    private static IPlayerManager? _player;
    private static RobustaAimSystem? _aim;

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
                    var gunComps = _entMan.GetComponents(gunUid.Value).ToList();
                    string gunName = _entMan.ToPrettyString(gunUid.Value);
                    
                    bool isHitscan = false;
                    bool speedDetected = false;
                    string detectedInComp = "";
                    float bulletSpeed = RobustaConfig.DefaultProjectileSpeed;
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                    var hitscanKeywords = new[] { "hitscan", "beam", "instant", "raycast" };

                    // 1. DETECTION VIA WEAPON COMPONENTS
                    foreach (var c in gunComps)
                    {
                        string cName = c.GetType().Name.ToLower();
                        if (hitscanKeywords.Any(kw => cName.Contains(kw)))
                        {
                            isHitscan = true;
                            detectedInComp = $"Weapon:{c.GetType().Name}";
                            break;
                        }
                    }

                    // 2. DETECTION VIA NAME / ID (Primary method for client)
                    _entMan.TryGetComponent<MetaDataComponent>(gunUid.Value, out var meta);
                    if (!isHitscan && meta != null && meta.EntityPrototype != null)
                    {
                        string pId = meta.EntityPrototype.ID.ToLower();
                        string pName = meta.EntityName.ToLower();
                        
                        var nameKeywords = new[] { "laser", "pulse", "xray", "ray", "beam" };
                        
                        if (nameKeywords.Any(kw => pId.Contains(kw) || pName.Contains(kw)))
                        {
                            isHitscan = true;
                            detectedInComp = "NameMatch";
                        }
                    }

                    // 3. WHITELIST (Fallback insurance)
                    if (!isHitscan && meta != null && meta.EntityPrototype != null)
                    {
                        var hitscanPrototypes = new HashSet<string> { 
                            "WeaponAntiqueLaser", "WeaponLaserPistol", "WeaponLaserGun", "WeaponLaserCarbine", 
                            "WeaponLaserCarbinePractice", "WeaponAdvancedLaser", "WeaponLaserCannon", 
                            "WeaponLaserCannonXenoborg", "WeaponLaserGunXenoborg", "WeaponLaserSvalinn", 
                            "WeaponMakeshiftLaser", "WeaponBehonkerLaser",
                            "WeaponPulsePistol", "WeaponPulseCarbine", "WeaponPulseRifle",
                            "WeaponHitscanDebug", "WeaponHitscanDebugGib", "RedShuttleLaser"
                        };
                        if (hitscanPrototypes.Contains(meta.EntityPrototype.ID))
                        {
                            isHitscan = true;
                            detectedInComp = "Whitelist";
                        }
                    }

                    // 4. VELOCITY SEARCH (For Ballistics)
                    if (!isHitscan)
                    {
                        var potentialNames = new[] { "ProjectileSpeedModified", "ProjectileSpeed", "speed", "_projectileSpeed" };
                        foreach (var comp in gunComps)
                        {
                            var t = comp.GetType();
                            foreach (var name in potentialNames)
                            {
                                var prop = t.GetProperty(name, flags);
                                var val = prop?.GetValue(comp) ?? t.GetField(name, flags)?.GetValue(comp);
                                if (val is float f && f > 0) { bulletSpeed = f; speedDetected = true; detectedInComp = t.Name; break; }
                            }
                            if (speedDetected) break;
                        }
                    }

                    if (bulletSpeed > 150f) isHitscan = true;

                    // LOGGING
                    if (isHitscan)
                        Logger.GetSawmill("RobustaAim").Info($"> DETECT: {gunName} | Type: HITSCAN | Src: {detectedInComp}");
                    else
                        Logger.GetSawmill("RobustaAim").Info($"> DETECT: {gunName} | Type: BALLISTIC | Speed: {bulletSpeed}m/s ({ (speedDetected ? detectedInComp : "FALLBACK") })");

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