namespace NOOSE_Website.Services.Usage;

/// <summary>Writes the in-memory usage counters to the database and drops expired rows. Called by the worker only.</summary>
/// <remarks>No actor, guard or audit, like the other stamp workers; the rows carry no agent.</remarks>
public interface IUsageStore
{
    /// <summary>Writes what the collector gained since the last call; a failure leaves it queued for the next one.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes counter rows older than <see cref="UsageRules.RetentionDays"/>; returns how many.</summary>
    Task<int> PurgeAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
