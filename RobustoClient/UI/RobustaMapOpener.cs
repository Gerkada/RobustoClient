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
        
        // Get player position
        var player = playerMan.LocalSession?.AttachedEntity;
        
        if (player == null || !player.Value.IsValid())
            return;

        // Get transform component to determine which grid (station) we are on
        if (!entMan.TryGetComponent(player.Value, out TransformComponent? xform))
            return;

        var grid = xform.GridUid;
        if (grid == null)
            return;

        var stationName = string.Empty;
        if (entMan.TryGetComponent(grid.Value, out MetaDataComponent? meta))
            stationName = meta.EntityName;

        // Create map window (new one on each click to avoid closing bugs)
        var window = new StationMapWindow();
        window.Title = "Robusta GPS"; // Title can be anything

        // Pass station name, grid, and character to the map for centering
        window.Set(stationName, grid.Value, player.Value);
        window.OpenCentered();
    }
}