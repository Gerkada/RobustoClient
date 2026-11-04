using System.Reflection;
using HarmonyLib;
using Robust.Client.Input;
using RobustoClient.ImGui;

namespace RobustoClient.Patches;

internal static class ClydeImGuiInputPatch
{
    private static readonly Type? ClydeType = AccessTools.TypeByName("Robust.Client.Graphics.Clyde.Clyde");

    [HarmonyPatch]
    private static class SendKeyDownPatch
    {
        private static MethodBase? TargetMethod() =>
            ClydeType != null ? AccessTools.Method(ClydeType, "SendKeyDown", [typeof(KeyEventArgs)]) : null;

        [HarmonyPrefix]
        private static bool Prefix(KeyEventArgs ev) => !ImGuiInputBridge.OnKeyDown(ev);
    }

    [HarmonyPatch]
    private static class SendKeyUpPatch
    {
        private static MethodBase? TargetMethod() =>
            ClydeType != null ? AccessTools.Method(ClydeType, "SendKeyUp", [typeof(KeyEventArgs)]) : null;

        [HarmonyPrefix]
        private static bool Prefix(KeyEventArgs ev) => !ImGuiInputBridge.OnKeyUp(ev);
    }

    [HarmonyPatch]
    private static class SendScrollPatch
    {
        private static MethodBase? TargetMethod() =>
            ClydeType != null ? AccessTools.Method(ClydeType, "SendScroll", [typeof(MouseWheelEventArgs)]) : null;

        [HarmonyPrefix]
        private static bool Prefix(MouseWheelEventArgs ev) => !ImGuiInputBridge.OnMouseWheel(ev);
    }

    [HarmonyPatch]
    private static class SendMouseMovePatch
    {
        private static MethodBase? TargetMethod() =>
            ClydeType != null ? AccessTools.Method(ClydeType, "SendMouseMove", [typeof(MouseMoveEventArgs)]) : null;

        [HarmonyPrefix]
        private static void Prefix(MouseMoveEventArgs ev) => ImGuiInputBridge.OnMouseMove(ev);
    }

    [HarmonyPatch]
    private static class SendTextPatch
    {
        private static MethodBase? TargetMethod() =>
            ClydeType != null ? AccessTools.Method(ClydeType, "SendText", [typeof(TextEnteredEventArgs)]) : null;

        [HarmonyPrefix]
        private static bool Prefix(TextEnteredEventArgs ev) => !ImGuiInputBridge.OnTextEntered(ev);
    }
}
