using System.Reflection;
using HarmonyLib;
using Robust.Client.Input;
using RobustoClient.ImGui;

namespace RobustoClient.Patches;

[HarmonyPatch]
internal static class ClydeImGuiInputPatch
{
    private static readonly Type? ClydeType = AccessTools.TypeByName("Robust.Client.Graphics.Clyde.Clyde");

    private static readonly Func<object, KeyEventArgs>? GetKeyDownArgs =
        CreateArgsGetter<KeyEventArgs>("DEventKeyDown");

    private static readonly Func<object, KeyEventArgs>? GetKeyUpArgs =
        CreateArgsGetter<KeyEventArgs>("DEventKeyUp");

    private static readonly Func<object, MouseWheelEventArgs>? GetScrollArgs =
        CreateArgsGetter<MouseWheelEventArgs>("DEventScroll");

    private static readonly Func<object, MouseMoveEventArgs>? GetMouseMoveArgs =
        CreateArgsGetter<MouseMoveEventArgs>("DEventMouseMove");

    private static readonly Func<object, TextEnteredEventArgs>? GetTextArgs =
        CreateArgsGetter<TextEnteredEventArgs>("DEventText");

    private static readonly Type? KeyDownEventType = ClydeType?.GetNestedType("DEventKeyDown", BindingFlags.NonPublic);
    private static readonly Type? KeyUpEventType = ClydeType?.GetNestedType("DEventKeyUp", BindingFlags.NonPublic);
    private static readonly Type? ScrollEventType = ClydeType?.GetNestedType("DEventScroll", BindingFlags.NonPublic);
    private static readonly Type? MouseMoveEventType = ClydeType?.GetNestedType("DEventMouseMove", BindingFlags.NonPublic);
    private static readonly Type? TextEventType = ClydeType?.GetNestedType("DEventText", BindingFlags.NonPublic);

    // Patch DispatchSingleEvent (770B IL) instead of the 20B Send* helpers, which RyuJIT inlines into Sdl3WindowingImpl.
    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() =>
        ClydeType != null ? AccessTools.Method(ClydeType, "DispatchSingleEvent") : null;

    [HarmonyPrefix]
    internal static bool DispatchSingleEventPrefix(object ev)
    {
        if (ev == null)
            return true;

        var evType = ev.GetType();

        if (evType == MouseMoveEventType && GetMouseMoveArgs != null)
        {
            ImGuiInputBridge.OnMouseMove(GetMouseMoveArgs(ev));
            return true;
        }

        if (evType == KeyDownEventType && GetKeyDownArgs != null)
            return !ImGuiInputBridge.OnKeyDown(GetKeyDownArgs(ev));

        if (evType == KeyUpEventType && GetKeyUpArgs != null)
            return !ImGuiInputBridge.OnKeyUp(GetKeyUpArgs(ev));

        if (evType == ScrollEventType && GetScrollArgs != null)
            return !ImGuiInputBridge.OnMouseWheel(GetScrollArgs(ev));

        if (evType == TextEventType && GetTextArgs != null)
            return !ImGuiInputBridge.OnTextEntered(GetTextArgs(ev));

        return true;
    }

    private static Func<object, TArgs>? CreateArgsGetter<TArgs>(string nestedRecordName) where TArgs : class
    {
        var recordType = ClydeType?.GetNestedType(nestedRecordName, BindingFlags.NonPublic);
        var prop = recordType?.GetProperty("Args", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop?.GetMethod == null)
            return null;

        return obj => (TArgs)prop.GetValue(obj)!;
    }
}
