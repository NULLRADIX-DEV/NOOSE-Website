using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Followups;

/// <summary>Notifies assignees and followers of due follow-ups and escalates long-overdue ones to leadership.</summary>
public sealed class FollowupDueWorker(IServiceScopeFactory scopeFactory, ILogger<FollowupDueWorker> logger)
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
                await ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wiedervorlage-Fälligkeitsprüfung fehlgeschlagen.");
            }

            // independent of the due run
            try
            {
                using var scope = scopeFactory.CreateScope();
                var followups = scope.ServiceProvider.GetRequiredService<IFollowupService>();
                var escalated = await followups.EscalateOverdueAsync(DateTime.UtcNow, stoppingToken);
                if (escalated > 0)
                {
                    logger.LogInformation("Wiedervorlagen eskaliert: {Count}.", escalated);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wiedervorlage-Eskalation fehlgeschlagen.");
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

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var watchlist = scope.ServiceProvider.GetRequiredService<IWatchlistService>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await NotifyDueAsync(db, notifications, watchlist, DateTime.UtcNow, cancellationToken);
    }

    /// <summary>Claims each due reminder, then notifies the responsible and followers who may see the record.</summary>
    public static async Task<int> NotifyDueAsync(AppDbContext db, INotificationService notifications,
        IWatchlistService watchlist, DateTime now, CancellationToken cancellationToken)
    {
        var due = await db.Followups.AsNoTracking()
            .Where(w => !w.Done && w.DueAt <= now && w.NotifiedAt == null)
            .OrderBy(w => w.DueAt)
            .Select(w => new { w.Id, w.EntityType, w.EntityId, w.ResponsibleAgentId, w.Note })
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var refs = due.Select(w => (w.EntityType, w.EntityId)).Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken);

        var notified = 0;
        foreach (var w in due)
        {
            // rest waits for next run
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (!resolved.TryGetValue((w.EntityType, w.EntityId), out var record))
            {
                // trashed parent: stamp silently
                await ClaimAsync(db, w.Id, now);
                continue;
            }

            var recipientIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(w.ResponsibleAgentId))
            {
                recipientIds.Add(w.ResponsibleAgentId);
            }
            foreach (var followerId in await watchlist.GetFollowerIdsAsync(w.EntityType, w.EntityId, cancellationToken))
            {
                recipientIds.Add(followerId);
            }

            var allowed = new List<string>();
            if (recipientIds.Count > 0)
            {
                var active = await db.Users
                    .Where(u => recipientIds.Contains(u.Id) && u.Status == AgentStatus.Active)
                    .Select(u => new { u.Id, u.IsAdmin, u.Rank, u.PartnerAgency })
                    .ToListAsync(cancellationToken);
                foreach (var u in active)
                {
                    var viewer = FollowupService.ReminderScope(u.Id, u.IsAdmin, u.Rank, u.PartnerAgency);
                    if (await Visibility.IsRecordVisibleAsync(db, w.EntityType, w.EntityId, viewer, cancellationToken))
                    {
                        allowed.Add(u.Id);
                    }
                }
            }

            // claim; a postpone wins
            if (!await ClaimAsync(db, w.Id, now) || allowed.Count == 0)
            {
                continue;
            }
            await notifications.NotifyManyAsync(allowed, NotificationType.Followup, BuildTitle(record.Display, w.Note),
                record.Href, triggerId: null, CancellationToken.None);
            notified++;
        }
        return notified;
    }

    /// <summary>Stamps the reminder only while the row is still open, unstamped and due.</summary>
    private static async Task<bool> ClaimAsync(AppDbContext db, string id, DateTime now)
        => await db.Followups
            .Where(x => x.Id == id && !x.Done && x.NotifiedAt == null && x.DueAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NotifiedAt, now), CancellationToken.None) == 1;

    private static string BuildTitle(string display, string? note)
    {
        var title = $"Wiedervorlage fällig: „{display}“";
        if (!string.IsNullOrWhiteSpace(note))
        {
            title += $" – {note.Trim()}";
        }
        return title.Length > 300 ? title[..297] + "…" : title;
    }
}
