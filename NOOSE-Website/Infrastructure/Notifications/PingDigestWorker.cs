using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Notifications;

/// <summary>Background worker that sends the daily ping digest once the digest hour has passed.</summary>
public sealed class PingDigestWorker(IServiceScopeFactory scopeFactory, ILogger<PingDigestWorker> logger)
    : BackgroundService
{
    // the digest is due once a day; a quarter hour late is fine
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
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
                var digest = scope.ServiceProvider.GetRequiredService<IPingDigestService>();
                var listed = await digest.RunDueAsync(DateTime.UtcNow, stoppingToken);
                if (listed > 0)
                {
                    logger.LogInformation("Ping digest sent ({Agents} agents listed).", listed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ping digest failed.");
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
