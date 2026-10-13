using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Announcements;

/// <summary>Background worker that reminds whoever still owes an acknowledgment once its deadline passed.</summary>
public sealed class AcknowledgmentReminderWorker(IServiceScopeFactory scopeFactory, ILogger<AcknowledgmentReminderWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
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
                var board = scope.ServiceProvider.GetRequiredService<IAnnouncementService>();
                var sent = await board.RemindOverdueAsync(DateTime.UtcNow, stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Acknowledgment reminders: {Count} sent.", sent);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Acknowledgment reminder run failed.");
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
