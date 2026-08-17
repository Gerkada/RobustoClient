using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Chemistry.Reaction;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.AutoChem;

public static class RobustaChemDatabase
{
    public static Dictionary<string, ReactionPrototype> RecipesByProduct = new(StringComparer.OrdinalIgnoreCase);
    
    public static bool IsInitialized = false;

    private static System.Reflection.FieldInfo? _productsField;
    private static System.Reflection.PropertyInfo? _productsProperty;
    private static System.Reflection.FieldInfo? _reactantsField;
    private static System.Reflection.PropertyInfo? _reactantsProperty;
    private static System.Reflection.FieldInfo? _priorityField;
    private static System.Reflection.PropertyInfo? _priorityProperty;
    private static System.Reflection.FieldInfo? _minTempField;
    private static System.Reflection.PropertyInfo? _minTempProperty;

    private static void InitReflection()
    {
        if (_productsField != null || _productsProperty != null) return;
        var type = typeof(ReactionPrototype);
        
        _productsField = type.GetField("Products");
        _productsProperty = type.GetProperty("Products");
        
        _reactantsField = type.GetField("Reactants");
        _reactantsProperty = type.GetProperty("Reactants");
        
        _priorityField = type.GetField("Priority");
        _priorityProperty = type.GetProperty("Priority");
        
        _minTempField = type.GetField("MinimumTemperature");
        _minTempProperty = type.GetProperty("MinimumTemperature");
    }

    public static IEnumerable<string> GetProductIds(ReactionPrototype reaction)
    {
        object? obj = _productsField?.GetValue(reaction) ?? _productsProperty?.GetValue(reaction);
        if (obj is System.Collections.IDictionary dict)
            foreach (var key in dict.Keys) yield return key.ToString() ?? string.Empty;
    }

    public static IEnumerable<string> GetReactantIds(ReactionPrototype reaction)
    {
        object? obj = _reactantsField?.GetValue(reaction) ?? _reactantsProperty?.GetValue(reaction);
        if (obj is System.Collections.IDictionary dict)
            foreach (var key in dict.Keys) yield return key.ToString() ?? string.Empty;
    }

    public static IEnumerable<(string Id, string Amount)> GetReactantsAndAmounts(ReactionPrototype reaction)
    {
        foreach (var r in GetReactants(reaction))
            yield return (r.Id, r.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static IEnumerable<(string Id, float Amount, bool Catalyst)> GetReactants(ReactionPrototype reaction)
    {
        object? obj = _reactantsField?.GetValue(reaction) ?? _reactantsProperty?.GetValue(reaction);
        if (obj is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                string id = entry.Key?.ToString() ?? "Unknown";
                float amount = 1f;
                bool catalyst = false;
                
                if (entry.Value != null)
                {
                    var valType = entry.Value.GetType();
                    var amtObj = valType.GetField("Amount")?.GetValue(entry.Value) ?? valType.GetProperty("Amount")?.GetValue(entry.Value);
                    if (amtObj != null)
                    {
                        var floatMethod = amtObj.GetType().GetMethod("Float", Type.EmptyTypes);
                        if (floatMethod != null) amount = (float)(floatMethod.Invoke(amtObj, null) ?? 1f);
                    }
                    var catObj = valType.GetField("Catalyst")?.GetValue(entry.Value) ?? valType.GetProperty("Catalyst")?.GetValue(entry.Value);
                    if (catObj is bool b) catalyst = b;
                }
                yield return (id, amount, catalyst);
            }
        }
    }

    public static float GetProductYield(ReactionPrototype reaction, string targetProductId)
    {
        object? obj = _productsField?.GetValue(reaction) ?? _productsProperty?.GetValue(reaction);
        if (obj is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                if (entry.Key?.ToString()?.Equals(targetProductId, StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (entry.Value != null)
                    {
                        var floatMethod = entry.Value.GetType().GetMethod("Float", Type.EmptyTypes);
                        if (floatMethod != null) return (float)(floatMethod.Invoke(entry.Value, null) ?? 1f);
                    }
                }
            }
        }
        return 1f;
    }

    private static int SafeGetPriority(ReactionPrototype reaction)
    {
        object? val = _priorityField?.GetValue(reaction) ?? _priorityProperty?.GetValue(reaction);
        return val is int i ? i : 0;
    }

    private static float SafeGetMinTemp(ReactionPrototype reaction)
    {
        object? val = _minTempField?.GetValue(reaction) ?? _minTempProperty?.GetValue(reaction);
        return val is float f ? f : 0f;
    }

    // This method should be called once (e.g., during client load or first bot activation)
    public static void Initialize()
    {
        if (IsInitialized) return;

        InitReflection();

        // Attempting to get the prototype manager directly via IoC
        var protoMan = IoCManager.Resolve<IPrototypeManager>();
        
        int recipeCount = 0;

        // Iterating through reaction prototypes
        foreach (var reaction in protoMan.EnumeratePrototypes<ReactionPrototype>())
        {
            var productIds = GetProductIds(reaction).ToList();
            if (productIds.Count == 0)
                continue;

            int currentScore = CalculateRecipeScore(reaction);

            foreach (var productId in productIds)
            {
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
        int score = SafeGetPriority(reaction) * 100;

        // Large penalty for using biological fluids or "dirty" components
        var badReagents = new[] { "Blood", "Urine", "Vomit", "AmmoniaBlood", "SpaceCleaner", "Slime", "Facum" };
        
        foreach (var reactantId in GetReactantIds(reaction))
        {
            if (badReagents.Any(r => reactantId.Contains(r, StringComparison.OrdinalIgnoreCase)))
                score -= 2000; // Doubled the penalty
        }

        // Cast reaction.ID to string
        string reactionId = (string)reaction.ID;

        // Penalty for dirty reaction IDs
        if (reactionId.Contains("Blood") || reactionId.Contains("Urine") || reactionId.Contains("Vomit"))
            score -= 2000;

        // Heating penalty (prefer mixing over heating)
        if (SafeGetMinTemp(reaction) > 295f)
            score -= 50;

        // Bonus for simple gases and base metals (common dispenser items)
        var commonReagents = new[] { "Hydrogen", "Nitrogen", "Oxygen", "Carbon", "Iron", "Iodine", "Phosphorus" };
        foreach (var reactantId in GetReactantIds(reaction))
        {
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
        return SafeGetMinTemp(recipe) > 295f; 
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