using Content.Client.Pinpointer.UI;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace ArabicaCliento.UI;

public static class ArabicaMapOpener
{
    public static void Show()
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var playerMan = IoCManager.Resolve<IPlayerManager>();
        
        // Получаем позицию игрока
        var player = playerMan.LocalSession?.AttachedEntity;
        
        if (player == null || !player.Value.IsValid())
            return;

        // Получаем компонент трансформации, чтобы узнать, на каком гриде (станции) мы стоим
        if (!entMan.TryGetComponent(player.Value, out TransformComponent? xform))
            return;

        var grid = xform.GridUid;
        if (grid == null)
            return;

        var stationName = string.Empty;
        if (entMan.TryGetComponent(grid.Value, out MetaDataComponent? meta))
            stationName = meta.EntityName;

        // Создаем окно карты (при каждом клике новое, чтобы избежать багов с закрытием)
        var window = new StationMapWindow();
        window.Title = "Arabica GPS"; // Можешь назвать как угодно

        // Передаем карте название станции, сам грид и нашего персонажа для центровки
        window.Set(stationName, grid.Value, player.Value);
        window.OpenCentered();
    }
}