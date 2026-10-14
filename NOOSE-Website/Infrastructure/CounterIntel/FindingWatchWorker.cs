using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.CounterIntel;

/// <summary>Background worker that evaluates the counter-intel rules and NOOSEI anomalies and reports new findings.</summary>
public sealed class FindingWatchWorker(IServiceScopeFactory scopeFactory, ILogger<FindingWatchWorker> logger)
    : BackgroundService
{
    // one pass costs about one cockpit visit
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // after the other workers' first pass
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
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
                var watch = scope.ServiceProvider.GetRequiredService<IFindingWatchService>();
                var reported = await watch.RunAsync(DateTime.UtcNow, stoppingToken);
                if (reported > 0)
                {
                    logger.LogInformation("Finding watch: {Count} new findings reported.", reported);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Finding watch run failed.");
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
