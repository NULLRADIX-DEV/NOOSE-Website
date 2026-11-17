namespace NOOSE_Website.Infrastructure.Chat;

/// <summary>Process-wide in-memory broadcaster for one kind of chat room, keyed by room id.</summary>
public abstract class ChatRoomBroadcaster
{
    // (room, agent) -> open chat panels; several tabs count separately
    private readonly Dictionary<(string RoomId, string AgentId), int> _watching = new();
    private readonly Lock _gate = new();

    /// <summary>Fired with the affected room id when its chat changes.</summary>
    public event Action<string>? Modified;

    public void Report(string roomId) => Modified?.Invoke(roomId);

    /// <summary>An agent has the chat on screen; pair every call with <see cref="Leave"/>.</summary>
    public void Enter(string roomId, string agentId)
    {
        lock (_gate)
        {
            _watching[(roomId, agentId)] = _watching.GetValueOrDefault((roomId, agentId)) + 1;
        }
    }

    public void Leave(string roomId, string agentId)
    {
        lock (_gate)
        {
            var key = (roomId, agentId);
            if (_watching.TryGetValue(key, out var open) && open > 1)
            {
                _watching[key] = open - 1;
            }
            else
            {
                _watching.Remove(key);
            }
        }
    }

    /// <summary>Whether the agent has this chat open right now.</summary>
    public bool IsWatching(string roomId, string agentId)
    {
        lock (_gate)
        {
            return _watching.ContainsKey((roomId, agentId));
        }
    }
}

/// <summary>Live updates of the taskforce chats; the room is the taskforce id.</summary>
public sealed class TaskforceChatBroadcaster : ChatRoomBroadcaster;

/// <summary>Live updates of the one Administration chat; the room is the workspace id.</summary>
public sealed class AdministrationChatBroadcaster : ChatRoomBroadcaster
{
    /// <summary>Fired with the agent id whose read mark moved; kept apart from Modified, which the chat panel answers by marking read.</summary>
    public event Action<string>? ReadMarked;

    public void ReportRead(string agentId) => ReadMarked?.Invoke(agentId);
}
