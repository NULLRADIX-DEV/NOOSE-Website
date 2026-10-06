using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Notifications;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="INotificationService" />
public class NotificationService(
    IDbContextFactory<AppDbContext> dbFactory, NotificationBroadcaster broadcaster, IDiscordWebhookService discord)
    : INotificationService
{
    public async Task NotifyAsync(string? recipientId, NotificationType type, string title, string? href,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientId))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Notifications.Add(new Notification
        {
            RecipientId = recipientId,
            Type = type,
            Title = title,
            Href = href,
        });
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(recipientId);
    }

    public async Task NotifyOnceAsync(string? recipientId, NotificationType type, string title, string? href,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientId))
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await TryFoldAsync(db, recipientId, type, title, href, cancellationToken))
        {
            db.Notifications.Add(new Notification
            {
                RecipientId = recipientId,
                Type = type,
                Title = title,
                Href = href,
            });
        }
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(recipientId);
    }

    /// <summary>Refreshes the recipient's still-unread notice for the same target; false when there is none to fold onto.</summary>
    /// <remarks>A thread that runs for a while must not fill the bell line by line; the unread notice IS the summary.</remarks>
    private static async Task<bool> TryFoldAsync(AppDbContext db, string recipientId, NotificationType type,
        string title, string? href, CancellationToken cancellationToken)
    {
        // without a target there is no identity to fold on
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        var open = await db.Notifications
            .Where(n => n.RecipientId == recipientId && n.Type == type && n.Href == href && n.ReadAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is null)
        {
            return false;
        }

        open.Title = title;
        // the bell sorts by CreatedAt, so an unbumped fold would stay buried under newer rows;
        // the audit interceptor stamps ModifiedAt on an update and leaves CreatedAt alone
        open.CreatedAt = DateTime.UtcNow;
        return true;
    }

    public Task NotifyMentionedAsync(string? text, string title, string? href, string targetType, string targetId,
        ClaimsPrincipal trigger, CancellationToken cancellationToken = default, string? childType = null)
        => FanOutMentionsAsync(MentionedAgentIds(text, trigger), title, href, targetType, targetId, childType, cancellationToken);

    public Task NotifyMentionedDeltaAsync(string? oldText, string? newText, string title, string? href,
        string targetType, string targetId, ClaimsPrincipal trigger, CancellationToken cancellationToken = default, string? childType = null)
    {
        // already-mentioned agents were pinged on the earlier save
        var known = MentionedAgentIds(oldText, trigger).ToHashSet(StringComparer.Ordinal);
        var added = MentionedAgentIds(newText, trigger).Where(id => !known.Contains(id)).ToList();
        return FanOutMentionsAsync(added, title, href, targetType, targetId, childType, cancellationToken);
    }

    /// <summary>Agent ids mentioned in the text, without the trigger, deduplicated.</summary>
    private static List<string> MentionedAgentIds(string? text, ClaimsPrincipal trigger)
    {
        var triggerId = trigger.GetAgentId();
        return MentionParser.Parse(text)
            .Where(t => t.Type == nameof(Agent) && t.Id != triggerId)
            .Select(t => t.Id)
            .Distinct()
            .ToList();
    }

    private async Task FanOutMentionsAsync(IReadOnlyList<string> agentIds, string title, string? href,
        string targetType, string targetId, string? childType, CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // active recipients only
        var recipient = await db.Users
            .Where(u => agentIds.Contains(u.Id) && u.Status == AgentStatus.Active)
            .Select(u => new { u.Id, u.IsAdmin, u.Rank, u.PartnerAgency })
            .ToListAsync(cancellationToken);

        var notified = new List<string>();
        foreach (var e in recipient)
        {
            // gate on recipient's own visibility, not the trigger's (no record/classification leak)
            if (e.PartnerAgency is { } agency)
            {
                // child release unknown
                if (childType is not null
                    || !await Visibility.IsRecordVisibleAsync(db, targetType, targetId,
                        new ViewerScope(false, false, e.Id, agency), cancellationToken))
                {
                    continue;
                }
            }
            else
            {
                var recipientIsLeadership = e.IsAdmin || e.Rank is >= Rank.SupervisorySpecialAgent;
                if (!await Visibility.IsRecordVisibleAsync(db, targetType, targetId, recipientIsLeadership, cancellationToken, e.Id))
                {
                    continue;
                }
            }
            db.Notifications.Add(new Notification
            {
                RecipientId = e.Id,
                Type = NotificationType.Mention,
                Title = title,
                Href = href,
            });
            notified.Add(e.Id);
        }

        if (notified.Count == 0)
        {
            return;
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var id in notified)
        {
            broadcaster.Report(id);
        }

        // one channel post per mention event; pings the mentioned recipients directly
        await discord.PushAsync(NotificationType.Mention, href, notified, cancellationToken: cancellationToken);
    }

    public async Task NotifyManyAsync(IReadOnlyCollection<string> recipientIds, NotificationType type,
        string title, string? href, string? triggerId, CancellationToken cancellationToken = default)
    {
        // exclude trigger, dedupe, drop empties
        var targets = recipientIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != triggerId)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var id in targets)
        {
            db.Notifications.Add(new Notification
            {
                RecipientId = id,
                Type = type,
                Title = title,
                Href = href,
            });
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var id in targets)
        {
            broadcaster.Report(id);
        }

        // one channel post per broadcast event; role categories ping their role, personal categories ping these recipients.
        // the in-app title is forwarded as an optional headline (used only for header-eligible categories when enabled)
        await discord.PushAsync(type, href, targets, title, cancellationToken);
    }

    public async Task NotifyManyOnceAsync(IReadOnlyCollection<string> recipientIds, NotificationType type,
        string title, string? href, string? triggerId, CancellationToken cancellationToken = default)
    {
        // exclude trigger, dedupe, drop empties
        var targets = recipientIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != triggerId)
            .Distinct()
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var fresh = new List<string>();
        foreach (var id in targets)
        {
            if (await TryFoldAsync(db, id, type, title, href, cancellationToken))
            {
                continue;
            }
            db.Notifications.Add(new Notification
            {
                RecipientId = id,
                Type = type,
                Title = title,
                Href = href,
            });
            fresh.Add(id);
        }
        await db.SaveChangesAsync(cancellationToken);

        // a fold changes the headline and the order, so every recipient's bell is refreshed
        foreach (var id in targets)
        {
            broadcaster.Report(id);
        }

        // a fold is a refresh of something already announced; only a genuinely new notice reaches the channel
        if (fresh.Count > 0)
        {
            await discord.PushAsync(type, href, fresh, title, cancellationToken);
        }
    }

    public async Task<List<Notification>> GetOwnAsync(ClaimsPrincipal actor, int max = 20, CancellationToken cancellationToken = default)
    {
        // recipient is always the caller — derive from principal, never a parameter
        var agentId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Notifications
            .Where(n => n.RecipientId == agentId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(max, 1, 100))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return 0;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Notifications
            .CountAsync(n => n.RecipientId == agentId && n.ReadAt == null, cancellationToken);
    }

    public async Task AsReadMarkAsync(string notificationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == notificationId, cancellationToken);
        // only own notification may be marked read
        if (n is null || n.RecipientId != agentId || n.ReadAt is not null)
        {
            return;
        }
        n.ReadAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(n.RecipientId);
    }

    public async Task AllAsReadAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var open = await db.Notifications
            .Where(n => n.RecipientId == agentId && n.ReadAt == null)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return;
        }
        var now = DateTime.UtcNow;
        foreach (var n in open)
        {
            n.ReadAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(agentId);
    }

    public async Task<NotificationInboxPage> GetInboxAsync(ClaimsPrincipal actor, NotificationInboxQuery query,
        CancellationToken cancellationToken = default)
    {
        // recipient is always the caller — derive from principal, never a parameter
        var agentId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return NotificationInboxPage.Empty;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = db.Notifications.Where(n => n.RecipientId == agentId);
        if (query.Types.Count > 0)
        {
            var types = query.Types.Distinct().ToList();
            rows = rows.Where(n => types.Contains(n.Type));
        }
        if (query.FromUtc is { } from)
        {
            rows = rows.Where(n => n.CreatedAt >= from);
        }
        if (query.ToUtc is { } to)
        {
            rows = rows.Where(n => n.CreatedAt < to);
        }
        if (query.OnlyUnread)
        {
            rows = rows.Where(n => n.ReadAt == null);
        }

        var total = await rows.CountAsync(cancellationToken);
        var size = Math.Clamp(query.PageSize, 10, 100);
        var pageCount = Math.Max(1, (total + size - 1) / size);
        // an old address or a shrunken filter must not land on an empty page
        var page = Math.Clamp(query.Page, 1, pageCount);

        var items = await rows
            .OrderByDescending(n => n.CreatedAt)
            // equal stamps (one broadcast) would otherwise swap between pages
            .ThenByDescending(n => n.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return new NotificationInboxPage(items, total, page, pageCount);
    }

    public async Task<List<NotificationTypeCount>> GetOwnTypesAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var counts = await db.Notifications
            .Where(n => n.RecipientId == agentId)
            .GroupBy(n => n.Type)
            .Select(g => new NotificationTypeCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
        return counts
            .OrderBy(c => NotificationTypeDisplay.Name(c.Type), StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task AsUnreadMarkAsync(string notificationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == notificationId, cancellationToken);
        // only own notification, and only one that is read
        if (n is null || n.RecipientId != agentId || n.ReadAt is null)
        {
            return;
        }
        n.ReadAt = null;
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(n.RecipientId);
    }
}
