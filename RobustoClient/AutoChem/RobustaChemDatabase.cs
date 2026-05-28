using System.Collections.Generic;
using System.Linq;
using Content.Shared.Chemistry.Reaction;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.AutoChem;

public static class RobustaChemDatabase
{
    // Словарь: "ID конечного химиката" -> "Прототип реакции, которая его создает"
    public static Dictionary<string, ReactionPrototype> RecipesByProduct = new(StringComparer.OrdinalIgnoreCase);
    
    public static bool IsInitialized = false;

    // Этот метод нужно вызвать один раз (например, при загрузке клиента или первом включении бота)
    public static void Initialize()
    {
        if (IsInitialized) return;

        // Пытаемся получить менеджер прототипов напрямую через IoC
        var protoMan = IoCManager.Resolve<IPrototypeManager>();
        
        int recipeCount = 0;

        // Перебираем прототипы реакций
        foreach (var reaction in protoMan.EnumeratePrototypes<ReactionPrototype>())
        {
            // Проверяем, есть ли у реакции продукты
            if (reaction.Products == null || reaction.Products.Count == 0)
                continue;

            int currentScore = CalculateRecipeScore(reaction);

            foreach (var product in reaction.Products.Keys)
            {
                // Сохраняем самый "лучший" рецепт для этого продукта
                if (!RecipesByProduct.TryGetValue(product, out var existing) || 
                    currentScore > CalculateRecipeScore(existing))
                {
                    RecipesByProduct[product] = reaction;
                    recipeCount++;
                }
            }
        }

        IsInitialized = true;
        Logger.GetSawmill("autochem").Info($"База данных инициализирована! Загружено рецептов: {recipeCount}");
    }

    private static int CalculateRecipeScore(ReactionPrototype reaction)
    {
        // Базовый вес на основе приоритета игры
        int score = reaction.Priority * 100;

        // Огромный штраф за использование биологических жидкостей или "грязных" компонентов
        var badReagents = new[] { "Blood", "Urine", "Vomit", "AmmoniaBlood", "SpaceCleaner", "Slime", "Facum" };
        
        foreach (var reactant in reaction.Reactants.Keys)
        {
            if (badReagents.Any(r => reactant.Contains(r, StringComparison.OrdinalIgnoreCase)))
                score -= 2000; // Удвоили штраф
        }

        // Штраф за грязные ID самих реакций
        if (reaction.ID.Contains("Blood") || reaction.ID.Contains("Urine") || reaction.ID.Contains("Vomit"))
            score -= 2000;

        // Штраф за нагрев (лучше смешать просто так, чем греть)
        if (reaction.MinimumTemperature > 295f)
            score -= 50;

        // Бонус за простые газы и базовые металлы (то, что обычно есть в раздатчике)
        var commonReagents = new[] { "Hydrogen", "Nitrogen", "Oxygen", "Carbon", "Iron", "Iodine", "Phosphorus" };
        foreach (var reactant in reaction.Reactants.Keys)
        {
            if (commonReagents.Any(r => reactant.Equals(r, StringComparison.OrdinalIgnoreCase)))
                score += 20;
        }

        return score;
    }

    /// <summary>
    /// Получить прямой рецепт для химиката (что нужно залить, чтобы он получился)
    /// </summary>
    public static ReactionPrototype? GetRecipe(string targetReagentId)
    {
        if (!IsInitialized) Initialize();

        if (RecipesByProduct.TryGetValue(targetReagentId, out var recipe))
        {
            return recipe;
        }
        return null; // Рецепта не существует (это базовый элемент типа Углерода или Воды)
    }

    /// <summary>
    /// Проверяет, нужен ли нагрев для этого рецепта
    /// </summary>
    public static bool RequiresHeating(ReactionPrototype recipe)
    {
        // В SS14 базовая комнатная температура ~293.15 Кельвина (20°C)
        // Если реакция требует больше (например, 300+), значит нужен нагреватель
        return recipe.MinimumTemperature > 295f; 
    }

    /// <summary>
    /// Ищет похожие ID реагентов в загруженной базе
    /// </summary>
    public static List<string> SearchReagents(string query)
    {
        if (!IsInitialized) Initialize();
        
        return RecipesByProduct.Keys
            .Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(10) // Не спамим слишком сильно, берем первые 10
            .ToList();
    }
}