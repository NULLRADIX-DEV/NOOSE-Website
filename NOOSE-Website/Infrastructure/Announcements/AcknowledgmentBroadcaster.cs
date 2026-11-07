namespace NOOSE_Website.Infrastructure.Announcements;

/// <summary>Process-wide in-memory broadcaster for the board and directive badges; recipient-specific so only the affected agent recounts.</summary>
public sealed class AcknowledgmentBroadcaster
{
    /// <summary>Fired with the affected agent id when its open-acknowledgment count changes.</summary>
    public event Action<string>? Modified;

    public void Report(string agentId) => Modified?.Invoke(agentId);
}
