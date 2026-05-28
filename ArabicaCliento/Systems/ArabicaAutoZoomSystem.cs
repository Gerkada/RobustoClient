using System.Numerics;
using ArabicaCliento.Systems.Abstract;
using Content.Client.Commands;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Player;
using Robust.Shared.GameObjects;

namespace ArabicaCliento.Systems;

public class ArabicaAutoZoomSystem : LocalPlayerSystem
{
    [Dependency] private readonly IPlayerManager _player = default!;

    private float _zoom = 1.5f;
    private bool _isEnabled = true; // По умолчанию зум включен

    // Метод для переключателя (Toggle)
    public void ToggleZoom()
    {
        _isEnabled = !_isEnabled;
        if (!_isEnabled)
        {
            RestoreZoom(); // Мягко возвращаем камеру в норму
        }
    }

    public void UpdateZoom(float zoom)
    {
        _zoom = zoom;
        _isEnabled = true; // Если задали значение вручную, автоматически включаем
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Если выключено или нет игрока - ничего не делаем
        if (!_isEnabled || _player.LocalEntity == null)
            return;

        if (!TryComp<EyeComponent>(_player.LocalEntity.Value, out var eyeComponent))
            return;

        // ЖЕСТКАЯ БЛОКИРОВКА ЗУМА: каждый кадр проверяем и ставим наше значение,
        // чтобы изменения ФОВа его не сбрасывали.
        var targetVector = new Vector2(_zoom, _zoom);
        if (eyeComponent.Eye.Zoom != targetVector)
        {
            eyeComponent.Eye.Zoom = targetVector;
            eyeComponent.NetSyncEnabled = false; // Блокируем синхронизацию с сервером
        }
    }

    protected override void OnAttached(LocalPlayerAttachedEvent ev)
    {
        if (!TryComp<EyeComponent>(ev.Entity, out var eyeComponent))
            return;
            
        eyeComponent.NetSyncEnabled = false;
        // Само значение зума выставится в следующем же кадре в Update
    }

    protected override void OnDetached(LocalPlayerDetachedEvent ev)
    {
        RestoreZoom(ev.Entity);
    }

    // Вспомогательный метод для сброса зума к настройкам базовой игры
    private void RestoreZoom(EntityUid? entity = null)
    {
        var target = entity ?? _player.LocalEntity;
        if (target == null) return;

        if (TryComp<EyeComponent>(target.Value, out var eyeComponent))
        {
            eyeComponent.Eye.Zoom = eyeComponent.Zoom; // Сброс до дефолта движка
            eyeComponent.NetSyncEnabled = true; // Возвращаем контроль серверу
        }
    }
}