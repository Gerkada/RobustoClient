using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RobustoClient.Systems.AutoReload;

public sealed class RobustaAutoReloadSystem : EntitySystem
{
    private AutoReloadContext _context = default!;
    public IReloadState CurrentState { get; private set; } = IdleState.Instance;

    public override void Initialize()
    {
        base.Initialize();
        var sawmill = Robust.Shared.Log.Logger.GetSawmill("autoreload");
        _context = new AutoReloadContext(sawmill);
    }

    public void StartReload()
    {
        if (CurrentState != IdleState.Instance)
            return;

        _context.Reset();
        CurrentState = CheckWeaponState.Instance;
        _context.Logger.Info("[AutoReload] Sequence started.");
    }

    public void StopReload()
    {
        CurrentState = IdleState.Instance;
        _context.Reset();
        _context.Logger.Info("[AutoReload] Sequence aborted.");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (CurrentState == IdleState.Instance)
            return;

        CurrentState = CurrentState.Execute(frameTime, _context);
    }
}
