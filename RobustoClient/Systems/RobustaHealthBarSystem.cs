using System;
using System.Collections;
using System.Reflection;
using Content.Shared.Overlays;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems;

public class RobustaHealthBarSystem : LocalPlayerAddCompSystem<ShowHealthBarsComponent>
{
    private static readonly string[] DefaultContainers = ["Biological", "Silicon"];

    protected override ShowHealthBarsComponent CompOverride => CreateComponent();

    private static ShowHealthBarsComponent CreateComponent()
    {
        var comp = new ShowHealthBarsComponent();
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = typeof(ShowHealthBarsComponent).GetField("DamageContainers", flags);
            var prop = typeof(ShowHealthBarsComponent).GetProperty("DamageContainers", flags);
            var listObj = field?.GetValue(comp) ?? prop?.GetValue(comp);

            if (listObj is IList list)
            {
                list.Clear();
                var listType = listObj.GetType();
                var elemType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(string);

                foreach (var id in DefaultContainers)
                {
                    if (elemType == typeof(string))
                    {
                        list.Add(id);
                    }
                    else
                    {
                        var ctor = elemType.GetConstructor([typeof(string)]);
                        if (ctor != null)
                            list.Add(ctor.Invoke([id]));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"RobustaHealthBarSystem failed to populate DamageContainers: {ex.Message}");
        }

        return comp;
    }
}