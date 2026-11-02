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
        var now = DateTime.UtcNow;

        var due = await db.Followups
            .Where(w => !w.Done && w.DueAt <= now && w.NotifiedAt == null)
            .OrderBy(w => w.DueAt)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return;
        }

        var refs = due.Select(w => (w.EntityType, w.EntityId)).Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken);

        foreach (var w in due)
        {
            if (!resolved.TryGetValue((w.EntityType, w.EntityId), out var record))
            {
                w.NotifiedAt = now;
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

            if (recipientIds.Count > 0)
            {
                var active = await db.Users
                    .Where(u => recipientIds.Contains(u.Id) && u.Status == AgentStatus.Active)
                    .Select(u => new { u.Id, u.IsAdmin, u.Rank, u.PartnerAgency })
                    .ToListAsync(cancellationToken);
                var allowed = new List<string>();
                foreach (var u in active)
                {
                    var viewer = FollowupService.ReminderScope(u.Id, u.IsAdmin, u.Rank, u.PartnerAgency);
                    if (await Visibility.IsRecordVisibleAsync(db, w.EntityType, w.EntityId, viewer, cancellationToken))
                    {
                        allowed.Add(u.Id);
                    }
                }

                if (allowed.Count > 0)
                {
                    var title = BuildTitle(record.Display, w.Note);
                    await notifications.NotifyManyAsync(allowed, NotificationType.Followup, title, record.Href,
                        triggerId: null, cancellationToken);
                }
            }

            w.NotifiedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

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
