using Robust.Shared.GameObjects;
using RobustoClient.Systems;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Inventory block: the flat item list behind the inventory window.</summary>
/// <remarks> Abstracted via RobustaInventoryViewerSystem </remarks>
public sealed partial class PlayerStateUtility
{
    private IPlayerData BuildInventory(EntityUid entity) =>
        new InventoryData(EntityManager.System<RobustaInventoryViewerSystem>().GetFullInventory(entity));
}
