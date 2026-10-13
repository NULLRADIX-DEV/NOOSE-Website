using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Search;

/// <summary>Background worker that reruns watched saved searches and reports their new hits.</summary>
public sealed class SavedSearchWatchWorker(IServiceScopeFactory scopeFactory, ILogger<SavedSearchWatchWorker> logger)
    : BackgroundService
{
    // each search runs at most hourly; the tick only decides how soon a newly watched one gets its baseline
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // after the index backfill and the other workers' first pass
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var watch = scope.ServiceProvider.GetRequiredService<ISavedSearchWatchService>();
                var reported = await watch.RunDueAsync(DateTime.UtcNow, stoppingToken);
                if (reported > 0)
                {
                    logger.LogInformation("Watched searches: {Count} reported new hits.", reported);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watched search run failed.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
