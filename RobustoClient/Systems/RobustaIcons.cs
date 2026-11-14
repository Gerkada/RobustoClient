using Content.Shared.Overlays;
using Robust.Client.Player;
using Robust.Shared.Player;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems;

/// <summary>
/// Turns the upstream job-icon overlay on or off, using the same mechanism as
/// <see cref="RobustaHealthBarSystem"/>: a <see cref="ShowJobIconsComponent"/> on the local player
/// activates the <c>ShowJobIconsSystem</c> HUD, which then makes the status-icon overlay add job icons.
/// </summary>
public class RobustaJobIconsSystem : LocalPlayerAddCompSystem<ShowJobIconsComponent>
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    /// <summary>Restores the saved switch when a new local player attaches.</summary>
    protected override void OnAttached(LocalPlayerAttachedEvent ev) =>
        SetEnabled(RobustaConfig.Esp.Icons.Enabled);

    public void SetEnabled(bool enabled)
    {
        if (_playerManager.LocalEntity is not { } uid || !Exists(uid))
            return;

        if (enabled)
        {
            if (!HasComp<ShowJobIconsComponent>(uid))
                AddComp(uid, new ShowJobIconsComponent { NetSyncEnabled = false });
        }
        else if (HasComp<ShowJobIconsComponent>(uid))
        {
            RemComp<ShowJobIconsComponent>(uid);
        }
    }
}

public class RobustaCriminalRecordIcons : LocalPlayerAddCompSystem<ShowCriminalRecordIconsComponent>;
public class RobustaMindShieldIcons : LocalPlayerAddCompSystem<ShowMindShieldIconsComponent>;
public class RobustaSyndicateIcons : LocalPlayerAddCompSystem<ShowSyndicateIconsComponent>;
