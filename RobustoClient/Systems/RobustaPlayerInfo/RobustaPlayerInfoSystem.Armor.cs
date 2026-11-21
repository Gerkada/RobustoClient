using System.Globalization;

namespace RobustoClient.Systems;

/// <summary>Armour formatting: the coefficients come from state, the grouping and text come from here.</summary>
public sealed partial class RobustaPlayerInfoSystem
{
    /// <summary>Upstream damage groups, in display order.</summary>
    private static readonly (string Group, string[] Types)[] DamageGroups =
    [
        ("BRUTE", ["Blunt", "Slash", "Piercing"]),
        ("BURN", ["Heat", "Shock", "Cold", "Caustic"]),
        ("TOXIN", ["Poison", "Radiation"]),
        ("AIRLOSS", ["Asphyxiation", "Bloodloss"]),
        ("GENETIC", ["Cellular"])
    ];

    /// <summary>
    /// Armor as one row per damage group, e.g. <c>BRUTE</c> → <c>Blunt 30%, Slash 30%</c>, from the
    /// combined coefficient per damage type.
    /// </summary>
    internal static List<InfoRow> CombineArmorGroups(IReadOnlyDictionary<string, float> coefficients)
    {
        var rows = new List<InfoRow>();
        var ordered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (group, types) in DamageGroups)
        {
            var parts = new List<string>();
            foreach (var type in types)
            {
                ordered.Add(type);
                AppendProtection(parts, coefficients, type);
            }

            // A group with nothing worth showing gets no row.
            if (parts.Count > 0)
                rows.Add(new InfoRow(group, string.Join(", ", parts)));
        }

        // A damage type no group names still counts.
        var ungrouped = new List<string>();
        foreach (var type in coefficients.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            if (!ordered.Contains(type))
                AppendProtection(ungrouped, coefficients, type);
        }

        if (ungrouped.Count > 0)
            rows.Add(new InfoRow("OTHER", string.Join(", ", ungrouped)));

        return rows;
    }

    /// <summary>Appends one type's protection when it actually reduces damage.</summary>
    private static void AppendProtection(
        List<string> parts,
        IReadOnlyDictionary<string, float> coefficients,
        string type)
    {
        if (!coefficients.TryGetValue(type, out var coefficient) || coefficient >= 1f)
            return;

        parts.Add($"{type} {((1f - coefficient) * 100f).ToString("0", CultureInfo.InvariantCulture)}%");
    }
}
