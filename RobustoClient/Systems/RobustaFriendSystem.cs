using RobustoClient.Components;
using Content.Client.Administration.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using Robust.Shared.Player;

namespace RobustoClient.Systems;

public class RobustaFriendSystem : EntitySystem
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
        if (HasComp<RobustaFriendComponent>(ev.Target))
            verb = new Verb
            {
                Text = "Unfriend",
                Act = () => RemComp<RobustaFriendComponent>(ev.Target),
                ClientExclusive = true
            };
        else
            verb = new Verb
            {
                Text = "Friend",
                Act = () => AddComp(ev.Target, new RobustaFriendComponent { NetSyncEnabled = false }),
                ClientExclusive = true
            };
        ev.Verbs.Add(verb);
    }

    public bool IsFriend(Entity<ActorComponent?> ent)
    {
        if (HasComp<RobustaFriendComponent>(ent))
            return true;
            
        // Pass 'false' as the third argument to disable console error spam
        // if it's an NPC or dummy without ActorComponent.
        Resolve(ent, ref ent.Comp, false);
        
        if (RobustaConfig.FriendsSet.Contains((ent.Comp?.PlayerSession.Name ?? GetUsername(ent)) ?? string.Empty))
            return true;
            
        return false;
    }

    private string? GetUsername(EntityUid uid)
    {
        var netEntity = GetNetEntity(uid);
        return _adminSystem.PlayerList.FirstOrDefault(player => player.NetEntity == netEntity)?.Username;
    }
}