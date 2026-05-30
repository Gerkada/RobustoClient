namespace RobustoClient.Systems.AutoChem;

public interface IChemState
{
    IChemState Execute(float frameTime, AutoChemContext context);
    string GetStatusInfo(AutoChemContext context);
    float GetProgress(AutoChemContext context);
}

public abstract class ChemStateBase : IChemState
{
    public abstract IChemState Execute(float frameTime, AutoChemContext context);
    public abstract string GetStatusInfo(AutoChemContext context);

    public virtual float GetProgress(AutoChemContext context)
    {
        if (context.CurrentPlan == null || context.CurrentPlan.TotalPhases == 0) return 0f;
        int completed = context.CurrentPlan.TotalPhases - context.CurrentPlan.PhaseQueue.Count;
        return (float)completed / context.CurrentPlan.TotalPhases;
    }
}
