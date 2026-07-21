using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Chemistry.Reaction;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.AutoChem;

public static class RobustaChemDatabase
{
    // Dictionary: "Target reagent ID" -> "Reaction prototype that creates it"
    public static Dictionary<string, ReactionPrototype> RecipesByProduct = new(StringComparer.OrdinalIgnoreCase);
    
    public static bool IsInitialized = false;

    // This method should be called once (e.g., during client load or first bot activation)
    public static void Initialize()
    {
        if (IsInitialized) return;

        // Attempting to get the prototype manager directly via IoC
        var protoMan = IoCManager.Resolve<IPrototypeManager>();
        
        int recipeCount = 0;

        // Iterating through reaction prototypes
        foreach (var reaction in protoMan.EnumeratePrototypes<ReactionPrototype>())
        {
            // Checking if the reaction has products
            if (reaction.Products == null || reaction.Products.Count == 0)
                continue;

            int currentScore = CalculateRecipeScore(reaction);

            foreach (var product in reaction.Products.Keys)
            {
                // Cast ProtoId to string as the dictionary expects string keys
                string productId = (string)product;

                // Saving the "best" recipe for this product
                if (!RecipesByProduct.TryGetValue(productId, out var existing) || 
                    currentScore > CalculateRecipeScore(existing))
                {
                    RecipesByProduct[productId] = reaction;
                    recipeCount++;
                }
            }
        }

        IsInitialized = true;
        Logger.GetSawmill("autochem").Info($"Database initialized! Recipes loaded: {recipeCount}");
    }

    private static int CalculateRecipeScore(ReactionPrototype reaction)
    {
        // Base weight based on game priority
        int score = reaction.Priority * 100;

        // Large penalty for using biological fluids or "dirty" components
        var badReagents = new[] { "Blood", "Urine", "Vomit", "AmmoniaBlood", "SpaceCleaner", "Slime", "Facum" };
        
        foreach (var reactant in reaction.Reactants.Keys)
        {
            // Cast ProtoId to string for string comparisons
            string reactantId = (string)reactant;

            if (badReagents.Any(r => reactantId.Contains(r, StringComparison.OrdinalIgnoreCase)))
                score -= 2000; // Doubled the penalty
        }

        // Cast reaction.ID to string
        string reactionId = (string)reaction.ID;

        // Penalty for dirty reaction IDs
        if (reactionId.Contains("Blood") || reactionId.Contains("Urine") || reactionId.Contains("Vomit"))
            score -= 2000;

        // Heating penalty (prefer mixing over heating)
        if (reaction.MinimumTemperature > 295f)
            score -= 50;

        // Bonus for simple gases and base metals (common dispenser items)
        var commonReagents = new[] { "Hydrogen", "Nitrogen", "Oxygen", "Carbon", "Iron", "Iodine", "Phosphorus" };
        foreach (var reactant in reaction.Reactants.Keys)
        {
            // Cast ProtoId to string for string comparisons
            string reactantId = (string)reactant;

            if (commonReagents.Any(r => reactantId.Equals(r, StringComparison.OrdinalIgnoreCase)))
                score += 20;
        }

        return score;
    }

    /// <summary>
    /// Get the direct recipe for a chemical (what needs to be dispensed to create it)
    /// </summary>
    public static ReactionPrototype? GetRecipe(string targetReagentId)
    {
        if (!IsInitialized) Initialize();

        if (RecipesByProduct.TryGetValue(targetReagentId, out var recipe))
        {
            return recipe;
        }
        return null; // Recipe does not exist (base element like Carbon or Water)
    }

    /// <summary>
    /// Checks if heating is required for this recipe
    /// </summary>
    public static bool RequiresHeating(ReactionPrototype recipe)
    {
        // In SS14, base room temperature is ~293.15 Kelvin (20°C)
        // If the reaction requires more (e.g., 300+), a heater is needed
        return recipe.MinimumTemperature > 295f; 
    }

    /// <summary>
    /// Searches for similar reagent IDs in the loaded database
    /// </summary>
    public static List<string> SearchReagents(string query)
    {
        if (!IsInitialized) Initialize();
        
        return RecipesByProduct.Keys
            .Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(10) // Limit output to the first 10 matches to avoid spam
            .ToList();
    }
}