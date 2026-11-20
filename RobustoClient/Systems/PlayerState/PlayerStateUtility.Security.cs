using Content.Shared.Security;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Security block: department membership and the station's criminal status.</summary>
public sealed partial class PlayerStateUtility
{
    private IPlayerData? BuildSecurity(EntityUid entity, PlayerFlagReport flags)
    {
        var status = EntityManager.System<RobustaSyndicateDetectorSystem>().GetWantedStatus(entity);
        var isSecurity = flags.Has(PlayerFlag.IsSecurity);

        if (!isSecurity && status == SecurityStatus.None)
            return null;

        return new SecurityData(status, isSecurity);
    }
}
