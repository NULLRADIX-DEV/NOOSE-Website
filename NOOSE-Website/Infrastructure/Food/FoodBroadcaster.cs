namespace NOOSE_Website.Infrastructure.Food;

/// <summary>Process-wide in-memory broadcaster for the food badge; global because every decider recounts when any agent files a request.</summary>
public sealed class FoodBroadcaster
{
    /// <summary>Fired when the number of requests awaiting a decision may have changed.</summary>
    public event Action? Modified;

    public void Report() => Modified?.Invoke();
}
