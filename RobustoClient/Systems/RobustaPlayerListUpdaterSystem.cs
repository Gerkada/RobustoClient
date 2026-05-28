using RobustoClient.Systems.Abstract;
using Content.Client.Chat.Managers;
using Robust.Client.Player;
using Robust.Shared.Network;
using Robust.Shared.Network.Messages;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace RobustoClient.Systems;
/*
public sealed class RobustaPlayerListUpdaterSystem : LocalPlayerSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly INetManager _net = default!;

    private static readonly TimeSpan UpdateTime = TimeSpan.FromSeconds(4);


    private TimeSpan? _nextUpdate;
    private IReadOnlyDictionary<NetUserId, ICommonSession>? _lastSessions = null;

    public override void Initialize()
    {
        base.Initialize();
    }

    private void OnPlayerListUpdated()
    {
        if (_lastSessions is null)
        {
            _lastSessions = _player.SessionsDict;
            return;
        }
        if (!RobustaConfig.LogPlayers)
        {
            _lastSessions = _player.SessionsDict;
            return;
        }
        var leaved = _lastSessions.Keys.Concat(_player.SessionsDict.Keys).Select(key => _lastSessions[key]);
        var joined = _player.SessionsDict.Keys.Concat(_lastSessions.Keys).Select(key => _player.SessionsDict[key]);
        _lastSessions = _player.SessionsDict;
        
        foreach (var leave in leaved)
        {
            MarseyLogger.Info($"LEAVED PLAYER: {leave.Name} with GUID {leave.UserId}");
        }

        foreach (var join in joined)
        {
            MarseyLogger.Info($"JOINED PLAYER: {join.Name} with GUID {join.UserId}");
        }
    }


    protected override void OnAttached(LocalPlayerAttachedEvent ev)
    {
        _nextUpdate = _timing.CurTime + UpdateTime;
        _player.PlayerListUpdated += OnPlayerListUpdated;
        _lastSessions = _player.SessionsDict;
        MarseyLogger.Info("Attach");
    }

    protected override void OnDetached(LocalPlayerDetachedEvent ev)
    {
        _player.PlayerListUpdated -= OnPlayerListUpdated;
        _nextUpdate = null;
        _lastSessions = null;
        MarseyLogger.Info("Detach");

    }

    public override void Update(float frameTime)
    {
        if (_nextUpdate is null)
            return;
        if (_timing.CurTime > _nextUpdate)
            return;
        MarseyLogger.Debug("SENDING");
        _net.ClientSendMessage(new MsgPlayerListReq());
        _nextUpdate = _timing.CurTime + UpdateTime;
    }
}
*/