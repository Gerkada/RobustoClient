using HarmonyLib;

// ReSharper disable once CheckNamespace
// ReSharper disable once UnusedType.Global
public static class SubverterPatch
{
    public static string Name = "RobustoClient";
    public static string Description = "JUST DRINK ROBUSTA";
    public static Harmony Harm = new("com.noverd.robusta");
}