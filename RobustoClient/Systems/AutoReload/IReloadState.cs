namespace RobustoClient.Systems.AutoReload;

public interface IReloadState
{
    IReloadState Execute(float frameTime, AutoReloadContext context);
    string GetStatusInfo(AutoReloadContext context);
}

public abstract class ReloadStateBase : IReloadState
{
    public abstract IReloadState Execute(float frameTime, AutoReloadContext context);
    public virtual string GetStatusInfo(AutoReloadContext context) => GetType().Name;
}
