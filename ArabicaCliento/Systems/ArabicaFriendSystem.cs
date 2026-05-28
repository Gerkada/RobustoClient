using ArabicaCliento.Components;
using Content.Client.Administration.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using Robust.Shared.Player;

namespace ArabicaCliento.Systems;

public class ArabicaFriendSystem : EntitySystem
{
    [Dependency] private readonly AdminSystem _adminSystem = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<GetVerbsEvent<Verb>>(AddFriendVerb);
    }

    private void AddFriendVerb(GetVerbsEvent<Verb> ev)
    {
        if (!HasComp<MobStateComponent>(ev.Target))
            return;
        Verb verb;
        if (HasComp<ArabicaFriendComponent>(ev.Target))
            verb = new Verb
            {
                Text = "Unfriend",
                Act = () => RemComp<ArabicaFriendComponent>(ev.Target),
                ClientExclusive = true
            };
        else
            verb = new Verb
            {
                Text = "Friend",
                Act = () => AddComp(ev.Target, new ArabicaFriendComponent { NetSyncEnabled = false }),
                ClientExclusive = true
            };
        ev.Verbs.Add(verb);
    }

    public bool IsFriend(Entity<ActorComponent?> ent)
    {
        if (HasComp<ArabicaFriendComponent>(ent))
            return true;
            
        // Передаем 'false' третьим аргументом, чтобы отключить спам ошибок в консоль,
        // если это NPC или манекен без ActorComponent.
        Resolve(ent, ref ent.Comp, false);
        
        if (ArabicaConfig.FriendsSet.Contains((ent.Comp?.PlayerSession.Name ?? GetUsername(ent)) ?? string.Empty))
            return true;
            
        return false;
    }

    private string? GetUsername(EntityUid uid)
    {
        var netEntity = GetNetEntity(uid);
        return _adminSystem.PlayerList.FirstOrDefault(player => player.NetEntity == netEntity)?.Username;
    }
}