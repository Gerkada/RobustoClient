using System.Collections.Generic;
using System.Reflection;
using Content.Client.Chemistry.UI;
using HarmonyLib;
using Robust.Client.UserInterface.Controls;
using RobustoClient.UI;

namespace RobustoClient.Patches;

[HarmonyPatch]
internal static class AutoChemUIPatch
{
    private static AutoChemWindow? _autoChemWindow;

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var stdCtor = AccessTools.Constructor(typeof(ReagentDispenserWindow));
        if (stdCtor != null)
            yield return stdCtor;

        var energyWindowType = AccessTools.TypeByName("Content.Goobstation.Client.Chemistry.UI.EnergyReagentDispenserWindow");
        var energyCtor = energyWindowType != null ? AccessTools.Constructor(energyWindowType) : null;
        if (energyCtor != null)
            yield return energyCtor;
    }

    [HarmonyPostfix]
    private static void ConstructorPostfix(object __instance)
    {
        var autoChemBtn = new Button
        {
            Text = "AutoChem"
        };
        autoChemBtn.AddStyleClass("ButtonColorGreen");

        autoChemBtn.OnPressed += _ =>
        {
            if (_autoChemWindow == null || _autoChemWindow.Disposed)
            {
                _autoChemWindow = new AutoChemWindow();
            }
            
            _autoChemWindow.OpenCentered();
        };

        var clearBtn = (__instance as ReagentDispenserWindow)?.ClearButton
            ?? __instance.GetType().GetProperty("ClearButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__instance) as Button;
        clearBtn?.Parent?.AddChild(autoChemBtn);
    }
}
