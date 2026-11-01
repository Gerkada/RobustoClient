using System.Collections;
using Content.Shared.Construction;

namespace RobustoClient.Systems.Construction;

public sealed partial class RobustaConstructionSolver
{
    private static string? ExtractIdString(object? val)
    {
        if (val == null)
            return null;
        if (val is string s)
            return s;
        if (GetMemberValue(val, "Id") is string idStr)
            return idStr;
        return val.ToString();
    }

    private void ConvertEdgeSteps(
        ConstructionGraphEdge edge,
        string expectedNextNode,
        List<ConstructionActionStep> output)
    {
        var rawSteps = GetMemberValue(edge, "Steps") as IEnumerable ?? GetMemberValue(edge, "_steps") as IEnumerable;
        if (rawSteps == null)
            return;

        foreach (var stepObj in rawSteps)
        {
            if (stepObj != null)
                ConvertSingleOrNestedStep(stepObj, expectedNextNode, output);
        }
    }

    private void ConvertSingleOrNestedStep(
        object stepObj,
        string expectedNextNode,
        List<ConstructionActionStep> output)
    {
        var type = stepObj.GetType();
        var typeName = type.Name;

        // Handle NestedConstructionGraphStep (which stores nested step collections)
        if (typeName.Contains("NestedConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var nestedEnumerable = GetMemberValue(stepObj, "Steps") as IEnumerable
                                   ?? GetMemberValue(stepObj, "_steps") as IEnumerable;
            if (nestedEnumerable != null)
            {
                foreach (var sub in nestedEnumerable)
                {
                    if (sub == null)
                        continue;

                    if (sub is IEnumerable subList && sub is not string)
                    {
                        foreach (var innerStep in subList)
                        {
                            if (innerStep != null)
                                ConvertSingleOrNestedStep(innerStep, expectedNextNode, output);
                        }
                    }
                    else
                    {
                        ConvertSingleOrNestedStep(sub, expectedNextNode, output);
                    }
                }
            }
            return;
        }

        float doAfter = GetFloatMember(stepObj, "DoAfter", 0f);
        int amount = Math.Max(1, GetIntMember(stepObj, "Amount", 1));

        // 1. MaterialConstructionGraphStep
        if (typeName.Contains("MaterialConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var materialId = GetStringMember(stepObj, "MaterialPrototypeId")
                             ?? GetStringMember(stepObj, "Material")
                             ?? "Steel";
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.AddMaterial,
                $"Add {amount}x {materialId}",
                PrototypeOrQualityOrTag: materialId,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 2. ToolConstructionGraphStep
        if (typeName.Contains("ToolConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var toolQuality = GetStringMember(stepObj, "Tool") ?? "Wrenching";
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.UseTool,
                $"Use {toolQuality} tool",
                PrototypeOrQualityOrTag: toolQuality,
                Amount: 1,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 3. PrototypeConstructionGraphStep / EntityInsertConstructionGraphStep
        if (typeName.Contains("PrototypeConstructionGraphStep", StringComparison.OrdinalIgnoreCase) ||
            (typeName.Contains("EntityInsertConstructionGraphStep", StringComparison.OrdinalIgnoreCase) &&
             !typeName.Contains("Component", StringComparison.OrdinalIgnoreCase) &&
             !typeName.Contains("Tag", StringComparison.OrdinalIgnoreCase)))
        {
            var protoId = GetStringMember(stepObj, "Prototype")
                          ?? GetStringMember(stepObj, "PrototypeId")
                          ?? string.Empty;
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertPrototype,
                $"Insert {amount}x {protoId}",
                PrototypeOrQualityOrTag: protoId,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 4. ComponentConstructionGraphStep
        if (typeName.Contains("ComponentConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var compName = GetStringMember(stepObj, "Component") ?? string.Empty;
            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertComponent,
                $"Insert {amount}x [{compName}]",
                PrototypeOrQualityOrTag: compName,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
            return;
        }

        // 5. TagConstructionGraphStep / MultipleTagsConstructionGraphStep
        if (typeName.Contains("TagConstructionGraphStep", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("MultipleTagsConstructionGraphStep", StringComparison.OrdinalIgnoreCase))
        {
            var tag = GetStringMember(stepObj, "Tag") ?? GetStringMember(stepObj, "_tag");
            if (string.IsNullOrEmpty(tag))
            {
                tag = FirstElementAsString(GetMemberValue(stepObj, "AllTags") ?? GetMemberValue(stepObj, "_allTags"))
                      ?? FirstElementAsString(GetMemberValue(stepObj, "AnyTags") ?? GetMemberValue(stepObj, "_anyTags"))
                      ?? string.Empty;
            }

            output.Add(new ConstructionActionStep(
                ConstructionActionKind.InsertTag,
                $"Insert {amount}x tag:{tag}",
                PrototypeOrQualityOrTag: tag,
                Amount: amount,
                DoAfterDelay: doAfter,
                ExpectedNextNode: expectedNextNode));
        }
    }
}
