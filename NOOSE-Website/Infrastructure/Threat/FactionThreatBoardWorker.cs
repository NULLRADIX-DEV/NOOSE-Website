using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Threat;

/// <summary>Keeps the Discord threat board current; edits only when the list changed.</summary>
public sealed class FactionThreatBoardWorker(IServiceScopeFactory scopeFactory, ILogger<FactionThreatBoardWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    // an unchanged board is never touched; an hourly edit renews its stamp and notices one deleted in Discord
    private const int ForceEveryTicks = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        var tick = 0;
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IFactionThreatBoardService>();
                var outcome = await service.RunAsync(force: tick % ForceEveryTicks == 0, stoppingToken);
                if (outcome == ThreatBoardOutcome.Failed)
                {
                    logger.LogWarning("Threat board not delivered; retrying on the next pass.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Threat board pass failed.");
            }
            tick++;
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
