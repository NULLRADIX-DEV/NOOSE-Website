namespace NOOSE_Website.Infrastructure.Chat;

/// <summary>Process-wide in-memory broadcaster for live emergency-alarm updates.</summary>
public sealed class EmergencyAlarmBroadcaster
{
    /// <summary>Fired with the affected alarm id when it is triggered, answered, re-pinged or ended.</summary>
    public event Action<string>? Modified;

    public void Report(string alarmId) => Modified?.Invoke(alarmId);
}
