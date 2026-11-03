using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFirearmUseService" />
public class FirearmUseService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications) : IFirearmUseService
{
    /// <summary>Page route of the overview; the bell links here.</summary>
    public const string OverviewRoute = "/schusswaffengebrauch";

    /// <summary>Rows one list returns; a filter narrows further.</summary>
    public const int MaxRows = 500;

    public async Task<FirearmUse> ReportAsync(string reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireFirearmUseReport(actor);
        var text = FirearmUseRules.CleanReason(reason);
        var agentId = actor.GetAgentId()
            ?? throw new UnauthorizedAccessException("Ohne Agenten-Kontext ist keine Meldung möglich.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = new FirearmUse { AgentId = agentId, Timestamp = DateTime.UtcNow, Reason = text };
        db.FirearmUses.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        await NotifyLeadershipAsync(db, row, cancellationToken);
        return row;
    }

    public async Task<List<FirearmUseRow>> GetListAsync(FirearmUseFilter filter, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireFirearmUseOverview(viewer);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.FirearmUses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.AgentId))
        {
            query = query.Where(x => x.AgentId == filter.AgentId);
        }
        if (filter.FromUtc is { } from)
        {
            query = query.Where(x => x.Timestamp >= from);
        }
        if (filter.ToUtc is { } to)
        {
            query = query.Where(x => x.Timestamp < to);
        }
        if (filter.Division is { } division)
        {
            var members = await MembersOfAsync(db, division, cancellationToken);
            query = query.Where(x => members.Contains(x.AgentId));
        }
        return await RowsAsync(query, viewer, cancellationToken);
    }

    public async Task<List<FirearmUseRow>> GetForAgentAsync(string agentId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        if (!FirearmUseRules.MayRead(viewer, agentId))
        {
            throw new UnauthorizedAccessException(
                "Diese Meldungen sehen nur der Agent selbst, die Führung und die Aufsicht.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await RowsAsync(db.FirearmUses.AsNoTracking().Where(x => x.AgentId == agentId), viewer,
            cancellationToken);
    }

    public async Task<List<(string Id, string Codename)>> GetAgentOptionsAsync(ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireFirearmUseOverview(viewer);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.FirearmUses.AsNoTracking().Select(x => x.AgentId).Distinct().ToListAsync(cancellationToken);
        return await AgentDirectory.ByIdsAsync(db, ids, cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireFirearmUseDelete(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.FirearmUses.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Meldung '{id}' nicht gefunden.");

        if (!FirearmUseRules.MayDelete(actor, row.AgentId))
        {
            throw new UnauthorizedAccessException(
                "Die eigene Meldung löscht ein anderes Mitglied der Führung, nicht der Meldende selbst.");
        }

        db.FirearmUses.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FirearmUse>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FirearmUses.IgnoreQueryFilters().AsNoTracking()
            // the trash page shows the codename, not the raw agent id
            .Include(x => x.Agent)
            .Where(x => x.IsDeleted)
            .OrderByDescending(x => x.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireFirearmUseDelete(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.FirearmUses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Meldung '{id}' nicht gefunden.");

        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Agents whose home division is the given one; resolved in memory because the home may be derived.</summary>
    private static async Task<List<string>> MembersOfAsync(AppDbContext db, Division division,
        CancellationToken cancellationToken)
    {
        var agents = await db.Users.AsNoTracking().OnlyListable().Where(a => a.Rank != null)
            .Select(a => new { a.Id, a.Rank, a.Division, a.IsTRU })
            .ToListAsync(cancellationToken);
        return agents
            .Where(a => DivisionRules.Of(a.Rank, a.Division, a.IsTRU).Home == division)
            .Select(a => a.Id)
            .ToList();
    }

    private static async Task<List<FirearmUseRow>> RowsAsync(IQueryable<FirearmUse> query, ClaimsPrincipal viewer,
        CancellationToken cancellationToken)
    {
        var rows = await query.OrderByDescending(x => x.Timestamp).Take(MaxRows)
            .Select(x => new
            {
                x.Id,
                x.AgentId,
                Codename = x.Agent!.Codename,
                Rank = x.Agent!.Rank,
                Division = x.Agent!.Division,
                IsTru = x.Agent!.IsTRU,
                Partner = x.Agent!.PartnerAgency,
                x.Timestamp,
                x.Reason,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r =>
        {
            var unit = DivisionRules.Of(r.Rank, r.Division, r.IsTru, r.Partner is null);
            return new FirearmUseRow(r.Id, r.AgentId, r.Codename, r.Rank, unit.Home, unit.IsBoard, r.Timestamp,
                r.Reason, FirearmUseRules.MayDelete(viewer, r.AgentId));
        }).ToList();
    }

    /// <summary>Best effort; the reason never goes into the title, the overview carries it.</summary>
    private async Task NotifyLeadershipAsync(AppDbContext db, FirearmUse row, CancellationToken cancellationToken)
    {
        try
        {
            var reporter = await db.Users.AsNoTracking().Where(u => u.Id == row.AgentId)
                .Select(u => new { u.Codename, u.Rank, u.Division, u.IsTRU, u.PartnerAgency })
                .FirstOrDefaultAsync(cancellationToken);
            var home = reporter is null
                ? null
                : DivisionRules.Of(reporter.Rank, reporter.Division, reporter.IsTRU, reporter.PartnerAgency is null).Home;

            // selectable staff only
            var candidates = await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.Rank >= FirearmUseRules.NotifyFloor && u.Id != row.AgentId)
                .Select(u => new { u.Id, u.Rank, u.Division, u.IsTRU })
                .ToListAsync(cancellationToken);
            var recipients = candidates
                .Where(c => FirearmUseRules.Notifies(DivisionRules.Of(c.Rank, c.Division, c.IsTRU), home))
                .Select(c => c.Id)
                .ToList();

            if (recipients.Count == 0)
            {
                return;
            }

            var local = MeetingTime.Local(row.Timestamp);
            var title = $"Schusswaffengebrauch: {reporter?.Codename ?? "Unbekannt"} am {local:dd.MM.yyyy} um {local:HH:mm} Uhr.";

            await notifications.NotifyManyInAppAsync(recipients, NotificationType.FirearmUseReported,
                title.Length > 300 ? title[..297] + "…" : title, OverviewRoute, row.AgentId, cancellationToken);
        }
        catch { /* best effort */ }
    }
}
