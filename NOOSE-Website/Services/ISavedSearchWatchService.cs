namespace NOOSE_Website.Services;

/// <summary>Reruns watched saved searches in their owner's name and reports hits the previous run did not have.</summary>
public interface ISavedSearchWatchService
{
    /// <summary>Runs the watched searches that are due, stalest first, one after another.</summary>
    /// <returns>How many of them reported new hits.</returns>
    Task<int> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
