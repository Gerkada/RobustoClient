using Content.Client.Pinpointer.UI;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RobustoClient.UI;

public static class RobustaMapOpener
{
    public static void Show()
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var playerMan = IoCManager.Resolve<IPlayerManager>();
        var player = playerMan.LocalSession?.AttachedEntity;
        
        if (player == null || !player.Value.IsValid())
            return;

        // The grid the player stands on is the station to centre on.
        if (!entMan.TryGetComponent(player.Value, out TransformComponent? xform))
            return;

        var grid = xform.GridUid;
        if (grid == null)
            return;

        var stationName = string.Empty;
        if (entMan.TryGetComponent(grid.Value, out MetaDataComponent? meta))
            stationName = meta.EntityName;

        // A fresh window per click, reusing one hit a close bug.
        var window = new StationMapWindow();
        window.Title = "Robusta GPS";
        window.Set(stationName, grid.Value, player.Value);
        window.OpenCentered();
    }
}