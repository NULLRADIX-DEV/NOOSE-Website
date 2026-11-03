using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Models.Absences;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Absences are self-service and effective at once; leadership only acknowledges them.</summary>
public class AbsenceService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications) : IAbsenceService
{
    /// <summary>A single sign-off may not silence the anomaly detection for years.</summary>
    private const int MaxSpanDays = 180;

    /// <summary>Gated on the agent record, i.e. leadership — which mirrors who may read the reason at all.</summary>
    private Task NotifyMentionsAsync(string? oldReason, string? newReason, string agentId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MentionNotify.DeltaAsync(notifications, oldReason, newReason, "einer Abmeldung", nameof(Agent), agentId,
            actor, cancellationToken, href: "/abmeldungen/uebersicht");

    private static AbsenceViewScope Granted(ClaimsPrincipal viewer, AbsenceViewScope requested)
        => AbsenceVisibility.Granted(viewer, requested);

    public async Task<List<AbsenceRow>> GetListAsync(ClaimsPrincipal viewer, AbsenceViewScope requested,
        DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var scope = Granted(viewer, requested);
        var meId = viewer.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Absences.AsNoTracking().OnlyVisible(db, scope, meId);
        if (from is { } f)
        {
            query = query.Where(a => a.ToDate >= f);
        }
        if (to is { } t)
        {
            query = query.Where(a => a.FromDate <= t);
        }

        var rows = await query
            .OrderByDescending(a => a.FromDate)
            .Select(a => new
            {
                a.Id,
                a.AgentId,
                Codename = a.Agent!.Codename,
                a.FromDate,
                a.ToDate,
                a.Days,
                a.Category,
                a.Reason,
                a.AcknowledgedAt,
                a.AcknowledgedByName,
                a.CreatedById,
            })
            .ToListAsync(cancellationToken);

        // leadership may edit any absence; an owner only their own, and only while it has not lapsed
        var today = MeetingTime.Day(DateTime.UtcNow);
        var full = scope == AbsenceViewScope.All;
        return rows.Select(a =>
        {
            var mine = a.AgentId == meId;
            // free text and the acknowledgement signal never leave the server for viewers who may not read them
            var privateFields = AbsenceVisibility.MayReadPrivateFields(scope, mine);
            return new AbsenceRow(
                a.Id, a.AgentId, a.Codename, a.FromDate, a.ToDate, a.Days, a.Category,
                privateFields ? a.Reason : null,
                privateFields ? a.AcknowledgedAt : null,
                privateFields ? a.AcknowledgedByName : null,
                full || (mine && a.ToDate >= today));
        }).ToList();
    }

    public async Task<Absence?> GetDetailAsync(string id, ClaimsPrincipal viewer, AbsenceViewScope requested,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Absences.AsNoTracking()
            .OnlyVisible(db, Granted(viewer, requested), viewer.GetAgentId())
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, DateOnly>> GetAbsentOnAsync(DateOnly day, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // who is away is in-house business; the pickers a partner reaches never carry a target date anyway
        Permission.RequireInternalAgent(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Absences.AsNoTracking()
            .RosterVisible(db)
            .Covering(day)
            .Select(a => new { a.AgentId, a.ToDate })
            .ToListAsync(cancellationToken);

        // two sign-offs may overlap; the later end is the one a picker should name
        return rows
            .GroupBy(r => r.AgentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(r => r.ToDate), StringComparer.Ordinal);
    }

    public async Task<List<Absence>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Absences.AsNoTracking()
            .IgnoreQueryFilters()
            // the trash page shows the codename, not the raw agent id
            .Include(a => a.Agent)
            .Where(a => a.IsDeleted)
            .OrderByDescending(a => a.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Absence> CreateAsync(AbsenceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        var agentId = actor.GetAgentId()
            ?? throw new UnauthorizedAccessException("Ohne Agenten-Kontext ist keine Abmeldung möglich.");
        var (from, to) = Validated(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var absence = new Absence
        {
            AgentId = agentId,
            FromDate = from,
            ToDate = to,
            Days = to.DayNumber - from.DayNumber + 1,
            Category = input.Category,
            Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim(),
        };
        db.Absences.Add(absence);
        await db.SaveChangesAsync(cancellationToken);

        await LeadershipNotifyAsync(db, absence, agentId, cancellationToken);
        await NotifyMentionsAsync(null, absence.Reason, agentId, actor, cancellationToken);
        return absence;
    }

    public async Task RefreshAsync(string id, AbsenceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var (from, to) = Validated(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var absence = await db.Absences.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Abmeldung '{id}' nicht gefunden.");

        RequireLeadershipOrOwner(absence, actor);
        RequireNotPast(absence, actor);
        // block moving the coverage into the past: it would backdate an excuse onto a missed meeting
        if (!actor.IsLeadership() && to < DateOnly.FromDateTime(MeetingTime.Local(DateTime.UtcNow)))
        {
            throw new UnauthorizedAccessException(
                "Eine Abmeldung kann nur die Führung rückwirkend datieren.");
        }

        var oldReason = absence.Reason;
        absence.FromDate = from;
        absence.ToDate = to;
        absence.Days = to.DayNumber - from.DayNumber + 1;
        absence.Category = input.Category;
        absence.Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
        // any change resets leadership's acknowledgement so they re-review the new period
        absence.AcknowledgedAt = null;
        absence.AcknowledgedById = null;
        absence.AcknowledgedByName = null;
        await db.SaveChangesAsync(cancellationToken);

        await NotifyMentionsAsync(oldReason, absence.Reason, absence.AgentId, actor, cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var absence = await db.Absences.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Abmeldung '{id}' nicht gefunden.");

        RequireLeadershipOrOwner(absence, actor);
        RequireNotPast(absence, actor);

        db.Absences.Remove(absence);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var absence = await db.Absences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Abmeldung '{id}' nicht gefunden.");

        absence.IsDeleted = false;
        absence.DeletedAt = null;
        absence.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AcknowledgeAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var absence = await db.Absences.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Abmeldung '{id}' nicht gefunden.");

        // idempotent: acknowledging twice is not an error
        if (absence.AcknowledgedAt is not null)
        {
            return;
        }

        absence.AcknowledgedAt = DateTime.UtcNow;
        absence.AcknowledgedById = actor.GetAgentId();
        absence.AcknowledgedByName = actor.GetCodename();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Throws unless the actor is leadership or the absent agent themselves.</summary>
    private static void RequireLeadershipOrOwner(Absence absence, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var agentId = actor.GetAgentId();
        if (string.IsNullOrEmpty(agentId) || absence.AgentId != agentId)
        {
            throw new UnauthorizedAccessException(
                "Diese Abmeldung darf nur der abgemeldete Agent selbst oder die Führung bearbeiten.");
        }
    }

    /// <summary>A finished absence is evidence for the attendance statistics; only leadership may still touch it.</summary>
    private static void RequireNotPast(Absence absence, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        if (absence.ToDate < DateOnly.FromDateTime(MeetingTime.Local(DateTime.UtcNow)))
        {
            throw new UnauthorizedAccessException(
                "Eine bereits abgelaufene Abmeldung kann nur noch die Führung ändern.");
        }
    }

    private static (DateOnly From, DateOnly To) Validated(AbsenceInput input)
    {
        if (input.From is not { } from || input.To is not { } to)
        {
            throw new InvalidOperationException("Bitte Von- und Bis-Datum angeben.");
        }

        var fromDay = DateOnly.FromDateTime(from);
        var toDay = DateOnly.FromDateTime(to);
        if (toDay < fromDay)
        {
            throw new InvalidOperationException("Das Bis-Datum darf nicht vor dem Von-Datum liegen.");
        }
        if (toDay.DayNumber - fromDay.DayNumber + 1 > MaxSpanDays)
        {
            throw new InvalidOperationException($"Eine Abmeldung darf höchstens {MaxSpanDays} Tage umfassen.");
        }
        return (fromDay, toDay);
    }

    /// <summary>Best effort; the free-text reason deliberately stays out of the title.</summary>
    private async Task LeadershipNotifyAsync(AppDbContext db, Absence absence, string actorId, CancellationToken cancellationToken)
    {
        try
        {
            var codename = await db.Users.AsNoTracking()
                .Where(u => u.Id == actorId).Select(u => u.Codename)
                .FirstOrDefaultAsync(cancellationToken) ?? "Unbekannt";

            var recipients = await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.IsAdmin || u.Rank >= Rank.SpecialAgentInCharge)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var title = $"Abmeldung: {codename} vom {absence.FromDate:dd.MM.yyyy} bis {absence.ToDate:dd.MM.yyyy} "
                      + $"({AbsenceCategoryDisplay.Name(absence.Category)}).";

            await notifications.NotifyManyAsync(recipients, NotificationType.AbsenceFiled,
                title.Length > 300 ? title[..297] + "…" : title, "/abmeldungen/uebersicht", actorId, cancellationToken);
        }
        catch { /* best effort */ }
    }
}
