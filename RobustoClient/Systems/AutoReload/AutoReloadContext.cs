using Content.Shared.Inventory;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Whitelist;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Shared.Containers;
using Robust.Shared.Timing;
using Robust.Shared.Prototypes;
using Robust.Client.Input;
using Robust.Client.Player;
using RobustoClient.Systems.AutoChem;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;

namespace RobustoClient.Systems.AutoReload;

public sealed class AutoReloadContext
{
    [Dependency] public readonly IEntityManager EntManager = default!;
    [Dependency] public readonly IPlayerManager PlayerManager = default!;
    [Dependency] public readonly IInputManager InputManager = default!;
    [Dependency] public readonly IGameTiming GameTiming = default!;
    [Dependency] public readonly IPrototypeManager ProtoManager = default!;

    public InventorySystem InventorySystem => EntManager.System<InventorySystem>();
    public SharedStorageSystem StorageSystem => EntManager.System<SharedStorageSystem>();
    public EntityWhitelistSystem WhitelistSystem => EntManager.System<EntityWhitelistSystem>();
    public SharedContainerSystem ContainerSystem => EntManager.System<SharedContainerSystem>();
    public SharedHandsSystem HandsSystem => EntManager.System<SharedHandsSystem>();
    public SharedInteractionSystem InteractionSystem => EntManager.System<SharedInteractionSystem>();
    public RobustaNetworkHands NetworkHands => EntManager.System<RobustaNetworkHands>();

    public readonly ISawmill Logger;

    // State data
    public EntityUid? Player;
    public EntityUid? Gun;
    public EntityUid? FoundAmmo;
    public EntityUid? AmmoSourceContainer; // Backpack or belt where ammo was found
    public string? GunHandName;
    public string? EmptyHandName;

    public float Timer = 0f;
    public bool IsMagazineType = false;
    public bool IsBallisticBox = false;
    public bool IsLooping = false;
    public int RetrieveStep = 0;
    public int ActionStep = 0;
    public int CleanupStep = 0;
    public bool WasWielded = false;
    public bool AttemptedForceUnwield = false;
    public List<string> ExecutedSteps = new();

    public AutoReloadContext(ISawmill logger)
    {
        IoCManager.InjectDependencies(this);
        Logger = logger;
    }

    public void Reset()
    {
        Player = null;
        Gun = null;
        FoundAmmo = null;
        AmmoSourceContainer = null;
        GunHandName = null;
        EmptyHandName = null;
        Timer = 0f;
        IsMagazineType = false;
        IsBallisticBox = false;
        IsLooping = false;
        RetrieveStep = 0;
        ActionStep = 0;
        CleanupStep = 0;
        WasWielded = false;
        AttemptedForceUnwield = false;
        ExecutedSteps.Clear();
    }
}
