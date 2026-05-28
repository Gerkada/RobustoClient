using Content.Shared.FixedPoint;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;

namespace RobustoClient.Systems.AutoChem;

/// <summary>
/// Консольная команда для запуска нашего авто-химика прямо из игры
/// </summary>
public sealed class AutoChemCommand : IConsoleCommand
{
    // Название команды, которую ты будешь писать в консоль
    public string Command => "autochem";
    public string Description => "Управление автоматической варкой химикатов";
    public string Help => "Использование:\n" +
                          "  autochem <reagent_id> <amount> - Начать варку\n" +
                          "  autochem calculate <reagent>   - Расчитать идеальную порцию (до 100u)\n" +
                          "  autochem search <query>        - Поиск реагента\n" +
                          "  autochem stop                  - Остановить текущий процесс";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var entMan = IoCManager.Resolve<IEntityManager>();
        var autoChem = entMan.System<RobustaAutoChemSystem>();

        if (args.Length == 1 && args[0] == "stop")
        {
            autoChem.StopJob();
            shell.WriteLine("[AutoChem] Процесс остановлен.");
            return;
        }

        if (args.Length == 2 && args[0] == "calculate")
        {
            var calcReagent = args[1];
            float best = RobustaRecipeSolver.CalculateBestBatch(calcReagent, 100f);
            if (best <= 0)
            {
                shell.WriteError($"[AutoChem] Рецепт '{calcReagent}' не найден.");
                return;
            }
            shell.WriteLine($"[AutoChem] Лучшая порция для '{calcReagent}' (без мусора и перелива): {best}u");
            shell.WriteLine($"Используйте: autochem {calcReagent} {best}");
            return;
        }

        if (args.Length == 2 && args[0] == "search")
        {
            var results = RobustaChemDatabase.SearchReagents(args[1]);
            if (results.Count == 0) shell.WriteError("[AutoChem] Ничего не найдено.");
            else shell.WriteLine($"[AutoChem] Результаты поиска: {string.Join(", ", results)}");
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
            shell.WriteError("Количество должно быть числом!");
            return;
        }

        var plan = RobustaRecipeSolver.CreatePlan(reagent, amountInt);
        
        if (plan == null)
        {
            shell.WriteError($"[AutoChem] Рецепт '{reagent}' не найден!");
            var suggestions = RobustaChemDatabase.SearchReagents(reagent);
            if (suggestions.Count > 0)
            {
                shell.WriteLine($"Возможно, вы имели в виду: {string.Join(", ", suggestions)}");
            }
            return;
        }

        autoChem.StartJob(reagent, FixedPoint2.New(amountInt));
        shell.WriteLine($"[AutoChem] План принят: {reagent} ({amountInt}u). Ожидаю стакан...");
    }
}