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

// Мы больше не используем LocalPlayerAddCompSystem для Shared-компонента, 
// чтобы избежать конфликтов синхронизации (мерцания).
public sealed class RobustaChemicalScanSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;

    public override void Initialize()
    {
        base.Initialize();
        
        // 1. Разрешаем сканирование растворов на клиенте всегда
        SubscribeLocalEvent<SolutionScanEvent>(OnSolutionScan);
        
        // 2. Добавляем кнопку анализа (клиентский верб)
        SubscribeLocalEvent<GetVerbsEvent<ExamineVerb>>(OnGetExamineVerbs);

        // 3. ПЕРЕХВАТ СООБЩЕНИЙ СЕРВЕРА
        // Чтобы сервер не затирал наше окно осмотра "пустыми" данными
        SubscribeNetworkEvent<ExamineSystemMessages.ExamineInfoResponseMessage>(OnExamineInfoResponse);
    }

    private void OnSolutionScan(SolutionScanEvent args)
    {
        args.CanScan = true; // Чит: мы всегда можем сканировать
    }

    private void OnExamineInfoResponse(ExamineSystemMessages.ExamineInfoResponseMessage ev)
    {
        var uid = _entMan.GetEntity(ev.EntityUid);
        if (!_entMan.TryGetComponent<ExaminableSolutionComponent>(uid, out var component))
            return;

        // Если сервер прислал инфу об объекте с химией, принудительно вшиваем туда данные о растворе
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
            Text = "Хим-анализ (Robusta)",
            Category = VerbCategory.Examine,
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/drink.svg.192dpi.png")),
            ClientExclusive = true,
            Priority = 10
        };

        args.Verbs.Add(verb);
    }
}