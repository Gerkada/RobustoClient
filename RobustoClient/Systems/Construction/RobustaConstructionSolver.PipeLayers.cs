using Content.Shared.Construction;
using Content.Shared.Construction.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    public void ResolveExportBlueprintPrototypes(
        EntityUid uid,
        string rawEntityProtoId,
        out string exportedEntityProtoId,
        out string? exportedConstructionProtoId)
    {
        EnsureCacheInitialized();

        if (rawEntityProtoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            exportedEntityProtoId = TryExtractPipeLayerSuffix(rawEntityProtoId, out var baseManifold, out _)
                ? baseManifold
                : rawEntityProtoId;
            exportedConstructionProtoId = ResolveConstructionPrototypeForEntity(exportedEntityProtoId);
            return;
        }

        exportedEntityProtoId = rawEntityProtoId;
        int pipeLayer = DetectEntityPipeLayer(uid, out var detectedAltProtoId);

        if (pipeLayer > 0)
        {
            if (!string.IsNullOrEmpty(detectedAltProtoId))
            {
                exportedEntityProtoId = detectedAltProtoId;
            }
            else if (TryExtractPipeLayerSuffix(rawEntityProtoId, out var baseId, out int existingLayer) && existingLayer == pipeLayer)
            {
                exportedEntityProtoId = rawEntityProtoId;
            }
            else
            {
                var cleanBase = TryExtractPipeLayerSuffix(rawEntityProtoId, out var b, out _) ? b : rawEntityProtoId;
                if (_entityAltLayerProtos.TryGetValue((cleanBase, pipeLayer), out var mappedAlt))
                {
                    exportedEntityProtoId = mappedAlt;
                }
                else
                {
                    var suffix = pipeLayer == 1 ? "Alt1" : "Alt2";
                    exportedEntityProtoId = cleanBase + suffix;
                }
            }
        }

        exportedConstructionProtoId = ResolveConstructionPrototypeForEntity(exportedEntityProtoId);
    }

    private string ResolveAltLayerProtoId(string protoId, int pipeLayer)
    {
        if (string.IsNullOrEmpty(protoId))
            return string.Empty;

        if (TryExtractPipeLayerSuffix(protoId, out _, out int existingLayer) && existingLayer == pipeLayer)
            return protoId;

        var cleanBase = TryExtractPipeLayerSuffix(protoId, out var stripped, out _) ? stripped : protoId;
        if (_entityAltLayerProtos.TryGetValue((cleanBase, pipeLayer), out var mappedAlt))
            return mappedAlt;

        return pipeLayer == 0 ? cleanBase : cleanBase + (pipeLayer == 1 ? "Alt1" : "Alt2");
    }

    public bool ShouldUseScrewdriverForPipeLayer(
        IndexedConstructionGraph graph,
        IndexedGraphNode node,
        string? desiredEntityProtoId)
    {
        if (!IsPipeOrAtmosGraphOrNode(graph, node, desiredEntityProtoId))
            return false;

        var graphId = graph.Id ?? string.Empty;
        var nodeName = node.Name;
        var protoId = desiredEntityProtoId ?? string.Empty;

        // Pipes (GasPipe / DisposalPipe) and Manifolds are placed directly on their target layer via ConstructionPrototype,
        // never via Screwdriver cycling. Only use Screwdriver cycling for contraptions (connectors, mixers, filters, vents, etc.).
        if (graphId.Equals("GasPipe", StringComparison.OrdinalIgnoreCase) ||
            graphId.Contains("Disposal", StringComparison.OrdinalIgnoreCase) ||
            nodeName.Contains("manifold", StringComparison.OrdinalIgnoreCase) ||
            protoId.Contains("GasPipe", StringComparison.OrdinalIgnoreCase) ||
            protoId.Contains("DisposalPipe", StringComparison.OrdinalIgnoreCase) ||
            protoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public int GetDesiredPipeLayer(string? entityProtoId, string? fallbackNodeOrRecipeId)
    {
        EnsureCacheInitialized();

        if (!string.IsNullOrEmpty(entityProtoId))
        {
            if (entityProtoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (_altEntityToBaseAndLayer.TryGetValue(entityProtoId, out var cached))
                return cached.PipeLayer;

            if (TryExtractPipeLayerSuffix(entityProtoId, out _, out int layer) && layer > 0)
                return layer;
        }

        if (!string.IsNullOrEmpty(fallbackNodeOrRecipeId))
        {
            if (fallbackNodeOrRecipeId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (TryExtractPipeLayerSuffix(fallbackNodeOrRecipeId, out _, out int fbLayer) && fbLayer > 0)
                return fbLayer;
        }

        return 0;
    }

    public static int GetPipeLayerForIdOrNode(string? idOrNodeName)
    {
        return TryExtractPipeLayerSuffix(idOrNodeName, out _, out int layer) ? layer : 0;
    }

    public string NormalizeBaseEntityPrototypeId(string? protoId)
    {
        if (string.IsNullOrEmpty(protoId))
            return string.Empty;

        EnsureCacheInitialized();

        if (_altEntityToBaseAndLayer.TryGetValue(protoId, out var cached))
            return cached.BaseProtoId;

        return TryExtractPipeLayerSuffix(protoId, out var baseId, out _) ? baseId : protoId;
    }

    public static bool TryExtractPipeLayerSuffix(string? id, out string baseId, out int pipeLayer)
    {
        baseId = id ?? string.Empty;
        pipeLayer = 0;
        if (string.IsNullOrEmpty(id))
            return false;

        if (id.EndsWith("Alt1", StringComparison.OrdinalIgnoreCase))
        {
            baseId = id[..^4];
            pipeLayer = 1;
            return true;
        }

        if (id.EndsWith("Alt2", StringComparison.OrdinalIgnoreCase))
        {
            baseId = id[..^4];
            pipeLayer = 2;
            return true;
        }

        if (id.EndsWith("Secondary", StringComparison.OrdinalIgnoreCase) && id.Length > 9)
        {
            baseId = id[..^9];
            pipeLayer = 1;
            return true;
        }

        if (id.EndsWith("Tertiary", StringComparison.OrdinalIgnoreCase) && id.Length > 8)
        {
            baseId = id[..^8];
            pipeLayer = 2;
            return true;
        }

        if (id.EndsWith("Alt1Flipped", StringComparison.OrdinalIgnoreCase) && id.Length > 11)
        {
            baseId = id[..^11] + "Flipped";
            pipeLayer = 1;
            return true;
        }

        if (id.EndsWith("Alt2Flipped", StringComparison.OrdinalIgnoreCase) && id.Length > 11)
        {
            baseId = id[..^11] + "Flipped";
            pipeLayer = 2;
            return true;
        }

        return false;
    }

    private static bool TryParsePipeLayerValue(object? val, out int pipeLayer)
    {
        pipeLayer = 0;
        if (val == null)
            return false;

        var str = val.ToString() ?? string.Empty;
        if (str.Equals("Primary", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("Center", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            pipeLayer = 0;
            return true;
        }

        if (str.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("Alt1", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            pipeLayer = 1;
            return true;
        }

        if (str.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("Alt2", StringComparison.OrdinalIgnoreCase) ||
            str.Equals("2", StringComparison.OrdinalIgnoreCase))
        {
            pipeLayer = 2;
            return true;
        }

        if (val is Enum or int or byte or sbyte or short or ushort)
        {
            try
            {
                int v = Convert.ToInt32(val);
                if (v >= 0 && v <= 2)
                {
                    pipeLayer = v;
                    return true;
                }
            }
            catch
            {
                // Ignore
            }
        }

        return false;
    }

    private static void AppendPipeLayerScrewdriverSteps(
        int currentLayer,
        int desiredLayer,
        List<ConstructionActionStep> steps)
    {
        int cur = ((currentLayer % 3) + 3) % 3;
        int dst = ((desiredLayer % 3) + 3) % 3;
        if (cur == dst)
            return;

        int clicks = (dst - cur + 3) % 3;
        for (int i = 1; i <= clicks; i++)
        {
            int stepTargetLayer = (cur + i) % 3;
            string label = stepTargetLayer switch
            {
                1 => "Alt1/Secondary",
                2 => "Alt2/Tertiary",
                _ => "Primary"
            };

            steps.Add(new ConstructionActionStep(
                ConstructionActionKind.UseTool,
                $"Set pipe layer to {label} (Screwing)",
                PrototypeOrQualityOrTag: "Screwing",
                Amount: 1,
                DoAfterDelay: 0.25f,
                ExpectedNextNode: null,
                TargetPipeLayer: stepTargetLayer));
        }
    }
}
