using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Infrastructure.Usage;

/// <summary>Writes the in-memory usage counters about once a minute and deletes expired rows once an hour.</summary>
public sealed class UsageStatsWorker(IServiceScopeFactory scopeFactory, ILogger<UsageStatsWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    private DateTime _lastPurge = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // keep the last minute
        await RunOnceAsync(cancellationToken, purge: false);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken, bool purge = true)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IUsageStore>();
            await store.FlushAsync(cancellationToken);
            if (purge && DateTime.UtcNow - _lastPurge >= PurgeEvery)
            {
                _lastPurge = DateTime.UtcNow;
                var removed = await store.PurgeAsync(DateTime.UtcNow, cancellationToken);
                if (removed > 0)
                {
                    logger.LogInformation("Usage statistics: {Count} expired rows deleted.", removed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Usage statistics write failed.");
        }
    }
}
