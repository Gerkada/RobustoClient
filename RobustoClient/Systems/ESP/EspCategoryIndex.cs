using System;
using System.Collections.Generic;

namespace RobustoClient.Systems.ESP;

/// <summary>
/// Buckets ESP categories by a component the entity must have, so the per-entity loop runs fewer rules.
/// </summary>
internal static class EspCategoryIndex
{
    /// <summary>Component a category provably requires, or null when none can be proven.</summary>
    internal static string? GateComponent(
        EspCategoryDefinition category,
        IReadOnlyDictionary<string, string> ruleGates)
    {
        // hasComponent is a conjunction, so any one of its names is necessary for the category.
        foreach (var name in category.Conditions.HasComponent)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        // A job title and department come off the ID card, so an inventory is required.
        if (category.Conditions.JobContains.Count > 0)
            return "Inventory";

        foreach (var rule in category.Conditions.CustomRule)
        {
            if (ruleGates.TryGetValue(rule, out var gate))
                return gate;
        }

        return null;
    }

    /// <summary>
    /// Splits categories into component-keyed buckets plus the ungated remainder.
    /// </summary>
    internal static (Dictionary<string, List<EspCategoryDefinition>> ByComponent, List<EspCategoryDefinition> Ungated)
        Build(IEnumerable<EspCategoryDefinition> categories, IReadOnlyDictionary<string, string> ruleGates)
    {
        var byComponent = new Dictionary<string, List<EspCategoryDefinition>>(StringComparer.Ordinal);
        var ungated = new List<EspCategoryDefinition>();

        foreach (var category in categories)
        {
            var gate = GateComponent(category, ruleGates);
            if (gate == null)
            {
                ungated.Add(category);
                continue;
            }

            if (!byComponent.TryGetValue(gate, out var list))
                byComponent[gate] = list = new List<EspCategoryDefinition>();

            list.Add(category);
        }

        return (byComponent, ungated);
    }
}
