using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

public class FollowupService(IDbContextFactory<AppDbContext> dbFactory, INotificationService notifications) : IFollowupService
{
    private const string Mentioned = "einer Wiedervorlage";

    public async Task<List<FollowupItem>> GetForRecordAsync(string entityType, string entityId,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Only show followups if the caller may see the parent record.
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, ViewerScope.From(actor), cancellationToken))
        {
            return new();
        }

        var rows = await db.Followups
            .Where(w => w.EntityType == entityType && w.EntityId == entityId)
            .OrderBy(w => w.Done).ThenBy(w => w.DueAt)
            .Select(w => new
            {
                w.Id, w.DueAt, w.Note, w.ResponsibleAgentId, w.Done, w.DoneAt, w.CreatedById,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new();
        }

        // Codenames only, never real names.
        var agentIds = rows.Select(r => r.ResponsibleAgentId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList()!;
        var codenames = agentIds.Count == 0
            ? new Dictionary<string, string?>()
            : (await db.Users.Where(u => agentIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Codename })
                .ToListAsync(cancellationToken))
                .ToDictionary(u => u.Id, u => (string?)u.Codename);

        var meId = actor.GetAgentId();
        var isLeadership = actor.IsLeadership();
        var now = DateTime.UtcNow;

        var items = rows.Select(r => new FollowupItem(
            Id: r.Id,
            DueAt: r.DueAt,
            Note: r.Note,
            ResponsibleAgentId: r.ResponsibleAgentId,
            ResponsibleCodename: r.ResponsibleAgentId is not null && codenames.TryGetValue(r.ResponsibleAgentId, out var cn) ? cn : null,
            Done: r.Done,
            DoneAt: r.DoneAt,
            Overdue: !r.Done && r.DueAt <= now,
            // Read-only supervision may not edit anything, even its own.
            MayEdit: actor.MayWrite() && (isLeadership || r.CreatedById == meId || r.ResponsibleAgentId == meId))).ToList();
        if (actor.GetPartnerAgency() is { } agency)
        {
            items = await PartnerVisibility.FilterChildrenAsync(db, entityType, entityId, nameof(Followup), items, i => i.Id, agency, meId, cancellationToken);
        }
        return items;
    }

    public async Task CreateAsync(string entityType, string entityId, FollowupInput input,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Für diese Akte darf keine Wiedervorlage angelegt werden.");
        }

        var responsibleId = await DetermineResponsibleAsync(db, input.ResponsibleAgentId, current: null, actor, cancellationToken);

        var note = input.Note.TrimToNull();
        db.Followups.Add(new Followup
        {
            EntityType = entityType,
            EntityId = entityId,
            DueAt = input.DueAt.ToUniversalTime(),
            Note = note,
            ResponsibleAgentId = responsibleId,
        });
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, note, Mentioned, entityType, entityId,
            actor, cancellationToken, childType: nameof(Followup));
    }

    public async Task RefreshAsync(string id, FollowupInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var w = await db.Followups.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Wiedervorlage nicht gefunden.");
        RequireEdit(w, actor);

        var newDue = input.DueAt.ToUniversalTime();
        // Re-notify on a moved due date by clearing the dedupe stamp.
        if (w.DueAt != newDue)
        {
            w.NotifiedAt = null;
        }
        var oldNote = w.Note;
        w.DueAt = newDue;
        w.Note = input.Note.TrimToNull();
        w.ResponsibleAgentId = await DetermineResponsibleAsync(db, input.ResponsibleAgentId,
            w.ResponsibleAgentId, actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, oldNote, w.Note, Mentioned, w.EntityType, w.EntityId,
            actor, cancellationToken, childType: nameof(Followup));
    }

    public async Task CompleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var w = await db.Followups.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Wiedervorlage nicht gefunden.");
        RequireEdit(w, actor);

        if (!w.Done)
        {
            w.Done = true;
            w.DoneAt = DateTime.UtcNow;
            w.DoneById = actor.GetAgentId();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ReopenAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var w = await db.Followups.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Wiedervorlage nicht gefunden.");
        RequireEdit(w, actor);

        if (w.Done)
        {
            w.Done = false;
            w.DoneAt = null;
            w.DoneById = null;
            // Due again, so allow re-notification.
            w.NotifiedAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var w = await db.Followups.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Wiedervorlage nicht gefunden.");
        RequireDelete(w, actor);
        db.Followups.Remove(w);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FollowupDashboardItem>> GetMyDueAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // Open and due, where I am responsible or watch the record.
        var rows = await db.Followups
            .Where(w => !w.Done && w.DueAt <= now
                && (w.ResponsibleAgentId == meId
                    || db.Watchlists.Any(x => x.AgentId == meId && x.EntityType == w.EntityType && x.EntityId == w.EntityId)))
            .OrderBy(w => w.DueAt)
            .Select(w => new { w.Id, w.EntityType, w.EntityId, w.DueAt, w.Note })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new();
        }

        var isLeadership = actor.MayClassifiedRead();
        var refs = rows.Select(r => (r.EntityType, r.EntityId)).Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken,
            mayAllTaskforces: actor.MayAllTaskforcesSee(), meId: meId);

        var result = new List<FollowupDashboardItem>();
        foreach (var r in rows)
        {
            if (!resolved.TryGetValue((r.EntityType, r.EntityId), out var a))
            {
                continue; // record trashed or unknown
            }
            if (a.Classified && !isLeadership)
            {
                continue; // hide classified from non-leadership
            }
            result.Add(new FollowupDashboardItem(r.Id, a.Display, a.Href, r.DueAt, r.Note));
        }
        return result;
    }

    /// <summary>Resolves the responsible agent; only a changed pick is validated.</summary>
    /// <remarks>
    /// Re-saving a followup whose responsible has since left must not fail, so an unchanged id passes through.
    /// </remarks>
    private static async Task<string?> DetermineResponsibleAsync(AppDbContext db, string? desired, string? current,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        // No selection defaults to the creator.
        if (string.IsNullOrWhiteSpace(desired))
        {
            return actor.GetAgentId();
        }
        if (desired == current)
        {
            return desired;
        }
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == desired, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte zuständige Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        return desired;
    }

    private static void RequireEdit(Followup w, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId) && (w.CreatedById == meId || w.ResponsibleAgentId == meId))
        {
            return;
        }
        throw new UnauthorizedAccessException("Diese Wiedervorlage darf nur ihr Ersteller, der Zuständige oder die Führung bearbeiten.");
    }

    private static void RequireDelete(Followup w, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId) && w.CreatedById == meId)
        {
            return;
        }
        throw new UnauthorizedAccessException("Eine Wiedervorlage darf nur ihr Ersteller oder die Führung löschen.");
    }
}
