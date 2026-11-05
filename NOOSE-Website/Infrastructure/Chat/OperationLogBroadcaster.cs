namespace NOOSE_Website.Infrastructure.Chat;

/// <summary>Process-wide in-memory broadcaster for live operation-log updates.</summary>
public sealed class OperationLogBroadcaster
{
    /// <summary>Fired with the affected operation id when its log changes.</summary>
    public event Action<string>? Modified;

    public void Report(string operationId) => Modified?.Invoke(operationId);
}
