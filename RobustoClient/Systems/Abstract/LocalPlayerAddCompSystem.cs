using Robust.Shared.Player;

namespace RobustoClient.Systems.Abstract;

public abstract class LocalPlayerAddCompSystem<TComp>: LocalPlayerSystem where TComp: Component, new()
{
    protected virtual TComp CompOverride => new();

    private TComp CreateComponent()
    {
        var comp = CompOverride;
        comp.NetSyncEnabled = false;
        return comp;
    }
    
    protected override void OnAttached(LocalPlayerAttachedEvent ev)
    {
        if (!HasComp<TComp>(ev.Entity))
            AddComp(ev.Entity, CreateComponent());
    }
    
    protected override void OnDetached(LocalPlayerDetachedEvent ev)
    {
        RemComp<TComp>(ev.Entity);
    }
}