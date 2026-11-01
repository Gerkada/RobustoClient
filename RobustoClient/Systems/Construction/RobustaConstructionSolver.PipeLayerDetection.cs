using System.Collections;
using System.Reflection;
using Content.Shared.Atmos.Components;
using Robust.Shared.Prototypes;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    /// <summary>
    /// Reads the live pipe layer of an entity, or null when it has no adjustable pipe layer.
    /// </summary>
    private int? GetLivePipeLayer(EntityUid uid)
    {
        if (!_entMan.TryGetComponent<AtmosPipeLayersComponent>(uid, out var layers))
            return null;

        // Locked layers (e.g. manifolds) and single-layer entities never report a layered position.
        if (layers.PipeLayersLocked || layers.NumberOfPipeLayers <= 1)
            return null;

        return (int)layers.CurrentPipeLayer;
    }

    /// <summary>
    /// Pipe layer an entity prototype is declared to spawn on, or 0 when it is not layer-specific.
    /// Upstream variantized pipe layers, so the layer lives on <c>AtmosPipeLayers.pipeLayer</c> rather
    /// than in the prototype id suffix.
    /// </summary>
    private int GetDeclaredPipeLayer(string? entityProtoId)
    {
        if (string.IsNullOrEmpty(entityProtoId))
            return 0;

        int suffixLayer = GetPipeLayerForIdOrNode(entityProtoId);
        if (suffixLayer > 0)
            return suffixLayer;

        if (_protoMan.TryIndex<EntityPrototype>(entityProtoId, out var proto) &&
            _itemUtil.TryGetPrototypeComponent<AtmosPipeLayersComponent>(proto, "AtmosPipeLayers", out var layers) &&
            layers != null)
        {
            return (int)layers.CurrentPipeLayer;
        }

        return 0;
    }

    public int DetectEntityPipeLayer(EntityUid uid, out string? altEntityProtoId)
    {
        altEntityProtoId = null;
        if (!_entMan.EntityExists(uid))
            return 0;

        EnsureCacheInitialized();

        var protoId = _entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? string.Empty;

        // The prototype id is only the *initial* layer. Upstream pipe layers are variant prototypes that are
        // cycled in place via AtmosPipeLayersComponent.CurrentPipeLayer, so a live component value always wins.
        if (GetLivePipeLayer(uid) is { } liveLayer)
        {
            altEntityProtoId = ResolveAltLayerProtoId(protoId, liveLayer);
            return liveLayer;
        }

        if (!string.IsNullOrEmpty(protoId))
        {
            // Manifolds bridge all 3 pipe layers simultaneously and never have a single Alt1/Alt2 pipe layer
            if (protoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
            {
                altEntityProtoId = TryExtractPipeLayerSuffix(protoId, out var baseManifold, out _) ? baseManifold : protoId;
                return 0;
            }

            if (_altEntityToBaseAndLayer.TryGetValue(protoId, out var cached))
            {
                altEntityProtoId = protoId;
                return cached.PipeLayer;
            }

            if (TryExtractPipeLayerSuffix(protoId, out _, out int suffixLayer) && suffixLayer > 0)
            {
                altEntityProtoId = protoId;
                return suffixLayer;
            }

            // Standard pipes (GasPipe*, DisposalPipe*) encode their layer directly in their EntityPrototype ID
            // (e.g. GasPipeStraight, GasPipeStraightAlt1, GasPipeBendAlt2) and are placed directly without screwdriving.
            if (protoId.Contains("GasPipe", StringComparison.OrdinalIgnoreCase) ||
                protoId.Contains("DisposalPipe", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
        }

        int detectedLayer = 0;
        IDictionary? liveAltProtos = null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 1. Inspect AtmosPipeLayersComponent and all live components for AtmosPipeLayer / PipeLayer fields or properties
        foreach (var comp in _entMan.GetComponents(uid))
        {
            var compType = comp.GetType();
            var compName = compType.Name;
            if (compName is "TransformComponent" or "MetaDataComponent" or "PhysicsComponent" or "FixturesComponent")
                continue;

            if (liveAltProtos == null &&
                (GetMemberValue(comp, "AlternativePrototypes") ?? GetMemberValue(comp, "_alternativePrototypes")) is IDictionary altDict)
            {
                liveAltProtos = altDict;
            }

            if (compName.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                compName.Contains("AtmosPipe", StringComparison.OrdinalIgnoreCase))
            {
                var rawLayer = GetMemberValue(comp, "CurrentPipeLayer")
                               ?? GetMemberValue(comp, "PipeLayer")
                               ?? GetMemberValue(comp, "Layer")
                               ?? GetMemberValue(comp, "CurrentLayer");
                if (TryParsePipeLayerValue(rawLayer, out int compLayer) && compLayer > 0)
                {
                    detectedLayer = compLayer;
                    break;
                }
            }

            foreach (var prop in compType.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0)
                    continue;

                var pTypeName = prop.PropertyType.Name;
                bool isPipeLayerMember = pTypeName.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                                         prop.Name.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase);
                if (isPipeLayerMember || prop.PropertyType.IsEnum)
                {
                    try
                    {
                        var val = prop.GetValue(comp);
                        if (isPipeLayerMember)
                        {
                            if (TryParsePipeLayerValue(val, out int pLayer) && pLayer > 0)
                            {
                                detectedLayer = pLayer;
                                break;
                            }
                        }
                        else if (val is Enum eVal)
                        {
                            var eStr = eVal.ToString();
                            if (eStr.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt1", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 1;
                                break;
                            }
                            if (eStr.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt2", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 2;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore reflection getter exceptions
                    }
                }
            }

            if (detectedLayer > 0)
                break;

            foreach (var field in compType.GetFields(flags))
            {
                var fTypeName = field.FieldType.Name;
                bool isPipeLayerField = fTypeName.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                                        field.Name.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase);
                if (isPipeLayerField || field.FieldType.IsEnum)
                {
                    try
                    {
                        var val = field.GetValue(comp);
                        if (isPipeLayerField)
                        {
                            if (TryParsePipeLayerValue(val, out int fLayer) && fLayer > 0)
                            {
                                detectedLayer = fLayer;
                                break;
                            }
                        }
                        else if (val is Enum eVal)
                        {
                            var eStr = eVal.ToString();
                            if (eStr.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt1", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 1;
                                break;
                            }
                            if (eStr.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                                eStr.Equals("Alt2", StringComparison.OrdinalIgnoreCase))
                            {
                                detectedLayer = 2;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }

            if (detectedLayer > 0)
                break;
        }

        // 2. Inspect NodeContainerComponent PipeNodes if present on client
        if (detectedLayer == 0 && !protoId.Contains("Manifold", StringComparison.OrdinalIgnoreCase))
        {
            var nodeCont = FindComponentByName(uid, "NodeContainerComponent");
            if (nodeCont != null &&
                (GetMemberValue(nodeCont, "Nodes") ?? GetMemberValue(nodeCont, "_nodes")) is IDictionary nodesDict)
            {
                var foundLayers = new HashSet<int>();
                int primaryPipeNodeLayer = 0;

                foreach (DictionaryEntry entry in nodesDict)
                {
                    var nodeObj = entry.Value;
                    if (nodeObj == null)
                        continue;

                    var rawNodeLayer = GetMemberValue(nodeObj, "CurrentPipeLayer")
                                       ?? GetMemberValue(nodeObj, "PipeLayer")
                                       ?? GetMemberValue(nodeObj, "Layer");
                    if (TryParsePipeLayerValue(rawNodeLayer, out int nLayer))
                    {
                        foundLayers.Add(nLayer);
                        var nodeKey = entry.Key?.ToString() ?? string.Empty;
                        if (nLayer > 0 &&
                            (nodeKey.Equals("pipe", StringComparison.OrdinalIgnoreCase) ||
                             nodeKey.Equals("inlet", StringComparison.OrdinalIgnoreCase) ||
                             nodeKey.Equals("port", StringComparison.OrdinalIgnoreCase)))
                        {
                            primaryPipeNodeLayer = nLayer;
                        }
                    }
                }

                if (foundLayers.Count == 1)
                {
                    int single = foundLayers.First();
                    if (single > 0)
                        detectedLayer = single;
                }
                else if (primaryPipeNodeLayer > 0 && foundLayers.Count < 3)
                {
                    detectedLayer = primaryPipeNodeLayer;
                }
            }
        }

        // 3. Inspect AppearanceComponent.AppearanceData for AtmosPipeLayer / PipeVisuals
        if (detectedLayer == 0)
        {
            var appComp = FindComponentByName(uid, "AppearanceComponent");
            if (appComp != null &&
                (GetMemberValue(appComp, "AppearanceData") ?? GetMemberValue(appComp, "_appearanceData")) is IDictionary appDict)
            {
                foreach (DictionaryEntry entry in appDict)
                {
                    if (entry.Value is Enum valEnum)
                    {
                        var enumStr = valEnum.ToString();
                        if (enumStr.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
                            enumStr.Equals("Alt1", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 1;
                            break;
                        }
                        if (enumStr.Equals("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                            enumStr.Equals("Alt2", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 2;
                            break;
                        }
                    }

                    var keyType = entry.Key?.GetType().Name ?? string.Empty;
                    var keyStr = entry.Key?.ToString() ?? string.Empty;
                    var valType = entry.Value?.GetType().Name ?? string.Empty;
                    if (keyType.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                        keyStr.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                        valType.Contains("PipeLayer", StringComparison.OrdinalIgnoreCase) ||
                        keyType.Contains("AtmosPipe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParsePipeLayerValue(entry.Value, out int appLayer) && appLayer > 0)
                        {
                            detectedLayer = appLayer;
                            break;
                        }
                    }
                }
            }
        }

        // 4. Inspect SpriteComponent visible layers for Alt1/Alt2 or Secondary/Tertiary RSI states
        if (detectedLayer == 0)
        {
            var spriteComp = FindComponentByName(uid, "SpriteComponent");
            if (spriteComp != null)
            {
                var layers = GetMemberValue(spriteComp, "AllLayers") as IEnumerable
                             ?? GetMemberValue(spriteComp, "_layers") as IEnumerable;
                if (layers != null)
                {
                    foreach (var entry in layers)
                    {
                        if (entry == null)
                            continue;

                        var layer = entry is DictionaryEntry de ? de.Value : GetMemberValue(entry, "Value") ?? entry;
                        if (layer == null)
                            continue;

                        if (GetMemberValue(layer, "Visible") is false)
                            continue;

                        var stateObj = GetMemberValue(layer, "State");
                        var stateName = GetStringMember(stateObj, "Name")
                                        ?? GetStringMember(layer, "RsiState")
                                        ?? stateObj?.ToString()
                                        ?? string.Empty;
                        var rsiObj = GetMemberValue(layer, "Rsi") ?? GetMemberValue(layer, "ActualRsi");
                        var rsiPath = GetStringMember(rsiObj, "Path") ?? rsiObj?.ToString() ?? string.Empty;

                        bool isFrame = protoId.Contains("Frame", StringComparison.OrdinalIgnoreCase);
                        if (stateName.Contains("Alt1", StringComparison.OrdinalIgnoreCase) ||
                            stateName.Contains("Secondary", StringComparison.OrdinalIgnoreCase) ||
                            (!isFrame && stateName.EndsWith("_1", StringComparison.OrdinalIgnoreCase)) ||
                            rsiPath.Contains("alt1", StringComparison.OrdinalIgnoreCase) ||
                            rsiPath.Contains("secondary", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 1;
                            break;
                        }

                        if (stateName.Contains("Alt2", StringComparison.OrdinalIgnoreCase) ||
                            stateName.Contains("Tertiary", StringComparison.OrdinalIgnoreCase) ||
                            (!isFrame && stateName.EndsWith("_2", StringComparison.OrdinalIgnoreCase)) ||
                            rsiPath.Contains("alt2", StringComparison.OrdinalIgnoreCase) ||
                            rsiPath.Contains("tertiary", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedLayer = 2;
                            break;
                        }
                    }
                }
            }
        }

        if (detectedLayer > 0)
        {
            if (liveAltProtos != null)
            {
                foreach (DictionaryEntry entry in liveAltProtos)
                {
                    if (TryParsePipeLayerValue(entry.Key, out int kLayer) && kLayer == detectedLayer)
                    {
                        var candidate = ExtractIdString(entry.Value);
                        if (!string.IsNullOrEmpty(candidate))
                        {
                            altEntityProtoId = candidate;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(altEntityProtoId) && !string.IsNullOrEmpty(protoId))
            {
                var cleanBase = TryExtractPipeLayerSuffix(protoId, out var b, out _) ? b : protoId;
                if (_entityAltLayerProtos.TryGetValue((cleanBase, detectedLayer), out var cachedAlt))
                {
                    altEntityProtoId = cachedAlt;
                }
                else
                {
                    var suffix = detectedLayer == 1 ? "Alt1" : "Alt2";
                    altEntityProtoId = cleanBase + suffix;
                }
            }
        }

        return detectedLayer;
    }
}
