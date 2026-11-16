using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Administration;

/// <summary>The chat of the Administration workspace.</summary>
public interface IAdministrationChatService
{
    Task<List<AdministrationMessage>> GetMessagesAsync(ClaimsPrincipal actor, int limit = 100, DateTime? olderAs = null,
        CancellationToken cancellationToken = default);

    /// <summary>Unread count and the line "new since your last visit"; the same predicate as the menu badge.</summary>
    Task<AdministrationChatState> GetReadStateAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves the caller's mark forward; quiet for accounts that may not write.</summary>
    Task MarkReadAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<AdministrationMessage> SendAsync(string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string messageId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdministrationChatService" />
public class AdministrationChatService(IDbContextFactory<AppDbContext> dbFactory, AdministrationChatBroadcaster broadcaster,
    INotificationService notifications) : IAdministrationChatService
{
    private const string Room = AdministrationWorkspace.SingletonId;

    public async Task<List<AdministrationMessage>> GetMessagesAsync(ClaimsPrincipal actor, int limit = 100,
        DateTime? olderAs = null, CancellationToken cancellationToken = default)
    {
        if (!actor.MayAdministrationRead())
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AdministrationMessages.AsNoTracking();
        if (olderAs is not null)
        {
            query = query.Where(m => m.CreatedAt < olderAs.Value);
        }
        var latest = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
        latest.Reverse();
        return latest;
    }

    /// <remarks>Only members who may write carry a mark: a counter they could never clear would only be noise.</remarks>
    public async Task<AdministrationChatState> GetReadStateAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId) || !actor.MayAdministrationWrite())
        {
            return AdministrationChatState.None;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var since = await SinceAsync(db, meId, cancellationToken);
        var unread = await db.AdministrationMessages
            .CountAsync(m => m.CreatedAt > since && m.CreatedById != meId, cancellationToken);
        return new AdministrationChatState(since, unread);
    }

    private static async Task<DateTime> SinceAsync(AppDbContext db, string meId, CancellationToken cancellationToken)
        => await db.AdministrationChatReads.AsNoTracking()
               .Where(r => r.AgentId == meId)
               .Select(r => (DateTime?)r.LastReadAt)
               .FirstOrDefaultAsync(cancellationToken)
           ?? DateTime.UtcNow - AdministrationChatRules.UnreadWindow;

    /// <remarks>No audit and no modified stamp for having read; that is why the write gate is checked here, quietly.</remarks>
    public async Task MarkReadAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId) || !actor.MayAdministrationWrite())
        {
            return;
        }
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // forward only: a late write from a second tab must not bring messages back
        var moved = await db.AdministrationChatReads
            .Where(r => r.AgentId == meId && r.LastReadAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastReadAt, now), cancellationToken);
        if (moved == 0 && !await db.AdministrationChatReads.AnyAsync(r => r.AgentId == meId, cancellationToken))
        {
            db.AdministrationChatReads.Add(new AdministrationChatRead { AgentId = meId, LastReadAt = now });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) { /* ignore */ }
        }

        broadcaster.ReportRead(meId);

        try
        {
            await notifications.TargetAsReadMarkAsync(actor, NotificationType.AdministrationChat,
                AdministrationChatRules.Href, now, cancellationToken);
        }
        catch { /* best effort */ }
    }

    public async Task<AdministrationMessage> SendAsync(string text, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        var content = text?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Die Nachricht darf nicht leer sein.");
        }
        if (content.Length > AdministrationChatRules.MaxLength)
        {
            throw new InvalidOperationException($"Höchstens {AdministrationChatRules.MaxLength} Zeichen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var message = new AdministrationMessage { Text = content, AuthorName = actor.GetCodename() };
        db.AdministrationMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(Room);

        try
        {
            var who = string.IsNullOrWhiteSpace(actor.GetCodename()) ? "Ein Agent" : actor.GetCodename();
            await notifications.NotifyMentionedAsync(content, $"{who} hat dich im Administration-Chat erwähnt.",
                AdministrationChatRules.Href, nameof(AdministrationWorkspace), AdministrationWorkspace.SingletonId,
                actor, cancellationToken);
        }
        catch { /* best effort */ }

        try
        {
            await NotifyMembersAsync(db, message, actor, cancellationToken);
        }
        catch { /* best effort */ }

        return message;
    }

    /// <summary>Rings division members for whom this is the first unread message; the Board only hears of mentions.</summary>
    private async Task NotifyMembersAsync(AppDbContext db, AdministrationMessage message, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var authorId = message.CreatedById ?? actor.GetAgentId();
        var mentioned = MentionParser.Parse(message.Text)
            .Where(t => t.Type == nameof(Agent))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        var memberIds = await db.Users.OnlySelectable()
            .Where(u => u.IsHRB && u.Id != authorId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        var candidates = memberIds
            .Where(id => !mentioned.Contains(id) && !broadcaster.IsWatching(Room, id))
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var marks = await db.AdministrationChatReads.AsNoTracking()
            .Where(r => candidates.Contains(r.AgentId))
            .ToDictionaryAsync(r => r.AgentId, r => r.LastReadAt, cancellationToken);
        var fallback = DateTime.UtcNow - AdministrationChatRules.UnreadWindow;
        var oldest = candidates.Min(id => marks.GetValueOrDefault(id, fallback));
        var earlier = await db.AdministrationMessages.AsNoTracking()
            .Where(m => m.Id != message.Id && m.CreatedAt > oldest)
            .Select(m => new { m.CreatedAt, m.CreatedById })
            .ToListAsync(cancellationToken);
        var recipients = candidates
            .Where(id =>
            {
                var since = marks.GetValueOrDefault(id, fallback);
                return !earlier.Any(m => m.CreatedAt > since && m.CreatedById != id);
            })
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        await notifications.NotifyManyInAppAsync(recipients, NotificationType.AdministrationChat,
            "Neue Nachrichten im Administration-Chat.", AdministrationChatRules.Href, authorId, cancellationToken);
    }

    public async Task DeleteAsync(string messageId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.AdministrationMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
        {
            return;
        }
        if (!DisciplinaryRules.IsLeadershipSide(actor) && message.CreatedById != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Nur der Autor oder das Board kann eine Nachricht zurückziehen.");
        }
        db.AdministrationMessages.Remove(message);
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Report(Room);
    }
}
