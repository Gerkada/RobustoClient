using System.Reflection;
using RobustoClient.Systems.Abstract;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Examine;
using Content.Shared.Verbs;
using Robust.Shared.Utility;
using Robust.Client.Player;
using Robust.Client.GameObjects;
using Content.Client.Examine;

namespace RobustoClient.Systems;

// We no longer use LocalPlayerAddCompSystem for the Shared-component
// to avoid synchronization conflicts (flickering).
public sealed class RobustaChemicalScanSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    public override void Initialize()
    {
        base.Initialize();
        
        // 1. Always allow solution scanning on client
        SubscribeLocalEvent<SolutionScanEvent>(OnSolutionScan);
        
        // 2. Add analysis button (client verb)
        SubscribeLocalEvent<GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);

        // 3. INTERCEPT SERVER MESSAGES
        // To prevent server from overwriting our examine window with "empty" data
        SubscribeNetworkEvent<ExamineSystemMessages.ExamineInfoResponseMessage>(OnExamineInfoResponse);
    }

    private void OnSolutionScan(SolutionScanEvent args)
    {
        args.CanScan = true; // Always allow scanning
    }

    private static object? GetMemberValue(object obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var prop = type.GetProperty(name, flags);
        if (prop != null) return prop.GetValue(obj);
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(obj);
        return null;
    }

    private void OnExamineInfoResponse(ExamineSystemMessages.ExamineInfoResponseMessage ev)
    {
        var uid = _entMan.GetEntity(ev.EntityUid);
        
        object? component = null;
        foreach (var comp in _entMan.GetComponents(uid))
        {
            if (comp.GetType().Name == "ExaminableSolutionComponent")
            {
                component = comp;
                break;
            }
        }
        if (component == null) return;

        var solutionName = GetMemberValue(component, "Solution") as string ?? "default";

        // If server sent info about an object with chemistry, force injection of solution data
        var solutionSystem = _entMan.System<SharedSolutionContainerSystem>();
        
        // Use reflection to call TryGetSolution to avoid TypeLoadException on SolutionManagerComponent
        var solutionManagerType = typeof(SharedSolutionContainerSystem).Assembly
            .GetType("Content.Shared.Chemistry.Components.SolutionManager.SolutionManagerComponent") 
            ?? typeof(SharedSolutionContainerSystem).Assembly
            .GetType("Content.Shared.Chemistry.Components.SolutionContainerManagerComponent");

        if (solutionManagerType == null) return;

        var entityType = typeof(Entity<>).MakeGenericType(solutionManagerType);
        var tryGetSolutionMethod = solutionSystem.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "TryGetSolution" && 
                                 m.GetParameters().Length == 5 &&
                                 m.GetParameters()[0].ParameterType.IsGenericType &&
                                 m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Entity<>));

        if (tryGetSolutionMethod == null) return;

        // Construct Entity<T> via reflection
        var entityInstance = Activator.CreateInstance(entityType, uid, null);
        var parameters = new object?[] { entityInstance, solutionName, null, null, false };
        var result = (bool)tryGetSolutionMethod.Invoke(solutionSystem, parameters)!;

        if (result && parameters[3] is Solution solution)
        {
            var method = solutionSystem.GetType().GetMethod("GetSolutionExamine", 
                BindingFlags.Instance | BindingFlags.NonPublic);
            
            if (method != null)
            {
                var chemMsg = (FormattedMessage)method.Invoke(solutionSystem, new object[] { solution })!;
                ev.Message.PushNewline();
                ev.Message.AddMessage(chemMsg);
            }
        }
    }

    private void OnGetExamineVerbs(GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess)
            return;

        object? component = null;
        foreach (var comp in _entMan.GetComponents(args.Target))
        {
            if (comp.GetType().Name == "ExaminableSolutionComponent")
            {
                component = comp;
                break;
            }
        }
        if (component == null) return;

        var solutionName = GetMemberValue(component, "Solution") as string ?? "default";

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null || args.User != localPlayer)
            return;

        var solutionSystem = _entMan.System<SharedSolutionContainerSystem>();
        
        // Use reflection to call TryGetSolution to avoid TypeLoadException
        var solutionManagerType = typeof(SharedSolutionContainerSystem).Assembly
            .GetType("Content.Shared.Chemistry.Components.SolutionManager.SolutionManagerComponent")
            ?? typeof(SharedSolutionContainerSystem).Assembly
            .GetType("Content.Shared.Chemistry.Components.SolutionContainerManagerComponent");

        if (solutionManagerType == null) return;

        var entityType = typeof(Entity<>).MakeGenericType(solutionManagerType);
        var tryGetSolutionMethod = solutionSystem.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "TryGetSolution" && 
                                 m.GetParameters().Length == 5 &&
                                 m.GetParameters()[0].ParameterType.IsGenericType &&
                                 m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Entity<>));

        if (tryGetSolutionMethod == null) return;

        var entityInstance = Activator.CreateInstance(entityType, args.Target, null);
        var parameters = new object?[] { entityInstance, solutionName, null, null, false };
        var result = (bool)tryGetSolutionMethod.Invoke(solutionSystem, parameters)!;

        if (!result || parameters[3] is not Solution solution)
            return;

        var verb = new ExamineVerb()
        {
            Act = () =>
            {
                var method = solutionSystem.GetType().GetMethod("GetSolutionExamine", 
                    BindingFlags.Instance | BindingFlags.NonPublic);
                
                if (method != null)
                {
                    var chemMsg = (FormattedMessage)method.Invoke(solutionSystem, new object[] { solution })!;
                    var finalMsg = new FormattedMessage();
                    finalMsg.AddText("[Robusta Analysis]");
                    finalMsg.PushNewline();
                    finalMsg.AddMessage(chemMsg);

                    _entMan.System<ExamineSystem>().SendExamineTooltip(args.User, args.Target, finalMsg, false, false);
                }
            },
            Text = "Chem-analysis (Robusta)",
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/drink.svg.192dpi.png")),
            ClientExclusive = true,
            Priority = 10
        };

        args.Verbs.Add(verb);
    }
}