using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;

namespace RobustoClient.Systems.ESP;

public sealed class EspRegistry
{
    private readonly Dictionary<string, EspCategoryDefinition> _categories = new();

    public IReadOnlyCollection<EspCategoryDefinition> Categories => _categories.Values;

    private const string DefaultConfig = @"
- id: AdminESP
  priority: 100
  color: ""#FF0000""
  textFormat: ""[!!! ADMIN !!!] {PlayerName}""
  dock: Top
  conditions:
    prototypeContains: [ ""Admin"" ]

- id: GhostESP
  priority: 10
  color: ""#808080""
  textFormat: ""[GHOST] {PlayerName}""
  dock: Top
  conditions:
    hasComponent: [ ""Ghost"" ]

- id: JobSecurity
  priority: 80
  color: ""#7070FF""
  textFormat: ""[{JobTitle}] {EntityName} {PlayerName}""
  dock: Top
  conditions:
    jobContains: [ ""Security"", ""Officer"", ""Warden"", ""Captain"" ]
    hasNotComponent: [ ""PAI"" ]

- id: JobMedical
  priority: 70
  color: ""#70FF70""
  textFormat: ""[{JobTitle}] {EntityName} {PlayerName}""
  dock: Top
  conditions:
    jobContains: [ ""Medical"", ""Doctor"" ]
    hasNotComponent: [ ""PAI"" ]

- id: JobEngineer
  priority: 60
  color: ""#FFFF70""
  textFormat: ""[{JobTitle}] {EntityName} {PlayerName}""
  dock: Top
  conditions:
    jobContains: [ ""Engineer"" ]
    hasNotComponent: [ ""PAI"" ]

- id: DefaultAlive
  priority: 10
  color: ""#FFFFFF""
  textFormat: ""{EntityName} {PlayerName}""
  dock: Top
  conditions:
    customRule: [ ""IsAlive"" ]
    hasNotComponent: [ ""PAI"" ]

- id: AntagonistTag
  priority: 90
  color: ""#FF0000""
  textFormat: ""ANTAG""
  dock: Right
  conditions:
    customRule: [ ""IsAntag"" ]

- id: ContrabandTag
  priority: 80
  color: ""#FFA500""
  textFormat: ""SUS""
  dock: Right
  conditions:
    customRule: [ ""IsSus"" ]

- id: ImplantsTag
  priority: 70
  color: ""#FFFFFF""
  textFormat: ""{Implants}""
  dock: Right
  conditions:
    customRule: [ ""HasImplants"" ]

- id: HeldItems
  priority: 50
  color: ""#FFA500""
  textFormat: "">> {HeldItems} <<""
  dock: Bottom
  conditions:
    customRule: [ ""HasHeldItems"" ]
";

    public EspRegistry()
    {
        LoadConfigs();
    }

    public void LoadConfigs()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = global::System.IO.Path.Combine(appData, "RobustoClient", "Configs");
            var file = global::System.IO.Path.Combine(dir, "esp.yml");

            if (!global::System.IO.Directory.Exists(dir))
            {
                global::System.IO.Directory.CreateDirectory(dir);
            }

            if (!global::System.IO.File.Exists(file))
            {
                global::System.IO.File.WriteAllText(file, DefaultConfig);
            }

            var yamlString = global::System.IO.File.ReadAllText(file);
            LoadFromString(yamlString);
        }
        catch (Exception ex)
        {
            Robust.Shared.Log.Logger.ErrorS("ESP", $"Failed to load ESP configs from disk: {ex}");
            // Fallback to default in memory if file reading fails
            LoadFromString(DefaultConfig);
        }
    }

    public void LoadFromString(string yamlString)
    {
        _categories.Clear();
        try
        {
            using var reader = new global::System.IO.StringReader(yamlString);
            var docs = DataNodeParser.ParseYamlStream(reader).ToList();
            if (docs.Count == 0) return;
            
            var root = docs[0].Root;
            if (root is not SequenceDataNode seq) return;

            foreach (var node in seq.Sequence)
            {
                if (node is MappingDataNode mapping)
                {
                    var def = ParseCategory(mapping);
                    if (def != null && !string.IsNullOrWhiteSpace(def.Id))
                        _categories[def.Id] = def;
                }
            }
        }
        catch (Exception ex)
        {
            Robust.Shared.Log.Logger.ErrorS("ESP", $"Failed to parse ESP YAML: {ex}");
        }
    }

    private EspCategoryDefinition? ParseCategory(MappingDataNode mapping)
    {
        var def = new EspCategoryDefinition();

        if (mapping.TryGet("id", out ValueDataNode? idNode))
            def.Id = idNode.Value;

        if (mapping.TryGet("priority", out ValueDataNode? prioNode) && int.TryParse(prioNode.Value, out var prio))
            def.Priority = prio;

        if (mapping.TryGet("color", out ValueDataNode? colorNode))
            def.Color = Color.FromHex(colorNode.Value, Color.White);

        if (mapping.TryGet("textFormat", out ValueDataNode? textNode))
            def.TextFormat = textNode.Value;

        if (mapping.TryGet("dock", out ValueDataNode? dockNode))
        {
            if (Enum.TryParse<EspDock>(dockNode.Value, true, out var dock))
                def.Dock = dock;
        }

        if (mapping.TryGet("conditions", out MappingDataNode? condMapping))
        {
            ParseList(condMapping, "hasComponent", def.Conditions.HasComponent);
            ParseList(condMapping, "hasNotComponent", def.Conditions.HasNotComponent);
            ParseList(condMapping, "prototypeContains", def.Conditions.PrototypeContains);
            ParseList(condMapping, "jobContains", def.Conditions.JobContains);
            ParseList(condMapping, "customRule", def.Conditions.CustomRule);
        }

        return def;
    }

    private void ParseList(MappingDataNode mapping, string key, List<string> target)
    {
        if (mapping.TryGet(key, out SequenceDataNode? seq))
        {
            foreach (var child in seq.Sequence)
            {
                if (child is ValueDataNode val)
                {
                    target.Add(val.Value);
                }
            }
        }
    }
}
