using System;
using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Containers;

namespace RobustoClient.Systems.ESP;

public sealed class EspEvaluationResult
{
    public EspCategoryDefinition Category { get; set; } = default!;
    public string RenderText { get; set; } = string.Empty;
}

public sealed class EspEvaluator : EntitySystem
{
    [Dependency] private readonly IComponentFactory _compFactory = default!;

    private readonly Dictionary<EntityUid, List<EspEvaluationResult>> _cache = new();
    
    // Extension points
    private readonly Dictionary<string, Func<EntityUid, bool>> _customConditions = new();
    private readonly Dictionary<string, Func<EntityUid, string>> _customVariables = new();

    private EspRegistry _registry = default!;

    public EspRegistry Registry => _registry;

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

    public void RegisterCondition(string name, Func<EntityUid, bool> predicate)
    {
        _customConditions[name] = predicate;
    }

    public void RegisterVariable(string name, Func<EntityUid, string> resolver)
    {
        _customVariables[name] = resolver;
    }

    public void Invalidate(EntityUid uid)
    {
        _cache.Remove(uid);
    }

    public List<EspEvaluationResult> Evaluate(EntityUid uid)
    {
        if (_cache.TryGetValue(uid, out var cached))
            return cached;

        var results = new List<EspEvaluationResult>();

        if (TryComp<MetaDataComponent>(uid, out var meta))
        {
            if (meta.EntityPrototype?.ID == "VirtualItem" || 
                meta.EntityPrototype?.ID == "ActionDummy" ||
                meta.EntityName == "VIRTUAL ITEM YOU SHOULD NOT SEE THIS")
            {
                _cache[uid] = results;
                return results;
            }
        }

        foreach (var category in _registry.Categories)
        {
            if (Matches(uid, category.Conditions))
            {
                var text = ResolveText(uid, category.TextFormat);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add(new EspEvaluationResult { Category = category, RenderText = text });
                }
            }
        }

        _cache[uid] = results;
        return results;
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

    public string ResolveVariable(EntityUid uid, string name)
    {
        if (_customVariables.TryGetValue(name, out var resolver))
            return resolver(uid);
        return string.Empty;
    }

    private string ResolveText(EntityUid uid, string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return string.Empty;
        
        var result = format;
        foreach (var kvp in _customVariables)
        {
            var token = "{" + kvp.Key + "}";
            if (result.Contains(token))
            {
                result = result.Replace(token, kvp.Value(uid));
            }
        }

        // Resolve EntityName implicitly if not explicitly mapped as custom rule for simplicity,
        // though we mapped it in Initialize.
        return result.Trim();
    }
}
