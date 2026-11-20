using Robust.Shared.GameObjects;

namespace RobustoClient.Systems.PlayerState;

/// <summary>
/// Latches the verdict of a <see cref="PlayerFlagDefinition.RoundRetained"/> flag for the round. Once
/// a retained flag fires it keeps answering from the latched result, even after the evidence leaves
/// PVS or is dropped, which is what the clone, thief and revolutionary rules depend on.
/// </summary>
internal sealed class FlagRetentionStore
{
    private readonly Dictionary<EntityUid, Dictionary<string, PlayerFlagResult>> _latched = new();

    public PlayerFlagResult Apply(EntityUid entity, PlayerFlagDefinition definition, PlayerFlagResult result)
    {
        if (!definition.RoundRetained)
            return result;

        if (result.Triggered)
        {
            if (!_latched.TryGetValue(entity, out var byFlag))
                _latched[entity] = byFlag = new Dictionary<string, PlayerFlagResult>(StringComparer.Ordinal);

            byFlag[definition.Flag.Id] = result;
            return result;
        }

        return _latched.TryGetValue(entity, out var retained) &&
               retained.TryGetValue(definition.Flag.Id, out var kept)
            ? kept
            : result;
    }

    public void Clear() => _latched.Clear();
}
