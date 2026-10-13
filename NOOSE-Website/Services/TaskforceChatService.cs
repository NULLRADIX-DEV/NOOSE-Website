using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Taskforces;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ITaskforceChatService" />
public class TaskforceChatService(IDbContextFactory<AppDbContext> dbFactory, TaskforceChatBroadcaster broadcaster,
    INotificationService notifications) : ITaskforceChatService
{
    public async Task<List<TaskforceMessage>> GetMessagesAsync(string taskforceId, ViewerScope scope, int limit = 100, DateTime? olderAs = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // partners read the chat on a taskforce released to them
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), taskforceId, scope, cancellationToken))
        {
            return new();
        }

        var query = db.TaskforceMessages.Where(n => n.TaskforceId == taskforceId);
        if (olderAs is not null)
        {
            query = query.Where(n => n.CreatedAt < olderAs.Value);
        }
        // newest first, then reverse to chronological
        var latest = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        latest.Reverse();
        return latest;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A member without a read mark counts from joining, so a newcomer is not handed the whole history. Accounts that
    /// cannot set a mark get no counter at all: one they could never clear would only be noise.
    /// </remarks>
    public async Task<List<TaskforceActivity>> GetUnreadAsync(ClaimsPrincipal actor, bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId) || !actor.MayWrite())
        {
            return new();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // joined against the filtered set, so a trashed taskforce drops out
        var mine = await (
                from ta in db.TaskforceAgents
                join t in db.Taskforces on ta.TaskforceId equals t.Id
                where ta.AgentId == meId && (includeArchived || !t.IsArchived)
                select new { t.Id, t.Name })
            .ToListAsync(cancellationToken);
        var visible = await TaskforceVisibility.VisibleIdsAsync(db, mine.Select(t => t.Id).ToList(),
            actor.MayAllTaskforcesSee(), meId, cancellationToken);
        var names = mine.Where(t => visible.Contains(t.Id)).ToDictionary(t => t.Id, t => t.Name);
        if (names.Count == 0)
        {
            return new();
        }

        var ids = names.Keys.ToList();
        // flat rows, grouped here: unread is tens of messages, not thousands
        var messages = await (
                from ta in db.TaskforceAgents
                where ta.AgentId == meId && ids.Contains(ta.TaskforceId)
                join n in db.TaskforceMessages on ta.TaskforceId equals n.TaskforceId
                where n.CreatedAt > (ta.LastReadAt ?? ta.CreatedAt) && n.CreatedById != meId
                select new { n.TaskforceId, n.AuthorName, n.CreatedAt })
            .ToListAsync(cancellationToken);
        return messages
            .GroupBy(n => n.TaskforceId)
            .Select(g =>
            {
                var last = g.MaxBy(n => n.CreatedAt)!;
                return new TaskforceActivity(g.Key, names[g.Key], g.Count(), last.CreatedAt, last.AuthorName);
            })
            .OrderByDescending(a => a.LastAtUtc)
            .ToList();
    }

    public async Task<TaskforceReadState> GetReadStateAsync(string taskforceId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId) || !actor.MayWrite())
        {
            return TaskforceReadState.None;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // same predicate as GetUnreadAsync, or list and chat would disagree
        var since = await db.TaskforceAgents
            .Where(ta => ta.TaskforceId == taskforceId && ta.AgentId == meId)
            .Select(ta => (DateTime?)(ta.LastReadAt ?? ta.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        if (since is null
            || !await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), taskforceId, ViewerScope.From(actor), cancellationToken))
        {
            return TaskforceReadState.None;
        }
        var unread = await db.TaskforceMessages
            .CountAsync(n => n.TaskforceId == taskforceId && n.CreatedAt > since && n.CreatedById != meId, cancellationToken);
        return new TaskforceReadState(since, unread);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ExecuteUpdate on the caller's own membership row: no audit line and no modified stamp for having read, and the
    /// read-only barrier never sees it. That is why the write gate is checked here, and quietly, like the ticket marks.
    /// </remarks>
    public async Task MarkReadAsync(string taskforceId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId) || !actor.MayWrite())
        {
            return;
        }

        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // forward only: a late write from a second tab must not bring messages back
        await db.TaskforceAgents
            .Where(ta => ta.TaskforceId == taskforceId && ta.AgentId == meId && (ta.LastReadAt == null || ta.LastReadAt < now))
            .ExecuteUpdateAsync(s => s.SetProperty(ta => ta.LastReadAt, now), cancellationToken);
    }

    public async Task<TaskforceMessage> SendAsync(string taskforceId, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var content = text?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // partners may post on a taskforce released to them; the visibility check gates access
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), taskforceId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Taskforce ist für dich nicht zugänglich.");
        }

        var message = new TaskforceMessage
        {
            TaskforceId = taskforceId,
            Text = content,
            AuthorName = actor.GetCodename(),
        };
        db.TaskforceMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(taskforceId);

        try
        {
            var who = string.IsNullOrWhiteSpace(actor.GetCodename()) ? "Ein Agent" : actor.GetCodename();
            await notifications.NotifyMentionedAsync(content, $"{who} hat dich im Taskforce-Chat erwähnt.",
                $"/taskforces/{taskforceId}", nameof(Taskforce), taskforceId, actor, cancellationToken);
        }
        catch { /* best effort */ }

        return message;
    }

    public async Task DeleteAsync(string messageId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // partners may post but not retract messages
        if (actor.IsPartner())
        {
            throw new UnauthorizedAccessException("Partner können Nachrichten nicht zurückziehen.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.TaskforceMessages.FirstOrDefaultAsync(n => n.Id == messageId, cancellationToken);
        if (message is null)
        {
            return;
        }
        if (!actor.IsLeadership() && message.CreatedById != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Nur der Autor oder die Führung kann eine Nachricht zurückziehen.");
        }
        db.TaskforceMessages.Remove(message);
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(message.TaskforceId);
    }
}
