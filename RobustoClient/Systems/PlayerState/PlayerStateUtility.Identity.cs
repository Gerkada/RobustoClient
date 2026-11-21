using Content.Shared.Access.Components;
using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>Identity block: who the entity is and how far away it is.</summary>
public sealed partial class PlayerStateUtility
{
    private IPlayerData BuildIdentity(EntityUid entity, PlayerFlagReport flags)
    {
        var hasCard = TryGetIdCard(entity, Context(entity), out var cardUid, out var card);

        return new IdentityData(
            entity,
            EntityName(entity),
            FindSessionName(entity),
            hasCard ? card.LocalizedJobTitle : string.Empty,
            hasCard ? cardUid : null,
            GetCommandRole(entity),
            flags.Has(PlayerFlag.HasMind),
            MeasureRange(entity));
    }

    /// <summary>Display name, or the uid when the entity is not a networked one.</summary>
    public string EntityName(EntityUid entity) =>
        EntityManager.GetComponentOrNull<MetaDataComponent>(entity)?.EntityName ?? entity.ToString();

    /// <summary>The session name of the player driving the entity, or empty when nobody does.</summary>
    public string FindSessionName(EntityUid entity)
    {
        foreach (var session in _player.SessionsDict.Values)
        {
            if (session.AttachedEntity == entity)
                return session.Name;
        }

        return string.Empty;
    }

    /// <summary>Distance from the local player, or null when there is no attached entity.</summary>
    public float? MeasureRange(EntityUid entity)
    {
        if (_player.LocalSession?.AttachedEntity is not { } local)
            return null;

        if (!TryComp<TransformComponent>(local, out var localXform) ||
            !TryComp<TransformComponent>(entity, out var entityXform))
        {
            return null;
        }

        var transforms = EntityManager.System<SharedTransformSystem>();
        return (transforms.GetWorldPosition(entityXform) - transforms.GetWorldPosition(localXform)).Length();
    }
}
