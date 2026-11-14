using Content.Shared.Overlays;
using Robust.Client.Player;
using Robust.Shared.Player;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems;

/// <summary>
/// Turns the upstream health-bar overlay on or off.
/// </summary>
/// <remarks>
/// The overlay belongs to <c>ShowHealthBarsSystem</c>, an <c>EquipmentHudSystem</c>. That system
/// subscribes to its own refresh event on whichever entity holds the component, so a
/// <see cref="ShowHealthBarsComponent"/> on the local player is enough to activate it, and the
/// component survives the player changing clothes.
/// </remarks>
public class RobustaHealthBarSystem : LocalPlayerAddCompSystem<ShowHealthBarsComponent>
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    /// <summary>Restores the saved switch when a new local player attaches.</summary>
    protected override void OnAttached(LocalPlayerAttachedEvent ev) =>
        SetEnabled(RobustaConfig.Esp.Health.Enabled);

    /// <summary>Adds or removes the overlay for the local player.</summary>
    public void SetEnabled(bool enabled)
    {
        if (_playerManager.LocalEntity is not { } uid || !Exists(uid))
            return;

        if (enabled)
        {
            if (!HasComp<ShowHealthBarsComponent>(uid))
                AddComp(uid, NewComp());
        }
        else if (HasComp<ShowHealthBarsComponent>(uid))
        {
            RemComp<ShowHealthBarsComponent>(uid);
        }
    }

    // Client-side only, so it never reaches the server. Both damage containers are listed because a
    // silicon mob would otherwise get no bar.
    private static ShowHealthBarsComponent NewComp() => new()
    {
        DamageContainers = ["Biological", "Silicon"],
        NetSyncEnabled = false,
    };
}
