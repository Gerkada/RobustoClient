using System;
using System.Linq;
using Content.Shared.Hands.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Content.Shared.Hands;
using Content.Shared.Input;
using Content.Shared.Wieldable.Components;
using Content.Shared.Tag;
using Content.Shared.DoAfter;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Client.GameObjects;

namespace RobustoClient.Systems.AutoReload;

// Idle: Waiting for trigger
public sealed class IdleState : ReloadStateBase
{
    public static readonly IdleState Instance = new();
    public override IReloadState Execute(float frameTime, AutoReloadContext context) => this;
}

// Phase 1: Analyze active weapon
public sealed class CheckWeaponState : ReloadStateBase
{
    public static readonly CheckWeaponState Instance = new();
    public override IReloadState Execute(float frameTime, AutoReloadContext context)
    {
        var localPlayer = context.PlayerManager.LocalSession?.AttachedEntity;
        if (localPlayer == null)
            return IdleState.Instance;

        if (!context.EntManager.TryGetComponent<HandsComponent>(localPlayer.Value, out var hands))
        {
            context.Logger.Warning("[AutoReload] Could not find hands component on player.");
            return IdleState.Instance;
        }

        if (!context.HandsSystem.TryGetActiveItem(localPlayer.Value, out var gunUid))
        {
            context.Logger.Warning("[AutoReload] Active hand is empty.");
            return IdleState.Instance;
        }

        if (gunUid == null || !context.EntManager.TryGetComponent<GunComponent>(gunUid.Value, out var gun))
        {
            context.Logger.Warning("[AutoReload] Held item is not a gun.");
            return IdleState.Instance;
        }

        var ev = new GetAmmoCountEvent();
        context.EntManager.EventBus.RaiseLocalEvent(gunUid.Value, ref ev);

        if (ev.Count == ev.Capacity && ev.Capacity > 0)
        {
            context.Logger.Info("[AutoReload] Gun is already full.");
            return IdleState.Instance;
        }

        context.Player = localPlayer;
        context.Gun = gunUid;
        context.GunHandName = hands.ActiveHandId;

        // Check if gun was wielded
        if (context.EntManager.TryGetComponent<WieldableComponent>(gunUid.Value, out var wieldable) && wieldable.Wielded)
        {
            context.WasWielded = true;
            context.Logger.Info("[AutoReload] Gun is currently wielded.");
        }

        if (context.EntManager.HasComponent<MagazineAmmoProviderComponent>(gunUid.Value) || 
            context.EntManager.HasComponent<ChamberMagazineAmmoProviderComponent>(gunUid.Value))
        {
            context.IsMagazineType = true;
            context.Logger.Info($"[AutoReload] Detected magazine-fed gun: {context.EntManager.ToPrettyString(gunUid.Value)}");
        }
        else if (context.EntManager.HasComponent<BallisticAmmoProviderComponent>(gunUid.Value))
        {
            context.IsMagazineType = false;
            context.Logger.Info($"[AutoReload] Detected ballistic/internal gun: {context.EntManager.ToPrettyString(gunUid.Value)}");
        }
        else
        {
            var comps = context.EntManager.GetComponents(gunUid.Value);
            var compNames = string.Join(", ", comps.Select(c => c.GetType().Name));
            context.Logger.Warning($"[AutoReload] Gun has unknown ammo provider. Attached components: {compNames}");
            return IdleState.Instance;
        }

        return SearchAmmoState.Instance;
    }
}

// Phase 2: Search inventory for matching ammo/magazines
public sealed class SearchAmmoState : ReloadStateBase
{
    public static readonly SearchAmmoState Instance = new();

    public override IReloadState Execute(float frameTime, AutoReloadContext context)
    {
        if (context.Player == null || context.Gun == null)
            return IdleState.Instance;

        // Check if gun is already full (important for loops)
        var fullEv = new GetAmmoCountEvent();
        context.EntManager.EventBus.RaiseLocalEvent(context.Gun.Value, ref fullEv);
        if (fullEv.Count == fullEv.Capacity && fullEv.Capacity > 0)
        {
            context.Logger.Info("[AutoReload] Gun is already full.");
            if (context.IsLooping) return CleanupState.Instance;
            return IdleState.Instance;
        }

        EntityWhitelist? whitelist = null;

        if (context.IsMagazineType)
        {
            if (context.EntManager.TryGetComponent<ItemSlotsComponent>(context.Gun.Value, out var slots))
            {
                if (slots.Slots.TryGetValue(SharedGunSystem.MagazineSlot, out var slot))
                    whitelist = slot.Whitelist;
            }
        }
        else
        {
            if (context.EntManager.TryGetComponent<BallisticAmmoProviderComponent>(context.Gun.Value, out var ballistic))
                whitelist = ballistic.Whitelist;
        }

        if (whitelist == null)
        {
            context.Logger.Warning("[AutoReload] Could not determine ammo whitelist for the gun.");
            if (context.IsLooping) return CleanupState.Instance;
            return IdleState.Instance;
        }

        EntityUid? bestAmmo = null;
        int maxAmmo = -1;
        EntityUid? bestAmmoStorage = null;

        var slotsToScan = new[] { "belt", "back", "pocket1", "pocket2", "pocket3", "pocket4", "suitstorage" };

        foreach (var slotName in slotsToScan)
        {
            if (!context.InventorySystem.TryGetSlotEntity(context.Player.Value, slotName, out var slotEnt))
                continue;

            if (IsValidAmmo(slotEnt.Value, whitelist, context, out var count))
            {
                if (count > maxAmmo)
                {
                    maxAmmo = count;
                    bestAmmo = slotEnt;
                    bestAmmoStorage = context.Player;
                }
            }

            if (context.EntManager.TryGetComponent<StorageComponent>(slotEnt.Value, out var storage))
            {
                foreach (var contained in storage.Container.ContainedEntities)
                {
                    if (IsValidAmmo(contained, whitelist, context, out count))
                    {
                        if (count > maxAmmo)
                        {
                            maxAmmo = count;
                            bestAmmo = contained;
                            bestAmmoStorage = slotEnt;
                        }
                    }
                }
            }
        }

        if (bestAmmo != null && maxAmmo >= 0)
        {
            context.FoundAmmo = bestAmmo;
            context.AmmoSourceContainer = bestAmmoStorage;
            context.IsBallisticBox = !context.IsMagazineType && context.EntManager.HasComponent<BallisticAmmoProviderComponent>(bestAmmo.Value);
            context.Logger.Info($"[AutoReload] Found best ammo: {context.EntManager.ToPrettyString(bestAmmo.Value)} (Count: {maxAmmo}) in {context.EntManager.ToPrettyString(bestAmmoStorage ?? EntityUid.Invalid)}");
            context.Logger.Info("[AutoReload] IDEAL SEQUENCE: 1. Swap Hand -> 2. Open Storage -> 3. Take Mag -> 4. Swap Mag on Gun -> 5. Put old mag away -> 6. Swap Hand back -> 7. Wield -> 8. Rack.");
            return PrepareHandsState.Instance;
        }

        context.Logger.Warning("[AutoReload] No suitable ammo found in inventory.");
        if (context.IsLooping) return CleanupState.Instance;
        return IdleState.Instance;
    }

    private bool IsValidAmmo(EntityUid uid, EntityWhitelist whitelist, AutoReloadContext context, out int count)
    {
        count = 0;
        if (uid == context.Gun)
            return false;

        var name = context.EntManager.ToPrettyString(uid);

        // 1. Direct match (loose shell, magazine)
        bool whitelistMatch = !context.WhitelistSystem.IsWhitelistFail(whitelist, uid);
        if (whitelistMatch)
        {
            // Ignore spent cartridges
            if (context.EntManager.TryGetComponent<CartridgeAmmoComponent>(uid, out var cartridge) && cartridge.Spent)
            {
                context.Logger.Info($"[AutoReload] Rejected: {name} (Spent cartridge)");
                return false;
            }

            var ev = new GetAmmoCountEvent();
            context.EntManager.EventBus.RaiseLocalEvent(uid, ref ev);
            count = ev.Count;
            
            if (count == 0)
            {
                if (context.EntManager.HasComponent<AmmoComponent>(uid) || context.EntManager.HasComponent<CartridgeAmmoComponent>(uid))
                {
                    count = 1;
                    context.Logger.Debug($"[AutoReload] {name} is a single ammo/cartridge");
                }
            }

            if (count > 0)
            {
                context.Logger.Info($"[AutoReload] Found valid direct ammo: {name} (Count: {count})");
                return true;
            }
            else
            {
                context.Logger.Info($"[AutoReload] Rejected: {name} (Whitelist OK but no count/component found)");
            }
        }

        // 2. Ballistic Ammo Provider (Ammo Box)
        if (context.EntManager.TryGetComponent<BallisticAmmoProviderComponent>(uid, out var provider))
        {
            bool providerMatches = false;
            
            // Check contained entities
            foreach (var contained in provider.Container.ContainedEntities)
            {
                if (!context.WhitelistSystem.IsWhitelistFail(whitelist, contained))
                {
                    providerMatches = true;
                    break;
                }
            }

            // Check if it can spawn valid ammo via Proto
            if (!providerMatches && provider.Proto != null)
            {
                providerMatches = IsPrototypeValid(whitelist, provider.Proto.Value, context);
            }

            if (providerMatches)
            {
                count = provider.UnspawnedCount + provider.Container.ContainedEntities.Count;
                context.Logger.Info($"[AutoReload] Found valid ammo provider (box): {name} (Count: {count})");
                return true;
            }
        }

        context.Logger.Info($"[AutoReload] Rejected: {name} (WhitelistMatch: {whitelistMatch})");
        return false;
    }

    private bool IsPrototypeValid(EntityWhitelist whitelist, string protoId, AutoReloadContext context)
    {
        if (!context.ProtoManager.TryIndex<EntityPrototype>(protoId, out var proto))
            return false;

        if (whitelist.RequireAll)
        {
            if (whitelist.Components != null)
            {
                foreach (var compName in whitelist.Components)
                {
                    if (!proto.Components.ContainsKey(compName)) return false;
                }
            }
            if (whitelist.Tags != null)
            {
                if (!proto.TryGetComponent<TagComponent>(out var tagComp, context.EntManager.ComponentFactory))
                    return whitelist.Tags.Count == 0;
                
                foreach (var tag in whitelist.Tags)
                {
                    if (!tagComp.Tags.Contains(tag)) return false;
                }
            }
            return true;
        }
        else
        {
            if (whitelist.Components != null)
            {
                foreach (var compName in whitelist.Components)
                {
                    if (proto.Components.ContainsKey(compName)) return true;
                }
            }
            if (whitelist.Tags != null)
            {
                if (proto.TryGetComponent<TagComponent>(out var tagComp, context.EntManager.ComponentFactory))
                {
                    foreach (var tag in whitelist.Tags)
                    {
                        if (tagComp.Tags.Contains(tag)) return true;
                    }
                }
            }
            return false;
        }
    }
}

// Phase 3: Free hands and retrieve ammo
public sealed class PrepareHandsState : ReloadStateBase
{
    public static readonly PrepareHandsState Instance = new();

    public override IReloadState Execute(float frameTime, AutoReloadContext context)
    {
        if (context.Player == null || context.FoundAmmo == null || context.AmmoSourceContainer == null)
            return IdleState.Instance;

        if (!context.EntManager.TryGetComponent<HandsComponent>(context.Player.Value, out var hands))
            return IdleState.Instance;

        // Step 0: Find empty hand and swap to it explicitly via Network Event
        if (context.RetrieveStep == 0)
        {
            string? emptyHandName = null;
            foreach (var handName in hands.Hands.Keys)
            {
                if (context.ContainerSystem.TryGetContainer(context.Player.Value, handName, out var container) && container.ContainedEntities.Count == 0)
                {
                    emptyHandName = handName;
                    break;
                }
            }

            if (emptyHandName == null)
            {
                if (!context.AttemptedForceUnwield)
                {
                    // Find ANY off-hand to swap to and break wield
                    string? offHandName = hands.Hands.Keys.FirstOrDefault(h => h != context.GunHandName);
                    if (offHandName != null)
                    {
                        context.AttemptedForceUnwield = true;
                        context.EntManager.RaisePredictiveEvent(new RequestSetHandEvent(offHandName));
                        context.ExecutedSteps.Add("[Prepare 0] No empty hand. Swapping to off-hand to force unwield.");
                        context.RetrieveStep = -1;
                        context.Timer = 0f;
                        return this;
                    }
                }

                context.Logger.Warning("[AutoReload] No empty hand available! Please free a hand.");
                return IdleState.Instance;
            }

            context.EmptyHandName = emptyHandName;
            
            // Explicit network request to swap hands.
            context.EntManager.RaisePredictiveEvent(new RequestSetHandEvent(emptyHandName));
            
            var actualActiveHand = context.HandsSystem.GetActiveHand(context.Player.Value);
            context.ExecutedSteps.Add($"[Prepare 0] Sent RequestSetHandEvent({emptyHandName}). Current hand: {actualActiveHand}");

            context.RetrieveStep = 1;
            context.Timer = 0f;
            return this;
        }

        // Step -1 and -2 handle force unwield loops as defined in previous directives...
        if (context.RetrieveStep == -1)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.2f) return this;
            if (context.GunHandName != null)
            {
                context.EntManager.RaisePredictiveEvent(new RequestSetHandEvent(context.GunHandName));
                context.ExecutedSteps.Add("[Prepare ForceUnwield] Swapping back to gun hand.");
            }
            context.RetrieveStep = -2;
            context.Timer = 0f;
            return this;
        }
        if (context.RetrieveStep == -2)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.2f) return this;
            context.RetrieveStep = 0;
            context.Timer = 0f;
            return this;
        }

        // Step 1: Wait for hand swap and open storage
        if (context.RetrieveStep == 1)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            var actualActiveHand = context.HandsSystem.GetActiveHand(context.Player.Value);

            if (context.AmmoSourceContainer == context.Player)
            {
                // Retrieve from direct inventory (belt/pocket)
                if (context.InventorySystem.TryGetContainingSlot(context.FoundAmmo.Value, out var slotDef))
                {
                    context.ExecutedSteps.Add($"[Prepare 1] Taking ammo from inventory slot {slotDef.Name}. Active hand: {actualActiveHand}");
                    var ev = new UseSlotNetworkMessage(slotDef.Name);
                    context.EntManager.RaisePredictiveEvent(ev);
                    
                    context.Timer = 0f;
                    context.ActionStep = 0;
                    return ActionState.Instance;
                }
                return IdleState.Instance;
            }
            else
            {
                // Open storage UI explicitly
                if (context.InventorySystem.TryGetContainingSlot(context.AmmoSourceContainer.Value, out var slotDef))
                {
                    context.ExecutedSteps.Add($"[Prepare 1] Sent OpenSlotStorageNetworkMessage({slotDef.Name}). Active hand: {actualActiveHand}");
                    var ev = new OpenSlotStorageNetworkMessage(slotDef.Name);
                    context.EntManager.RaisePredictiveEvent(ev);
                    context.RetrieveStep = 2;
                    context.Timer = 0f;
                    return this;
                }
                return IdleState.Instance;
            }
        }

        // Step 2: Wait for UI and take ammo
        if (context.RetrieveStep == 2)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            var actualActiveHand = context.HandsSystem.GetActiveHand(context.Player.Value);
            context.ExecutedSteps.Add($"[Prepare 2] Taking ammo from storage UI. Active hand: {actualActiveHand}");

            var ammoNet = context.EntManager.GetNetEntity(context.FoundAmmo.Value);
            var storageNet = context.EntManager.GetNetEntity(context.AmmoSourceContainer.Value);
            var msg = new StorageInteractWithItemEvent(ammoNet, storageNet);
            context.EntManager.RaisePredictiveEvent(msg);
            
            context.Timer = 0f;
            context.ActionStep = 0;
            return ActionState.Instance;
        }

        return IdleState.Instance;
    }
}

// Phase 4: Swap magazines or initiate ballistic reload
public sealed class ActionState : ReloadStateBase
{
    public static readonly ActionState Instance = new();

    public override IReloadState Execute(float frameTime, AutoReloadContext context)
    {
        if (context.Player == null || context.Gun == null || context.FoundAmmo == null || context.GunHandName == null)
            return IdleState.Instance;

        var actualActiveHand = context.HandsSystem.GetActiveHand(context.Player.Value);

        // Safety check to avoid used == target crash
        if (actualActiveHand == context.GunHandName)
        {
            context.Logger.Warning("[AutoReload] Active hand is still the gun hand. Waiting for swap...");
            return this;
        }

        // Ballistic: Initiate interaction with delay
        if (!context.IsMagazineType)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            context.ExecutedSteps.Add($"[Action] Initiating ballistic reload via RequestHandInteractUsingEvent. Active hand: {actualActiveHand}");
            // Use RequestHandInteractUsingEvent to click active hand (ammo) on gun hand
            context.EntManager.RaisePredictiveEvent(new RequestHandInteractUsingEvent(context.GunHandName));
            
            context.Timer = 0f;
            if (context.IsBallisticBox)
            {
                context.ExecutedSteps.Add("[Action] It's a box. Transitioning to WaitBallisticState.");
                return WaitBallisticState.Instance;
            }
            
            context.IsLooping = true;
            context.RetrieveStep = 0; // Prepare to take next shell
            return SearchAmmoState.Instance;
        }

        // Magazine: Wait for ammo -> Interact (Swaps magazines) -> Wait for swap
        if (context.ActionStep == 0)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            context.ExecutedSteps.Add($"[Action 0] Swapping magazines via RequestHandInteractUsingEvent. Active hand: {actualActiveHand}");
            // Use RequestHandInteractUsingEvent to click active hand (new mag) on gun hand
            context.EntManager.RaisePredictiveEvent(new RequestHandInteractUsingEvent(context.GunHandName));
            
            context.ActionStep = 1;
            context.Timer = 0f;
            return this;
        }

        if (context.ActionStep == 1)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;
            
            context.ExecutedSteps.Add($"[Action 1] Waited for magazine swap. Active hand: {actualActiveHand}");
            context.Timer = 0f;
            return CleanupState.Instance;
        }

        return IdleState.Instance;
    }
}

// Phase 4.5: Wait for ballistic DoAfters to finish
public sealed class WaitBallisticState : ReloadStateBase
{
    public static readonly WaitBallisticState Instance = new();

    public override IReloadState Execute(float frameTime, AutoReloadContext context)
    {
        if (context.Player == null || context.Gun == null || context.FoundAmmo == null)
            return IdleState.Instance;

        context.Timer += frameTime;

        // Give it a bit of time to start the first DoAfter (0.5s)
        if (context.Timer < 0.5f)
            return this;

        // Check for active DoAfters on the player
        if (context.EntManager.TryGetComponent<DoAfterComponent>(context.Player.Value, out var doAfterComp))
        {
            bool foundActive = false;
            foreach (var doAfter in doAfterComp.DoAfters.Values)
            {
                if (doAfter.Cancelled || doAfter.Completed)
                    continue;

                // Check if this DoAfter is the ammo fill one
                if (doAfter.Args.Target == context.Gun && doAfter.Args.Used == context.FoundAmmo)
                {
                    foundActive = true;
                    break;
                }
            }

            if (foundActive)
            {
                // Reset a small "no-doafter-seen" timer
                context.ActionStep = 0; 
                context.RetrieveStep = 0; // Reusing RetrieveStep as a "started" flag if needed, but ActionStep is enough
                return this;
            }
        }

        // If no active DoAfter found, wait for a small grace period (0.4s) before assuming it's done
        // We use ActionStep to store a secondary timer state or just use Timer.
        // Let's use ActionStep to count frames or just another float if I had one. 
        // Actually I can just reset context.Timer when foundActive is true, but context.Timer is used for the initial 0.5s wait.
        // Let's use a separate timer logic.
        
        if (context.ActionStep == 0)
        {
            // First time we don't see a DoAfter, start a sub-timer
            context.ActionStep = 1;
            context.Timer = 0.5f; // Re-purpose Timer for grace period
        }
        
        context.Timer += frameTime;
        if (context.Timer < 0.9f) // 0.4s grace period
        {
            return this;
        }

        context.ExecutedSteps.Add("[WaitBallistic] No active DoAfters found. Reload complete or interrupted.");
        context.Timer = 0f;
        context.ActionStep = 0;
        return CleanupState.Instance;
    }
}

// Phase 5: Put remaining stuff back and reset
public sealed class CleanupState : ReloadStateBase
{
    public static readonly CleanupState Instance = new();

    public override IReloadState Execute(float frameTime, AutoReloadContext context)
    {
        if (context.Player == null)
            return IdleState.Instance;

        var actualActiveHand = context.HandsSystem.GetActiveHand(context.Player.Value);

        // Step 0: Try to put item (mag/box) away in backpack
        if (context.CleanupStep == 0)
        {
            // Safety: ensure we are in the off-hand before putting stuff away
            if (actualActiveHand == context.GunHandName && context.EmptyHandName != null)
            {
                context.Logger.Warning($"[Cleanup 0] Active hand is gun hand ({actualActiveHand}), but we need off-hand ({context.EmptyHandName}). Swapping back...");
                context.EntManager.RaisePredictiveEvent(new RequestSetHandEvent(context.EmptyHandName));
                return this; 
            }

            // Check if we still have something in hand that we retrieved
            if (context.HandsSystem.TryGetActiveItem(context.Player.Value, out var heldItem) && heldItem != null)
            {
                if (heldItem == context.Gun)
                {
                    context.Logger.Warning("[Cleanup 0] Active hand holds the gun! Skipping storage logic to avoid putting weapon away.");
                    context.CleanupStep = 2;
                    return this;
                }

                bool shouldStore = true;

                // For boxes: if empty, drop it
                if (context.IsBallisticBox && context.EntManager.TryGetComponent<BallisticAmmoProviderComponent>(heldItem.Value, out var box))
                {
                    if (box.UnspawnedCount + box.Container.ContainedEntities.Count == 0)
                    {
                        shouldStore = false;
                        context.ExecutedSteps.Add("[Cleanup 0] Box is empty. Will drop.");
                    }
                }

                if (shouldStore)
                {
                    context.ExecutedSteps.Add($"[Cleanup 0] Attempting to store {context.EntManager.ToPrettyString(heldItem.Value)} in backpack. Active hand: {actualActiveHand}");
                    var ev = new UseSlotNetworkMessage("back");
                    context.EntManager.RaisePredictiveEvent(ev);
                    
                    context.CleanupStep = 1;
                    context.Timer = 0f;
                    return this;
                }
                else
                {
                    // Proceed to drop in step 1
                    context.CleanupStep = 1;
                    context.Timer = 0.6f; // Force drop logic immediately
                    return this;
                }
            }
            context.CleanupStep = 2; // Skip to hand restore
        }

        // Step 1: Wait and check if still holding item (backpack was full) -> Drop if necessary
        if (context.CleanupStep == 1)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            if (context.HandsSystem.TryGetActiveItem(context.Player.Value, out var heldItem) && heldItem != null)
            {
                context.ExecutedSteps.Add($"[Cleanup 1] Backpack full or empty box. Dropping item: {context.EntManager.ToPrettyString(heldItem.Value)}. Active hand: {actualActiveHand}");
                
                var xform = context.EntManager.GetComponent<TransformComponent>(context.Player.Value);
                var coords = xform.Coordinates;
                var funcId = context.InputManager.NetworkBindMap.KeyFunctionID(ContentKeyFunctions.Drop);
                
                var msg = new FullInputCmdMessage(
                    context.GameTiming.CurTick,
                    context.GameTiming.TickFraction,
                    funcId,
                    BoundKeyState.Down,
                    context.EntManager.GetNetCoordinates(coords),
                    ScreenCoordinates.Invalid,
                    context.EntManager.GetNetEntity(context.Player.Value)
                );
                
                context.EntManager.System<InputSystem>().HandleInputCommand(context.PlayerManager.LocalSession, ContentKeyFunctions.Drop, msg);
                
                context.CleanupStep = 2;
                context.Timer = 0f;
                return this;
            }
            
            context.ExecutedSteps.Add($"[Cleanup 1] Item successfully stored.");
            context.CleanupStep = 2;
            context.Timer = 0f;
            return this;
        }

        // Step 2: Wait and restore active hand via Network Event
        if (context.CleanupStep == 2)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.2f)
                return this;

            if (context.GunHandName != null)
            {
                context.EntManager.RaisePredictiveEvent(new RequestSetHandEvent(context.GunHandName));
                var finalHand = context.HandsSystem.GetActiveHand(context.Player.Value);
                context.ExecutedSteps.Add($"[Cleanup 2] Sent RequestSetHandEvent({context.GunHandName}). Current hand: {finalHand}");
            }

            context.CleanupStep = 3;
            context.Timer = 0f;
            return this;
        }

        // Step 3: Wield weapon if it was wielded before - USE UseInHand for Wielding
        if (context.CleanupStep == 3)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            if (context.WasWielded)
            {
                context.ExecutedSteps.Add($"[Cleanup 3] Re-wielding weapon via RequestUseInHandEvent. Active hand: {actualActiveHand}");
                context.EntManager.RaisePredictiveEvent(new RequestUseInHandEvent());
            }

            context.CleanupStep = 4;
            context.Timer = 0f;
            return this;
        }

        // Step 4: Rack the slide if necessary / Force UI update
        if (context.CleanupStep == 4)
        {
            context.Timer += frameTime;
            if (context.Timer < 0.5f)
                return this;

            if (context.Gun != null)
            {
                var gunUid = context.Gun.Value;
                bool needsRack = false;

                if (context.EntManager.TryGetComponent<ChamberMagazineAmmoProviderComponent>(gunUid, out var chamberMag))
                {
                    // Check if bolt is open
                    if (chamberMag.BoltClosed == false)
                    {
                        needsRack = true;
                    }
                    else
                    {
                        // Check if chamber is actually empty using ContainerSystem
                        if (context.ContainerSystem.TryGetContainer(gunUid, SharedGunSystem.ChamberSlot, out var chamber) &&
                            chamber.ContainedEntities.Count == 0)
                        {
                            needsRack = true;
                        }
                    }
                }

                if (needsRack)
                {
                    context.ExecutedSteps.Add($"[Cleanup 4] Racking slide via RequestUseInHandEvent. Active hand: {actualActiveHand}");
                    context.EntManager.RaisePredictiveEvent(new RequestUseInHandEvent());
                }
            }

            context.Logger.Info("[AutoReload] Reload sequence finished.");
            context.Logger.Info("[AutoReload] EXECUTED STEPS:\n" + string.Join("\n", context.ExecutedSteps));
            context.Reset();
            return IdleState.Instance;
        }

        return IdleState.Instance;
    }
}
