namespace NOOSE_Website.Infrastructure.Chat;

/// <summary>Process-wide in-memory broadcaster for live taskforce-chat updates.</summary>
public sealed class TaskforceChatBroadcaster
{
    // (taskforce, agent) -> open chat panels; several tabs count separately
    private readonly Dictionary<(string TaskforceId, string AgentId), int> _watching = new();
    private readonly Lock _gate = new();

    /// <summary>Fired with the affected taskforce id when its chat changes.</summary>
    public event Action<string>? Modified;

    public void Report(string taskforceId) => Modified?.Invoke(taskforceId);

    /// <summary>An agent has the chat on screen; pair every call with <see cref="Leave"/>.</summary>
    public void Enter(string taskforceId, string agentId)
    {
        lock (_gate)
        {
            _watching[(taskforceId, agentId)] = _watching.GetValueOrDefault((taskforceId, agentId)) + 1;
        }
    }

    public void Leave(string taskforceId, string agentId)
    {
        lock (_gate)
        {
            var key = (taskforceId, agentId);
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
    public bool IsWatching(string taskforceId, string agentId)
    {
        lock (_gate)
        {
            return _watching.ContainsKey((taskforceId, agentId));
        }
    }
}
