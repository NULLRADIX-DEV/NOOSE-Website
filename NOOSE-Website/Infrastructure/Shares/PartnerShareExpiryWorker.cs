using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Shares;

/// <summary>Revokes partner releases past their end date and logs it on the record.</summary>
/// <remarks>Not a security control: the read gate (<c>PartnerVisibility.ActiveShares</c>) already ignores an expired
/// release by date, so a dead, late or doubled worker leaks nothing. It only keeps the books honest, which is why
/// nobody may drop the date filter from the read path because this worker exists. Holds no DbContext on purpose.</remarks>
public sealed class PartnerShareExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<PartnerShareExpiryWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // after the migrations and the seeders
            await Task.Delay(TimeSpan.FromSeconds(55), stoppingToken);
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
                var service = scope.ServiceProvider.GetRequiredService<IPartnerShareExpiryService>();
                var expired = await service.ExpireDueAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Partner release expiry: {Anzahl} releases revoked.", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Partner release expiry sweep failed.");
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
