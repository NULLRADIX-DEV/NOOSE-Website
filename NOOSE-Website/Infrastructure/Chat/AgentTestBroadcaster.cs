namespace NOOSE_Website.Infrastructure.Chat;

/// <summary>Process-wide in-memory broadcaster for live internal-test updates (taker and graders).</summary>
public sealed class AgentTestBroadcaster
{
    /// <summary>Fired with the assignment and its agent when an internal test changes state.</summary>
    public event Action<string, string>? Modified;

    public void Report(string assignmentId, string agentId) => Modified?.Invoke(assignmentId, agentId);
}
