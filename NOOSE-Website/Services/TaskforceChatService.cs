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

    public async Task<List<TaskforceActivity>> GetActivitySinceAsync(ClaimsPrincipal actor, DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // own memberships only; an archived taskforce is history, not work
        var mine = await db.TaskforceAgents
            .Where(ta => ta.AgentId == meId && !ta.Taskforce!.IsArchived)
            .Select(ta => new { ta.TaskforceId, ta.Taskforce!.Name })
            .Distinct()
            .ToListAsync(cancellationToken);
        var scope = ViewerScope.From(actor);
        var names = new Dictionary<string, string>();
        foreach (var t in mine)
        {
            if (!names.ContainsKey(t.TaskforceId)
                && await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), t.TaskforceId, scope, cancellationToken))
            {
                names[t.TaskforceId] = t.Name;
            }
        }
        if (names.Count == 0)
        {
            return new();
        }

        var ids = names.Keys.ToList();
        // flat rows, grouped here: a window holds tens of messages, not thousands
        var messages = await db.TaskforceMessages
            .Where(n => ids.Contains(n.TaskforceId) && n.CreatedAt > sinceUtc && n.CreatedById != meId)
            .Select(n => new { n.TaskforceId, n.AuthorName, n.CreatedAt })
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
