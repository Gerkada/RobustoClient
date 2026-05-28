using System.Reflection;
using RobustoClient.Systems.Abstract;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
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

    private void OnExamineInfoResponse(ExamineSystemMessages.ExamineInfoResponseMessage ev)
    {
        var uid = _entMan.GetEntity(ev.EntityUid);
        if (!_entMan.TryGetComponent<ExaminableSolutionComponent>(uid, out var component))
            return;

        // If server sent info about an object with chemistry, force injection of solution data
        var solutionSystem = _entMan.System<SharedSolutionContainerSystem>();
        if (solutionSystem.TryGetSolution(uid, component.Solution, out _, out var solution))
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

        if (!_entMan.TryGetComponent<ExaminableSolutionComponent>(args.Target, out var component))
            return;

        var localPlayer = _player.LocalSession?.AttachedEntity;
        if (localPlayer == null || args.User != localPlayer)
            return;

        var solutionSystem = _entMan.System<SharedSolutionContainerSystem>();
        if (!solutionSystem.TryGetSolution(args.Target, component.Solution, out _, out var solution))
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