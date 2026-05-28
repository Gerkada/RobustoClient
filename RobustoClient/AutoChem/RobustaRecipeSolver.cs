using System;
using System.Linq;
using System.Collections.Generic;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.FixedPoint;
using Robust.Shared.Log;

namespace RobustoClient.Systems.AutoChem;

public enum PhaseType
{
    Dispense,
    Heat,
    Wait
}

public class ChemPhase
{
    public PhaseType Type { get; set; }
    public Dictionary<string, FixedPoint2> Ingredients { get; set; } = new();
    public float TargetTemperature { get; set; }
    public List<string> WaitReagents { get; set; } = new(); 
    public string Description { get; set; } = string.Empty;
    public string? TargetProduct { get; set; }
}

public class DispensePlan
{
    public string TargetReagent { get; set; } = string.Empty;
    public FixedPoint2 TargetAmount { get; set; }
    public List<ChemPhase> RawPhases { get; set; } = new();
    public Queue<ChemPhase> PhaseQueue { get; set; } = new();
    public int TotalPhases { get; set; } = 0;

    public string GetPlanSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- ТЕХНОЛОГИЧЕСКАЯ КАРТА: {TargetReagent} ({TargetAmount}u) ---");
        int i = 1;
        foreach (var phase in RawPhases)
        {
            if (phase.Type == PhaseType.Dispense && phase.Ingredients.Count == 0) continue;
            
            sb.Append($"  [Этап {i++}] {phase.Description}");
            if (phase.Type == PhaseType.Dispense)
            {
                sb.Append(": ");
                var parts = new List<string>();
                if (phase.Ingredients.Count > 0)
                {
                    parts.Add(string.Join(", ", phase.Ingredients.Select(kv => $"{kv.Key} ({kv.Value}u)")));
                }
                sb.Append(string.Join(" + ", parts));
            }
            else if (phase.Type == PhaseType.Heat)
            {
                sb.Append($": {phase.TargetTemperature}K");
            }
            sb.AppendLine();
        }
        sb.AppendLine("---------------------------------------------------------");
        return sb.ToString();
    }
}

public static class RobustaRecipeSolver
{
    public static readonly HashSet<string> DispenserReagents = new(StringComparer.OrdinalIgnoreCase)
    {
        "aluminium", "carbon", "chlorine", "copper", "ethanol", "fluorine",
        "hydrogen", "iodine", "iron", "lithium", "mercury", "nitrogen",
        "oxygen", "phosphorus", "potassium", "radium", "silicon", "sodium",
        "sugar", "sulfur", "plasma", "weldingfuel"
    };

    private static long GCD(long a, long b) => b == 0 ? a : GCD(b, a % b);
    private static long LCM(long a, long b) => (a == 0 || b == 0) ? Math.Max(a, b) : (Abs(a * b) / GCD(a, b));
    private static long Abs(long v) => v < 0 ? -v : v;

    public static DispensePlan? CreatePlan(string targetReagent, FixedPoint2 amount, float capacity = 50f)
    {
        var plan = new DispensePlan { TargetReagent = targetReagent, TargetAmount = amount };
        var sawmill = Logger.GetSawmill("autochem");

        float globalLCM = CalculateRecursiveLCM(targetReagent);
        
        var reaction = RobustaChemDatabase.GetRecipe(targetReagent);
        float yieldPerCycle = (reaction != null && reaction.Products.TryGetValue(targetReagent, out var yr)) ? yr.Float() : 1f;

        float safeAmount = capacity * 0.95f;
        float targetAmount = amount.Float();
        if (targetAmount > safeAmount) targetAmount = safeAmount;

        float rawNeededCycles = targetAmount / yieldPerCycle;
        int batchCount = (int)Math.Ceiling(rawNeededCycles / globalLCM);
        if (batchCount < 1) batchCount = 1;

        float totalTargetCycles = globalLCM * batchCount;
        
        sawmill.Info($"[PLAN] Target: {targetReagent}, Amount: {targetAmount}, Yield/Cycle: {yieldPerCycle}, GlobalLCM: {globalLCM}, Cycles: {totalTargetCycles}");

        if (!ResolveRecursive(targetReagent, totalTargetCycles, plan))
            return null;

        var finalPhases = plan.RawPhases.Where(p => 
            (p.Type == PhaseType.Dispense && p.Ingredients.Count > 0) || 
            (p.Type == PhaseType.Heat) ||
            (p.Type == PhaseType.Wait)).ToList();

        plan.PhaseQueue = new Queue<ChemPhase>(finalPhases);
        plan.TotalPhases = finalPhases.Count;
        return plan;
    }

    public static float CalculateBestBatch(string reagentId, float capacity = 100f)
    {
        if (DispenserReagents.Contains(reagentId)) return capacity;

        var recipe = RobustaChemDatabase.GetRecipe(reagentId);
        if (recipe == null) return 0f;

        float globalLCM = CalculateRecursiveLCM(reagentId);
        float yieldPerCycle = recipe.Products.TryGetValue(reagentId, out var yAmt) ? yAmt.Float() : 1f;
        
        float consumingVolumePerCycle = 0f;
        float catalystVolume = 0f;

        foreach (var reactant in recipe.Reactants.Values)
        {
            if (reactant.Catalyst)
                catalystVolume += reactant.Amount.Float();
            else
                consumingVolumePerCycle += reactant.Amount.Float();
        }

        if (consumingVolumePerCycle <= 0) return capacity;

        float availableVolume = capacity - catalystVolume;
        float maxCycles = (float)Math.Floor(availableVolume / consumingVolumePerCycle);
        
        int batchCount = (int)Math.Floor(maxCycles / globalLCM);
        if (batchCount < 1) batchCount = 1;

        float bestCycles = batchCount * globalLCM;
        
        if ((bestCycles * consumingVolumePerCycle) + catalystVolume > capacity)
        {
            bestCycles = (float)Math.Floor(availableVolume / consumingVolumePerCycle);
        }

        return (float)Math.Round(bestCycles * yieldPerCycle, 2);
    }

    private static float CalculateRecursiveLCM(string reagentId)
    {
        if (DispenserReagents.Contains(reagentId)) return 1f;
        
        var recipe = RobustaChemDatabase.GetRecipe(reagentId);
        if (recipe == null) return 1f;

        float yield = recipe.Products.TryGetValue(reagentId, out var yAmt) ? yAmt.Float() : 1f;
        long currentLCM = 1;

        if (Math.Abs(yield % 1.0f) > 0.001f) currentLCM = LCM(currentLCM, 10);

        foreach (var (rId, reactant) in recipe.Reactants)
        {
            if (reactant.Catalyst) continue;

            if (Math.Abs(reactant.Amount.Float() % 1.0f) > 0.001f)
                currentLCM = LCM(currentLCM, 10);

            if (!DispenserReagents.Contains(rId))
            {
                float preLCM = CalculateRecursiveLCM(rId);
                currentLCM = LCM(currentLCM, (long)Math.Ceiling(preLCM));
            }
        }

        return (float)currentLCM;
    }

    private static bool ResolveRecursive(string reagentId, float cycles, DispensePlan plan)
    {
        cycles = (float)Math.Ceiling(cycles);

        if (DispenserReagents.Contains(reagentId))
        {
            var phase = new ChemPhase { Type = PhaseType.Dispense, Description = $"Add {reagentId}" };
            phase.Ingredients[reagentId] = FixedPoint2.New(cycles);
            plan.RawPhases.Add(phase);
            return true;
        }

        var recipe = RobustaChemDatabase.GetRecipe(reagentId);
        if (recipe == null) return true;

        // 1. Сначала обеспечиваем все под-реакции
        foreach (var (reactantId, reactantData) in recipe.Reactants)
        {
            if (reactantData.Catalyst) continue;

            if (!DispenserReagents.Contains(reactantId))
            {
                var preRecipe = RobustaChemDatabase.GetRecipe(reactantId);
                float preYield = (preRecipe != null && preRecipe.Products.TryGetValue(reactantId, out var yr)) ? yr.Float() : 1f;
                float neededPreAmount = cycles * reactantData.Amount.Float();
                float neededPreCycles = neededPreAmount / preYield;
                if (!ResolveRecursive(reactantId, neededPreCycles, plan)) return false;
            }
        }

        // 2. Группируем ингредиенты из раздатчика
        var synthesisPhase = new ChemPhase 
        { 
            Type = PhaseType.Dispense, 
            Description = $"Synthesis: {reagentId}",
            TargetProduct = reagentId
        };

        foreach (var (reactantId, reactantData) in recipe.Reactants)
        {
            if (DispenserReagents.Contains(reactantId) || reactantData.Catalyst)
            {
                float amount = reactantData.Amount.Float();
                if (!reactantData.Catalyst) amount *= cycles;
                
                synthesisPhase.Ingredients[reactantId] = FixedPoint2.New(amount);
            }
        }
        
        if (synthesisPhase.Ingredients.Count > 0)
            plan.RawPhases.Add(synthesisPhase);

        if (RobustaChemDatabase.RequiresHeating(recipe))
        {
            plan.RawPhases.Add(new ChemPhase {
                Type = PhaseType.Heat,
                TargetTemperature = recipe.MinimumTemperature,
                Description = $"Heat for {reagentId}",
                TargetProduct = reagentId
            });
        }

        // 3. ДОБАВЛЯЕМ ОТДЕЛЬНУЮ ФАЗУ ОЖИДАНИЯ
        plan.RawPhases.Add(new ChemPhase {
            Type = PhaseType.Wait,
            WaitReagents = new List<string> { reagentId },
            Description = $"Wait for {reagentId}",
            TargetProduct = reagentId
        });

        return true;
    }
}