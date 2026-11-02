using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
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

    public async Task<string> CreateAsync(string entityType, string entityId, FollowupInput input,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Für diese Akte darf keine Wiedervorlage angelegt werden.");
        }

        var responsibleId = await DetermineResponsibleAsync(db, input.ResponsibleAgentId, current: null, actor, cancellationToken);

        var note = input.Note.TrimToNull();
        var followup = new Followup
        {
            EntityType = entityType,
            EntityId = entityId,
            DueAt = input.DueAt.ToUniversalTime(),
            Note = note,
            ResponsibleAgentId = responsibleId,
        };
        db.Followups.Add(followup);
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, note, Mentioned, entityType, entityId,
            actor, cancellationToken, childType: nameof(Followup));
        return followup.Id;
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
            w.EscalatedAt = null;
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

    public Task<List<FollowupDashboardItem>> GetMyDueAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
        => GetMineUntilAsync(actor, DateTime.UtcNow, cancellationToken);

    public async Task<List<FollowupDashboardItem>> GetMineUntilAsync(ClaimsPrincipal actor, DateTime untilUtc,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Open and due by then, where I am responsible or watch the record.
        var rows = await db.Followups
            .Where(w => !w.Done && w.DueAt <= untilUtc
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

    public async Task<FollowupPlan> GetMyPlanAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new FollowupPlan([], 0, false);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Followups.AsNoTracking()
            .Where(w => !w.Done && w.ResponsibleAgentId == meId)
            .OrderBy(w => w.DueAt).ThenBy(w => w.Id)
            .Take(FollowupPlanRules.MaxRows + 1)
            .Select(w => new { w.Id, w.EntityType, w.EntityId, w.DueAt, w.Note })
            .ToListAsync(cancellationToken);
        var capped = rows.Count > FollowupPlanRules.MaxRows;
        if (capped)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var parents = await ParentsAsync(db, rows.Select(r => (r.EntityType, r.EntityId)), ViewerScope.From(actor),
            cancellationToken);
        var today = MeetingTime.Day(DateTime.UtcNow);
        var items = new List<FollowupPlanItem>();
        var hidden = 0;
        foreach (var r in rows)
        {
            var parent = parents[(r.EntityType, r.EntityId)];
            if (parent.State == ParentState.Gone)
            {
                continue; // record trashed or unknown
            }
            if (parent.State == ParentState.Hidden)
            {
                // count, never name
                hidden++;
                continue;
            }
            items.Add(new FollowupPlanItem(r.Id, parent.Record.Display, parent.Record.Href,
                RecordTypeDisplay.Name(r.EntityType), r.DueAt, r.Note,
                FollowupPlanRules.Bucket(MeetingTime.Day(r.DueAt), today)));
        }
        return new FollowupPlan(items, hidden, capped);
    }

    public async Task PostponeAsync(string id, int days, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!FollowupPlanRules.PostponeDays.Contains(days))
        {
            throw new InvalidOperationException("Eine Wiedervorlage lässt sich nur um einen Tag oder eine Woche verschieben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var w = await db.Followups.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Wiedervorlage nicht gefunden.");
        RequireEdit(w, actor);
        if (w.Done)
        {
            throw new InvalidOperationException("Eine erledigte Wiedervorlage lässt sich nicht verschieben.");
        }
        await RequireVisibleParentAsync(db, w, actor, cancellationToken);

        w.DueAt = FollowupPlanRules.Postponed(w.DueAt, days, DateTime.UtcNow);
        // rearm both stamps
        w.NotifiedAt = null;
        w.EscalatedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<FollowupBacklog> GetBacklogAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        Permission.RequireClassifiedRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Followups.AsNoTracking()
            .Where(w => !w.Done)
            .OrderBy(w => w.DueAt).ThenBy(w => w.Id)
            .Take(FollowupPlanRules.MaxRows + 1)
            .Select(w => new { w.Id, w.EntityType, w.EntityId, w.DueAt, w.Note, w.ResponsibleAgentId, w.EscalatedAt })
            .ToListAsync(cancellationToken);
        var capped = rows.Count > FollowupPlanRules.MaxRows;
        if (capped)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var parents = await ParentsAsync(db, rows.Select(r => (r.EntityType, r.EntityId)), ViewerScope.From(actor),
            cancellationToken);

        // codenames only, never real names
        var agentIds = rows.Select(r => r.ResponsibleAgentId).OfType<string>().Distinct().ToList();
        var codenames = await db.Users.AsNoTracking()
            .Where(u => agentIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Codename })
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
        var selectable = (await db.Users.OnlySelectable()
            .Where(u => agentIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var items = new List<FollowupBacklogItem>();
        foreach (var r in rows)
        {
            var parent = parents[(r.EntityType, r.EntityId)];
            if (parent.State == ParentState.Gone)
            {
                continue; // record trashed or unknown
            }
            var visible = parent.State == ParentState.Visible;
            items.Add(new FollowupBacklogItem(
                r.Id,
                visible,
                visible ? parent.Record.Display : null,
                visible ? parent.Record.Href : null,
                visible ? RecordTypeDisplay.Name(r.EntityType) : null,
                visible ? r.Note : null,
                r.DueAt,
                FollowupPlanRules.DaysOverdue(r.DueAt, now),
                r.ResponsibleAgentId,
                r.ResponsibleAgentId is not null && codenames.TryGetValue(r.ResponsibleAgentId, out var cn) ? cn : null,
                r.ResponsibleAgentId is not null && selectable.Contains(r.ResponsibleAgentId),
                r.EscalatedAt));
        }
        return new FollowupBacklog(items, capped);
    }

    public async Task ReassignAsync(string id, string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var w = await db.Followups.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Wiedervorlage nicht gefunden.");
        if (w.Done)
        {
            throw new InvalidOperationException("Eine erledigte Wiedervorlage lässt sich nicht umhängen.");
        }
        await RequireVisibleParentAsync(db, w, actor, cancellationToken);

        var target = await db.Users.OnlySelectable()
            .Where(u => u.Id == agentId)
            .Select(u => new { u.Id, u.IsAdmin, u.Rank, u.PartnerAgency })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        // MariaDB ids ignore case
        if (target.Id == w.ResponsibleAgentId)
        {
            return;
        }
        // same rule as reminders
        if (!await Visibility.IsRecordVisibleAsync(db, w.EntityType, w.EntityId,
                ReminderScope(target.Id, target.IsAdmin, target.Rank, target.PartnerAgency), cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent darf die Akte dieser Wiedervorlage nicht sehen.");
        }

        w.ResponsibleAgentId = target.Id;
        await db.SaveChangesAsync(cancellationToken);

        if (target.Id == actor.GetAgentId())
        {
            return;
        }
        try
        {
            var parent = await RecordsReference.ResolveAsync(db, [(w.EntityType, w.EntityId)], CancellationToken.None);
            if (parent.TryGetValue((w.EntityType, w.EntityId), out var record))
            {
                var title = $"Wiedervorlage übertragen: „{record.Display}“, fällig {MeetingTime.Day(w.DueAt):dd.MM.yyyy}";
                await notifications.NotifyAsync(target.Id, NotificationType.Followup,
                    title.Length > 300 ? title[..297] + "…" : title, record.Href, CancellationToken.None);
            }
        }
        catch { /* best effort */ }
    }

    public async Task<int> EscalateOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = FollowupPlanRules.EscalationCutoff(nowUtc);
        var due = await db.Followups.AsNoTracking()
            .Where(w => !w.Done && w.EscalatedAt == null && w.DueAt <= cutoff)
            .OrderBy(w => w.DueAt)
            .Select(w => new { w.Id, w.EntityType, w.EntityId })
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var refs = due.Select(w => (w.EntityType, w.EntityId)).Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken);

        var escalated = 0;
        foreach (var w in due)
        {
            // rest waits for next run
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            // claim before notifying
            var claimed = await db.Followups
                .Where(x => x.Id == w.Id && !x.Done && x.EscalatedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EscalatedAt, nowUtc), CancellationToken.None);
            // trashed parent: stamp silently
            if (claimed == 1 && resolved.ContainsKey((w.EntityType, w.EntityId)))
            {
                escalated++;
            }
        }
        if (escalated == 0)
        {
            return 0;
        }

        // no team leads, partners, demo
        var leadership = await db.Users.AsNoTracking()
            .OnlySelectable()
            .Where(u => u.IsAdmin || u.Rank >= Rank.SupervisorySpecialAgent)
            .Where(u => u.Id != DemoIdentity.AgentId)
            .Select(u => u.Id)
            .ToListAsync(CancellationToken.None);
        // one folding notice, unnamed
        await notifications.NotifyManyOnceAsync(leadership, NotificationType.FollowupEscalated,
            FollowupPlanRules.EscalationTitle, FollowupPlanRules.BacklogRoute, triggerId: null, CancellationToken.None);
        return escalated;
    }

    /// <summary>The scope a reminder recipient is checked with; the due worker and reassigning share it.</summary>
    public static ViewerScope ReminderScope(string agentId, bool isAdmin, Rank? rank, PartnerAgency? partnerAgency)
    {
        var leadership = isAdmin || rank is >= Rank.SupervisorySpecialAgent;
        // else citizen tickets hide
        return new ViewerScope(leadership, leadership, agentId, null, MayAgenda: leadership,
            IsInternalAgent: partnerAgency is null);
    }

    private enum ParentState
    {
        Gone,
        Hidden,
        Visible,
    }

    /// <summary>Each distinct parent once: trashed or unknown, hidden from the viewer, or visible with its name.</summary>
    private static async Task<Dictionary<(string Type, string Id), (ParentState State, RecordsReference.Resolution Record)>>
        ParentsAsync(AppDbContext db, IEnumerable<(string Type, string Id)> refs, ViewerScope scope,
            CancellationToken cancellationToken)
    {
        var distinct = refs.Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, distinct, cancellationToken);
        var map = new Dictionary<(string, string), (ParentState, RecordsReference.Resolution)>();
        foreach (var key in distinct)
        {
            if (!resolved.TryGetValue(key, out var record))
            {
                map[key] = (ParentState.Gone, default);
                continue;
            }
            var visible = await Visibility.IsRecordVisibleAsync(db, key.Type, key.Id, scope, cancellationToken);
            map[key] = (visible ? ParentState.Visible : ParentState.Hidden, record);
        }
        return map;
    }

    private static async Task RequireVisibleParentAsync(AppDbContext db, Followup w, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!await Visibility.IsRecordVisibleAsync(db, w.EntityType, w.EntityId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Wiedervorlage hängt an einer Akte, die du nicht sehen darfst.");
        }
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
