using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure.Chat;

namespace NOOSE_Website.Infrastructure.Operations;

/// <summary>Stamps emergency alarms past their expiry as closed and tells the live views.</summary>
/// <remarks>Not a security control: every read already treats <c>Expiry &lt;= now</c> as closed, so a late or dead
/// worker only leaves the stamp missing. It has no actor and writes no audit row, like the other stamp workers.</remarks>
public sealed class EmergencyAlarmExpiryWorker(IServiceScopeFactory scopeFactory, EmergencyAlarmBroadcaster broadcaster,
    ILogger<EmergencyAlarmExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

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
                var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var closed = await SweepAsync(db, DateTime.UtcNow, broadcaster, stoppingToken);
                if (closed > 0)
                {
                    logger.LogInformation("Einsatzalarme abgelaufen: {Count}.", closed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Einsatzalarm-Ablaufprüfung fehlgeschlagen.");
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

    /// <summary>Closes each alarm past its expiry at that expiry; returns how many this call closed.</summary>
    public static async Task<int> SweepAsync(AppDbContext db, DateTime nowUtc, EmergencyAlarmBroadcaster broadcaster,
        CancellationToken cancellationToken)
    {
        var due = await db.EmergencyAlarms.AsNoTracking()
            .Where(a => a.EndedAt == null && a.Expiry <= nowUtc)
            .OrderBy(a => a.Expiry)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        var closed = 0;
        foreach (var id in due)
        {
            // claim per id: a hand-end in between wins and stays unreported
            var claimed = await db.EmergencyAlarms
                .Where(a => a.Id == id && a.EndedAt == null && a.Expiry <= nowUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.EndedAt, a => a.Expiry)
                    .SetProperty(a => a.EndedById, (string?)null), cancellationToken);
            if (claimed == 1)
            {
                closed++;
                broadcaster.Report(id);
            }
        }
        return closed;
    }
}
