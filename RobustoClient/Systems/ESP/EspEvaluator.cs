using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Containers;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using RobustoClient.Systems.Abstract;

namespace RobustoClient.Systems.ESP;

public sealed class EspEvaluationResult
{
    public EspCategoryDefinition Category { get; set; } = default!;
    public string RenderText { get; set; } = string.Empty;

    /// <summary>Per-entity color from the category's <c>colorFrom</c> rule, or null to use the category color.</summary>
    public Color? Color { get; set; }
}

public sealed class EspEvaluator : EntitySystem
{
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>Bound on the evaluation cache.</summary>
    private const int CacheLimit = 1024;

    /// <summary>Lifetime of a cached evaluation.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    private readonly Dictionary<EntityUid, (List<EspEvaluationResult> Results, TimeSpan Stamp)> _cache = new();

    private readonly Dictionary<string, Func<EntityUid, bool>> _customConditions = new();
    private readonly Dictionary<string, VariableBinding> _customVariables = new();

    /// <summary>Per-entity color rules a category can name with <c>colorFrom</c>.</summary>
    private readonly Dictionary<string, Func<EntityUid, Color?>> _customColors = new();

    /// <summary>Component each custom rule provably requires, for the category index.</summary>
    private readonly Dictionary<string, string> _ruleGates = new(StringComparer.Ordinal);

    private readonly Dictionary<Type, List<EspCategoryDefinition>> _byComponent = new();
    private readonly List<EspCategoryDefinition> _ungated = new();
    private bool _indexDirty = true;

    private EspRegistry _registry = default!;

    public EspRegistry Registry => _registry;

    /// <summary>A variable's resolver plus the token it replaces.</summary>
    private readonly record struct VariableBinding(string Token, Func<EntityUid, string> Resolver);

    public override void Initialize()
    {
        base.Initialize();
        _registry = new EspRegistry();

        EntityManager.ComponentAdded += OnComponentAdded;
        EntityManager.ComponentRemoved += OnComponentRemoved;
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnContainerChange);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnContainerChange);
        
        // Basic variables
        RegisterVariable("EntityName", uid => TryComp<MetaDataComponent>(uid, out var meta) ? meta.EntityName : string.Empty);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        EntityManager.ComponentAdded -= OnComponentAdded;
        EntityManager.ComponentRemoved -= OnComponentRemoved;
    }

    private void OnComponentAdded(AddedComponentEventArgs args) => Invalidate(args.BaseArgs.Owner);
    private void OnComponentRemoved(RemovedComponentEventArgs args) => Invalidate(args.BaseArgs.Owner);
    
    private void OnContainerChange(EntInsertedIntoContainerMessage args)
    {
        Invalidate(args.Entity);
        Invalidate(args.Container.Owner);
    }

    private void OnContainerChange(EntRemovedFromContainerMessage args)
    {
        Invalidate(args.Entity);
        Invalidate(args.Container.Owner);
    }
    
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Registers a rule esp.yml can name, <paramref name="requires"/> is a component the rule cannot match without.
    /// </summary>
    public void RegisterCondition(string name, Func<EntityUid, bool> predicate, string? requires = null)
    {
        _customConditions[name] = predicate;

        if (requires != null)
            _ruleGates[name] = requires;

        _indexDirty = true;
    }

    public void RegisterVariable(string name, Func<EntityUid, string> resolver)
    {
        _customVariables[name] = new VariableBinding("{" + name + "}", resolver);
    }

    /// <summary>
    /// Registers a per-entity color a category can name with <c>colorFrom</c>. Returning null keeps
    /// the category's own color.
    /// </summary>
    public void RegisterColor(string name, Func<EntityUid, Color?> resolver)
    {
        _customColors[name] = resolver;
    }

    public void Invalidate(EntityUid uid)
    {
        _cache.Remove(uid);
    }

    public List<EspEvaluationResult> Evaluate(EntityUid uid)
    {
        if (_cache.TryGetValue(uid, out var cached) && _timing.CurTime - cached.Stamp < CacheLifetime)
            return cached.Results;

        if (_indexDirty)
            RebuildIndex();

        var results = new List<EspEvaluationResult>();

        if (TryComp<MetaDataComponent>(uid, out var meta))
        {
            if (meta.EntityPrototype?.ID == "VirtualItem" || 
                meta.EntityPrototype?.ID == "ActionDummy" ||
                meta.EntityName == "VIRTUAL ITEM YOU SHOULD NOT SEE THIS")
            {
                Store(uid, results);
                return results;
            }
        }

        // Ungated categories always run, the rest are bucketed by a required component.
        foreach (var category in _ungated)
            EvaluateCategory(uid, category, results);

        foreach (var (componentType, categories) in _byComponent)
        {
            if (!HasComp(uid, componentType))
                continue;

            foreach (var category in categories)
                EvaluateCategory(uid, category, results);
        }

        // Keep the highest-priority result per group.
        var groupedResults = new Dictionary<string, List<EspEvaluationResult>>();
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r.Category.Group))
            {
                if (!groupedResults.ContainsKey(r.Category.Group))
                    groupedResults[r.Category.Group] = new List<EspEvaluationResult>();
                groupedResults[r.Category.Group].Add(r);
            }
        }
        
        foreach (var kvp in groupedResults)
        {
            if (kvp.Value.Count > 1)
            {
                EspEvaluationResult best = kvp.Value[0];
                foreach (var r in kvp.Value)
                {
                    if (r.Category.Priority > best.Category.Priority)
                        best = r;
                }
                
                for (int i = results.Count - 1; i >= 0; i--)
                {
                    if (results[i].Category.Group == kvp.Key && results[i] != best)
                    {
                        results.RemoveAt(i);
                    }
                }
            }
        }

        Store(uid, results);
        return results;
    }

    private void EvaluateCategory(EntityUid uid, EspCategoryDefinition category, List<EspEvaluationResult> results)
    {
        if (!Matches(uid, category.Conditions))
            return;

        var text = ResolveText(uid, category.TextFormat);
        if (string.IsNullOrWhiteSpace(text))
            return;

        Color? color = null;
        if (!string.IsNullOrEmpty(category.ColorFrom) &&
            _customColors.TryGetValue(category.ColorFrom, out var resolver))
        {
            color = resolver(uid);
        }

        results.Add(new EspEvaluationResult { Category = category, RenderText = text, Color = color });
    }

    private void Store(EntityUid uid, List<EspEvaluationResult> results)
    {
        var now = _timing.CurTime;

        // Expired entries go first. Dropping the oldest outright is what made a crowded room
        // re-evaluate every entity on every frame: the set the next frame asks for never fit.
        if (_cache.Count >= CacheLimit)
        {
            CacheUtility.RemoveOlderThan(_cache, entry => entry.Stamp, now - CacheLifetime);

            if (_cache.Count >= CacheLimit)
                CacheUtility.RemoveOldest(_cache, entry => entry.Stamp);
        }

        _cache[uid] = (results, now);
    }

    /// <summary>Rebuilds the component buckets from the registry and rule gates.</summary>
    private void RebuildIndex()
    {
        _indexDirty = false;
        _byComponent.Clear();
        _ungated.Clear();

        var (byComponent, ungated) = EspCategoryIndex.Build(_registry.Categories, _ruleGates);
        _ungated.AddRange(ungated);

        foreach (var (componentName, categories) in byComponent)
        {
            if (!_compFactory.TryGetRegistration(componentName, out var registration))
            {
                _ungated.AddRange(categories);
                continue;
            }

            if (!_byComponent.TryGetValue(registration.Type, out var list))
                _byComponent[registration.Type] = list = new List<EspCategoryDefinition>();

            list.AddRange(categories);
        }
    }

    private bool Matches(EntityUid uid, EspConditions conditions)
    {
        // hasComponent
        foreach (var compName in conditions.HasComponent)
        {
            if (!_compFactory.TryGetRegistration(compName, out var reg) || !HasComp(uid, reg.Type))
                return false;
        }

        // hasNotComponent
        foreach (var compName in conditions.HasNotComponent)
        {
            if (_compFactory.TryGetRegistration(compName, out var reg) && HasComp(uid, reg.Type))
                return false;
        }

        // prototypeContains
        if (conditions.PrototypeContains.Count > 0)
        {
            if (!TryComp<MetaDataComponent>(uid, out var meta) || meta.EntityPrototype == null)
                return false;

            bool match = false;
            foreach (var proto in conditions.PrototypeContains)
            {
                if (meta.EntityPrototype.ID.Contains(proto, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                    break;
                }
            }
            if (!match) return false;
        }

        // jobContains
        if (conditions.JobContains.Count > 0)
        {
            var jobTitle = ResolveVariable(uid, "JobTitle");
            bool match = false;
            foreach (var job in conditions.JobContains)
            {
                if (jobTitle.Contains(job, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                    break;
                }
            }
            if (!match) return false;
        }

        // customRule
        foreach (var rule in conditions.CustomRule)
        {
            if (_customConditions.TryGetValue(rule, out var predicate))
            {
                if (!predicate(uid)) return false;
            }
            else
            {
                return false; // Unknown rule fails match
            }
        }

        return true;
    }

    public string ResolveVariable(EntityUid uid, string name) =>
        _customVariables.TryGetValue(name, out var binding) ? binding.Resolver(uid) : string.Empty;

    /// <summary>Substitutes registered tokens into a category's format.</summary>
    internal string ResolveText(EntityUid uid, string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return string.Empty;

        // Most formats carry no token at all, so the scan is skipped entirely for them.
        if (format.IndexOf('{') < 0)
            return format.Trim();

        var result = format;
        foreach (var binding in _customVariables.Values)
        {
            if (result.Contains(binding.Token))
                result = result.Replace(binding.Token, binding.Resolver(uid));
        }

        return result.Trim();
    }
}
