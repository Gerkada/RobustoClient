using System;
using System.Collections.Generic;

namespace RobustoClient.Systems.Macros;

public sealed partial class RobustaMacroSystem
{
    private static MacroDefinition[]? _all;

    /// <summary>
    /// Every selectable macro, in picker order. Built lazily rather than as a static field
    /// initialiser: initialisers across partials run in an order the language does not guarantee,
    /// so a field here could be built before <c>_inventoryMacros</c> exists and come out empty.
    /// </summary>
    internal static IReadOnlyList<MacroDefinition> Macros => _all ??= [.._inventoryMacros, .._securityMacros];

    /// <summary>Looks a macro up by the id stored in a bind or a radial sector.</summary>
    internal static bool TryGetMacro(string? id, out MacroDefinition macro)
    {
        if (!string.IsNullOrEmpty(id))
        {
            foreach (var candidate in Macros)
            {
                if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
                {
                    macro = candidate;
                    return true;
                }
            }
        }

        macro = null!;
        return false;
    }
}
