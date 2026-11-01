using Content.Shared.Construction.Prototypes;
using Robust.Shared.Map;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaAutoConstructSystem
{
    public void SetHudBanner(string header, string detail, bool isError, float duration = 5f)
    {
        _hudBannerHeader = header;
        _hudBannerDetail = detail;
        _hudBannerIsError = isError;
        _hudBannerTimer = duration;
    }

    private string GetEntityDisplayName(EntityUid uid)
    {
        if (!Exists(uid))
            return "target";

        if (HasGhostComponent(uid))
        {
            var ghostComp = FindComponentByName(uid, "ConstructionGhostComponent");
            var proto = GetMemberValue(ghostComp, "Prototype") as ConstructionPrototype;
            var protoId = proto?.ID ?? GetStringMember(ghostComp, "Prototype");
            if (!string.IsNullOrEmpty(protoId))
                return $"ghost:{protoId}";
        }

        var meta = MetaData(uid);
        var name = meta.EntityName;
        var pId = meta.EntityPrototype?.ID;
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(pId) && !string.Equals(name, pId, StringComparison.OrdinalIgnoreCase))
            return $"{name} ({pId})";

        return !string.IsNullOrEmpty(pId) ? pId : (!string.IsNullOrEmpty(name) ? name : uid.ToString());
    }

    private static string FormatStepRequirement(ConstructionActionStep step)
    {
        return step.Kind switch
        {
            ConstructionActionKind.AddMaterial => $"material '{step.Amount}x {step.PrototypeOrQualityOrTag}'",
            ConstructionActionKind.UseTool => $"tool with '{step.PrototypeOrQualityOrTag}' quality",
            ConstructionActionKind.CutWires => "Wirecutters / Multitool",
            ConstructionActionKind.InsertPrototype => $"item '{step.Amount}x {step.PrototypeOrQualityOrTag}'",
            ConstructionActionKind.InsertComponent => $"component item '{step.PrototypeOrQualityOrTag}'",
            ConstructionActionKind.InsertTag => $"tagged item '[{step.PrototypeOrQualityOrTag}]'",
            ConstructionActionKind.StartGhostConstruction => $"starting material/tool for '{step.PrototypeOrQualityOrTag}'",
            _ => step.Description
        };
    }

    public string? GetActiveStepHudSummary()
    {
        if (_activePlan == null || _stepIndex < 0 || _stepIndex >= _activePlan.Steps.Count)
            return null;

        var step = _activePlan.Steps[_stepIndex];
        return $"Step {_stepIndex + 1}/{_activePlan.Steps.Count}: {step.Description}";
    }

    public float GetActiveStepProgress()
    {
        if (_activePlan == null || _activePlan.Steps.Count == 0)
            return 0f;

        return Math.Clamp((float)(_stepIndex + 1) / _activePlan.Steps.Count, 0.05f, 0.98f);
    }

    public bool TryGetHudDisplay(
        out string header,
        out string detail,
        out string? subDetail,
        out float progress,
        out Color accentColor,
        out MapCoordinates? highlightMapCoords)
    {
        header = string.Empty;
        detail = string.Empty;
        subDetail = null;
        progress = 0f;
        accentColor = Color.Cyan;
        highlightMapCoords = null;

        // 1. Standalone AutoHack active solve takes priority when solving
        if (_wiresSolver.IsSolving &&
            _wiresSolver.TryGetHudStatus(out var hackHeader, out var hackDetail, out var hackSub, out var hackProg, out var hackErr, out _, out var hackTarget))
        {
            header = hackHeader;
            detail = hackDetail;
            subDetail = hackSub;
            progress = hackProg;
            accentColor = hackErr ? Color.OrangeRed : Color.Gold;
            if (hackTarget.HasValue && Exists(hackTarget.Value))
                highlightMapCoords = _transform.GetMapCoordinates(hackTarget.Value);
            return true;
        }

        // 2. Active AutoConstruct / AutoDeconstruct / Blueprint Build
        if (_activePlan != null || _blueprintQueue.Count > 0)
        {
            bool isBlueprint = _blueprintQueue.Count > 0 || _activeBlueprintTask != null;
            bool isDeconstruct = _activePlan?.IsDeconstruct == true;

            if (Exists(_currentTarget))
                highlightMapCoords = _transform.GetMapCoordinates(_currentTarget);
            else if (_targetMapCoords.MapId != MapId.Nullspace)
                highlightMapCoords = _targetMapCoords;

            if (isBlueprint)
            {
                int total = Math.Max(_totalBlueprintTasksQueued, _completedBlueprintTasksCount + _blueprintQueue.Count);
                int currentItemNum = Math.Min(total, _completedBlueprintTasksCount + 1);
                var bpName = _activeBlueprintName ?? "Blueprint";
                var phase = CurrentActiveBlueprintPhase;
                var itemProto = _activeBlueprintTask?.Item.EntityPrototypeId ?? _desiredBlueprintEntityProtoId ?? _currentTargetLabel;

                header = $"[Blueprint '{bpName}' ({currentItemNum}/{Math.Max(1, total)})] Building {itemProto}";

                if (_activePlan != null && _stepIndex >= 0 && _stepIndex < _activePlan.Steps.Count)
                {
                    var step = _activePlan.Steps[_stepIndex];
                    detail = $"Step {_stepIndex + 1}/{_activePlan.Steps.Count}: {step.Description} [{_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
                    subDetail = _currentDiagnosticStatus ?? $"Phase {(int)(phase ?? 0)} ({phase}) — {_blueprintQueue.Count} remaining";
                    float itemFrac = (float)_stepIndex / Math.Max(1, _activePlan.Steps.Count);
                    progress = Math.Clamp((_completedBlueprintTasksCount + itemFrac) / Math.Max(1, total), 0.04f, 0.99f);
                }
                else
                {
                    detail = _currentDiagnosticStatus ?? $"Preparing next item in Phase {(int)(phase ?? 0)} ({phase})...";
                    subDetail = $"{_blueprintQueue.Count} item(s) remaining in queue";
                    progress = Math.Clamp((float)_completedBlueprintTasksCount / Math.Max(1, total), 0.04f, 0.99f);
                }

                accentColor = _stepRetryCount > 0 || !string.IsNullOrEmpty(_currentDiagnosticStatus)
                    ? Color.Gold
                    : Color.Cyan;
                return true;
            }

            // Single-target AutoBuild or AutoDeconstruct
            var plan = _activePlan!;
            var stepIdx = Math.Clamp(_stepIndex, 0, Math.Max(0, plan.Steps.Count - 1));
            var curStep = plan.Steps[stepIdx];
            var modeTag = isDeconstruct ? "AutoDeconstruct" : "AutoBuild";

            header = $"[{modeTag}] {_currentTargetLabel} ({plan.StartNode} -> {plan.TargetNode})";
            detail = $"Step {stepIdx + 1}/{plan.Steps.Count}: {curStep.Description} [{_stepTotalElapsed:0.0}s / {JobTimeoutSeconds:0}s]";
            subDetail = _currentDiagnosticStatus;
            progress = Math.Clamp((float)(stepIdx + 1) / Math.Max(1, plan.Steps.Count), 0.08f, 0.98f);
            accentColor = _stepRetryCount > 0
                ? Color.Gold
                : (isDeconstruct ? Color.Orange : Color.Cyan);
            return true;
        }

        // 3. Recent failure or completion banner from AutoConstruct
        if (_hudBannerTimer > 0f && !string.IsNullOrEmpty(_hudBannerHeader))
        {
            header = _hudBannerHeader!;
            detail = _hudBannerDetail ?? string.Empty;
            progress = 1f;
            accentColor = _hudBannerIsError ? Color.OrangeRed : Color.LimeGreen;
            return true;
        }

        // 4. Recent failure or completion banner from AutoHack
        if (_wiresSolver.TryGetHudStatus(out var hBanner, out var dBanner, out var sBanner, out var pBanner, out var errBanner, out var compBanner, out _))
        {
            header = hBanner;
            detail = dBanner;
            subDetail = sBanner;
            progress = pBanner;
            accentColor = errBanner ? Color.OrangeRed : (compBanner ? Color.LimeGreen : Color.Gold);
            return true;
        }

        return false;
    }
}
