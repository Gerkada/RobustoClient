using Content.Shared.FixedPoint;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RobustoClient.Systems.AutoChem;

/// <summary>
/// Console command for launching the automated chemistry system from the game.
/// </summary>
public sealed class AutoChemCommand : IConsoleCommand
{
    // Command name for the console.
    public string Command => "autochem";
    public string Description => "Management of automated chemical brewing";
    public string Help => "Usage:\n" +
                          "  autochem <reagent_id> <amount> - Start brewing\n" +
                          "  autochem calculate <reagent>   - Calculate the ideal batch (up to 100u)\n" +
                          "  autochem search <query>        - Search for a reagent\n" +
                          "  autochem stop                  - Stop the current process";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var autoChem = entMan.System<RobustaAutoChemSystem>();

        if (args.Length == 1 && args[0] == "stop")
        {
            autoChem.StopJob();
            shell.WriteLine("[AutoChem] Process stopped.");
            return;
        }

        if (args.Length == 2 && args[0] == "calculate")
        {
            var calcReagent = args[1];
            float best = RobustaRecipeSolver.CalculateBestBatch(calcReagent, 100f);
            if (best <= 0)
            {
                shell.WriteError($"[AutoChem] Recipe '{calcReagent}' not found.");
                return;
            }
            shell.WriteLine($"[AutoChem] Best batch for '{calcReagent}' (no waste or overflow): {best}u");
            shell.WriteLine($"Use: autochem {calcReagent} {best}");
            return;
        }

        if (args.Length == 2 && args[0] == "search")
        {
            var results = RobustaChemDatabase.SearchReagents(args[1]);
            if (results.Count == 0) shell.WriteError("[AutoChem] Nothing found.");
            else shell.WriteLine($"[AutoChem] Search results: {string.Join(", ", results)}");
            return;
        }

        if (args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        var reagent = args[0];
        if (!int.TryParse(args[1], out var amountInt))
        {
            shell.WriteError("Amount must be a number!");
            return;
        }

        var plan = RobustaRecipeSolver.CreatePlan(reagent, amountInt);
        
        if (plan == null)
        {
            shell.WriteError($"[AutoChem] Recipe '{reagent}' not found!");
            var suggestions = RobustaChemDatabase.SearchReagents(reagent);
            if (suggestions.Count > 0)
            {
                shell.WriteLine($"Perhaps you meant: {string.Join(", ", suggestions)}");
            }
            return;
        }

        autoChem.StartJob(reagent, FixedPoint2.New(amountInt));
        shell.WriteLine($"[AutoChem] Plan accepted: {reagent} ({amountInt}u). Waiting for beaker...");
    }
}