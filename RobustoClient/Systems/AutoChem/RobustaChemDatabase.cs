using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Chemistry.Reaction;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.AutoChem;

public sealed record ChemReactant(string Id, float Amount, bool Catalyst = false);

public sealed record ChemRecipeDefinition(
    string Id,
    IReadOnlyDictionary<string, float> Products,
    IReadOnlyList<ChemReactant> Reactants,
    int Priority = 0,
    float MinimumTemperature = 0f);

public static class RobustaChemDatabase
{
    public static Dictionary<string, ChemRecipeDefinition> RecipesByProduct = new(StringComparer.OrdinalIgnoreCase);
    
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

    private static Dictionary<string, float> ReflectProducts(ReactionPrototype reaction)
    {
        var products = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        object? obj = _productsField?.GetValue(reaction) ?? _productsProperty?.GetValue(reaction);
        if (obj is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                string id = entry.Key?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(id)) continue;

                float yield = 1f;
                if (entry.Value != null)
                {
                    var floatMethod = entry.Value.GetType().GetMethod("Float", Type.EmptyTypes);
                    if (floatMethod != null) yield = (float)(floatMethod.Invoke(entry.Value, null) ?? 1f);
                }
                products[id] = yield;
            }
        }
        return products;
    }

    private static List<ChemReactant> ReflectReactants(ReactionPrototype reaction)
    {
        var reactants = new List<ChemReactant>();
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
                reactants.Add(new ChemReactant(id, amount, catalyst));
            }
        }
        return reactants;
    }

    public static IEnumerable<(string Id, string Amount)> GetReactantsAndAmounts(ChemRecipeDefinition recipe)
    {
        foreach (var r in recipe.Reactants)
            yield return (r.Id, r.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static IEnumerable<ChemReactant> GetReactants(ChemRecipeDefinition recipe) => recipe.Reactants;

    public static float GetProductYield(ChemRecipeDefinition recipe, string targetProductId)
    {
        foreach (var (id, yield) in recipe.Products)
        {
            if (id.Equals(targetProductId, StringComparison.OrdinalIgnoreCase))
                return yield;
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

    public static int LoadRecipes(IEnumerable<ChemRecipeDefinition> recipes)
    {
        RecipesByProduct.Clear();
        int recipeCount = 0;

        foreach (var recipe in recipes)
        {
            if (recipe.Products.Count == 0)
                continue;

            int currentScore = CalculateRecipeScore(recipe);

            foreach (var productId in recipe.Products.Keys)
            {
                if (!RecipesByProduct.TryGetValue(productId, out var existing) || 
                    currentScore > CalculateRecipeScore(existing))
                {
                    RecipesByProduct[productId] = recipe;
                    recipeCount++;
                }
            }
        }

        IsInitialized = true;
        return recipeCount;
    }

    // This method should be called once (e.g., during client load or first bot activation)
    public static void Initialize()
    {
        if (IsInitialized) return;

        InitReflection();

        var protoMan = IoCManager.Resolve<IPrototypeManager>();
        var definitions = protoMan.EnumeratePrototypes<ReactionPrototype>()
            .Select(r => new ChemRecipeDefinition(
                (string)r.ID,
                ReflectProducts(r),
                ReflectReactants(r),
                SafeGetPriority(r),
                SafeGetMinTemp(r)));

        int recipeCount = LoadRecipes(definitions);
        if (IoCManager.Instance != null && IoCManager.Instance.TryResolveType<ILogManager>(out var logMan))
            logMan.GetSawmill("autochem").Info($"Database initialized! Recipes loaded: {recipeCount}");
    }

    private static int CalculateRecipeScore(ChemRecipeDefinition recipe)
    {
        // Base weight based on game priority
        int score = recipe.Priority * 100;

        // Large penalty for using biological fluids or "dirty" components
        var badReagents = new[] { "Blood", "Urine", "Vomit", "AmmoniaBlood", "SpaceCleaner", "Slime", "Facum" };
        
        foreach (var reactant in recipe.Reactants)
        {
            if (badReagents.Any(r => reactant.Id.Contains(r, StringComparison.OrdinalIgnoreCase)))
                score -= 2000; // Doubled the penalty
        }

        // Penalty for dirty reaction IDs
        if (recipe.Id.Contains("Blood") || recipe.Id.Contains("Urine") || recipe.Id.Contains("Vomit"))
            score -= 2000;

        // Heating penalty (prefer mixing over heating)
        if (recipe.MinimumTemperature > 295f)
            score -= 50;

        // Bonus for simple gases and base metals (common dispenser items)
        var commonReagents = new[] { "Hydrogen", "Nitrogen", "Oxygen", "Carbon", "Iron", "Iodine", "Phosphorus" };
        foreach (var reactant in recipe.Reactants)
        {
            if (commonReagents.Any(r => reactant.Id.Equals(r, StringComparison.OrdinalIgnoreCase)))
                score += 20;
        }

        return score;
    }

    /// <summary>
    /// Get the direct recipe for a chemical (what needs to be dispensed to create it)
    /// </summary>
    public static ChemRecipeDefinition? GetRecipe(string targetReagentId)
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
    public static bool RequiresHeating(ChemRecipeDefinition recipe)
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