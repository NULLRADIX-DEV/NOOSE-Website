using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Notifications;

/// <summary>Sends record-changed notifications to active followers, deduplicated and recipient-visibility-checked.</summary>
public sealed class WatchlistFanout(IDbContextFactory<AppDbContext> dbFactory, INotificationService notifications)
{
    public async Task ProcessAsync(string? actorId, IReadOnlyCollection<(string Type, string Id)> records,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var resolved = await RecordsReference.ResolveAsync(db, records, cancellationToken);

        foreach (var (type, id) in records.Distinct())
        {
            if (!resolved.TryGetValue((type, id), out var aufl))
            {
                continue;
            }

            var followerIds = await db.Watchlists
                .Where(w => w.EntityType == type && w.EntityId == id)
                .Select(w => w.AgentId).Distinct().ToListAsync(cancellationToken);
            if (actorId is not null)
            {
                followerIds.Remove(actorId);
            }
            if (followerIds.Count == 0)
            {
                continue;
            }

            var follower = await db.Users
                .Where(u => followerIds.Contains(u.Id) && u.Status == AgentStatus.Active)
                .Select(u => new { u.Id, u.IsAdmin, u.Rank })
                .ToListAsync(cancellationToken);
            if (follower.Count == 0)
            {
                continue;
            }

            var alreadyOpen = new HashSet<string>();
            if (!string.IsNullOrWhiteSpace(aufl.Href))
            {
                alreadyOpen = (await db.Notifications
                    .Where(n => n.Type == NotificationType.RecordModified && n.Href == aufl.Href
                                && n.ReadAt == null && followerIds.Contains(n.RecipientId))
                    .Select(n => n.RecipientId)
                    .ToListAsync(cancellationToken)).ToHashSet();
            }

            var title = $"„{aufl.Display}“ wurde aktualisiert.";
            foreach (var f in follower)
            {
                if (alreadyOpen.Contains(f.Id))
                {
                    continue;
                }
                var isLeadership = f.IsAdmin || f.Rank is >= Rank.SupervisorySpecialAgent;
                if (!await Visibility.IsRecordVisibleAsync(db, type, id, isLeadership, cancellationToken, f.Id))
                {
                    continue;
                }
                // the notice summarises the record, so a follower who silenced it keeps one read row per record
                await notifications.NotifyOnceAsync(f.Id, NotificationType.RecordModified, title, aufl.Href, cancellationToken);
            }
        }
    }
}
