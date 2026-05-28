using JetBrains.Annotations;
using Robust.Shared.Player;

namespace RobustoClient.Systems.Abstract;

[UsedImplicitly(ImplicitUseTargetFlags.WithInheritors)]
public abstract class LocalPlayerSystem: EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnDetached);
    }
    
    protected abstract void OnAttached(LocalPlayerAttachedEvent ev);
    
    protected abstract void OnDetached(LocalPlayerDetachedEvent ev);

}