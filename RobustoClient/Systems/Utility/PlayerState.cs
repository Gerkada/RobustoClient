using Content.Shared.Mind.Components;

namespace RobustoClient.Systems.Utility;

/// <summary>
/// Player-state queries shared by overlays, systems and UI code.
/// </summary>
/// <remarks>
/// Component presence alone does not say whether an entity is player controlled: every mob inherits
/// <see cref="MindContainerComponent"/> from BaseControllable, mind attached or not.
/// </remarks>
public static class PlayerState
{
    /// <summary>Whether a mind is attached to the entity, which is what marks it player controlled.</summary>
    public static bool HasMind(IEntityManager entMan, EntityUid uid) =>
        entMan.TryGetComponent<MindContainerComponent>(uid, out var mind) && mind.HasMind;
}