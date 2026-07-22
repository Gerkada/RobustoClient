using Content.Client.Administration.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Verbs;
using Robust.Shared.Player;

namespace RobustoClient.Systems;

public class RobustaFriendSystem : EntitySystem
{
    [Dependency] private readonly AdminSystem _adminSystem = default!;
    
    // Local cache for friends to completely avoid component serialization crashes
    private readonly HashSet<EntityUid> _friends = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GetVerbsEvent<Verb>>(AddFriendVerb);
    }

    private void AddFriendVerb(GetVerbsEvent<Verb> ev)
    {
        if (!HasComp<MobStateComponent>(ev.Target))
            return;

        Verb verb;
        if (_friends.Contains(ev.Target))
        {
            verb = new Verb
            {
                Text = "Unfriend",
                Act = () => _friends.Remove(ev.Target),
                ClientExclusive = true
            };
        }
        else
        {
            verb = new Verb
            {
                Text = "Friend",
                Act = () => _friends.Add(ev.Target),
                ClientExclusive = true
            };
        }
        ev.Verbs.Add(verb);
    }

    public bool IsFriend(Entity<ActorComponent?> ent)
    {
        // Check local friend cache first
        if (_friends.Contains(ent))
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