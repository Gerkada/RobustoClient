using Content.Client.Chemistry.UI;
using HarmonyLib;
using Robust.Client.UserInterface.Controls;
using RobustoClient.UI;

namespace RobustoClient.Patches;

[HarmonyPatch(typeof(ReagentDispenserWindow))]
internal static class AutoChemUIPatch
{
    private static AutoChemWindow? _autoChemWindow;

    [HarmonyPatch(MethodType.Constructor)]
    [HarmonyPostfix]
    private static void ConstructorPostfix(ReagentDispenserWindow __instance)
    {
        var autoChemBtn = new Button
        {
            Text = "AutoChem",
            StyleClasses = { "ButtonColorGreen" }
        };

        autoChemBtn.OnPressed += _ =>
        {
            if (_autoChemWindow == null || _autoChemWindow.Disposed)
            {
                _autoChemWindow = new AutoChemWindow();
            }
            
            _autoChemWindow.OpenCentered();
        };

        __instance.ClearButton.Parent?.AddChild(autoChemBtn);
    }
}
