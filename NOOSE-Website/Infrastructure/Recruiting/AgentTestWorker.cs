using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Recruiting;

/// <summary>Hands in internal-test attempts whose clock ran out and sends the due-date reminders.</summary>
/// <remarks>Not a security control: the open, draft and submit paths re-check the deadline themselves, so a dead or
/// doubled worker grants no extra time. Holds no DbContext; the rules and the claims live in the service.</remarks>
public sealed class AgentTestWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentTestWorker> logger) : BackgroundService
{
    // the attempt clock is configured in minutes, so the sweep has to be as fine
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // after the migrations and the seeders, and clear of the recruiting sweep's start
            await Task.Delay(TimeSpan.FromSeconds(95), stoppingToken);
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
                var service = scope.ServiceProvider.GetRequiredService<IAgentTestExpiryService>();
                var expired = await service.ExpireDueAsync(stoppingToken);
                var reminded = await service.RemindDueAsync(stoppingToken);
                if (expired > 0 || reminded > 0)
                {
                    logger.LogInformation("Internal tests: {Expired} attempts handed in, {Reminded} reminders sent.",
                        expired, reminded);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Internal test sweep failed.");
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
