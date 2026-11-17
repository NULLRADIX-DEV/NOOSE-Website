using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Administration;

/// <summary>Service references issued by the Administration.</summary>
public interface IServiceReferenceService
{
    /// <summary>References the viewer may see; <paramref name="agentId"/> narrows to one agent.</summary>
    Task<IReadOnlyList<ServiceReferenceRow>> GetListAsync(ClaimsPrincipal actor, string? agentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Starting grade from the agent's evaluations; null for a viewer outside the workspace.</summary>
    Task<EvaluationGrade?> SuggestGradeAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> IssueAsync(ServiceReferenceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<ServiceReferencePrint?> GetPrintAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<ServiceReference>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IServiceReferenceService" />
public class ServiceReferenceService(IDbContextFactory<AppDbContext> dbFactory) : IServiceReferenceService
{
    public const int MaxTextLength = 40000;

    public async Task<IReadOnlyList<ServiceReferenceRow>> GetListAsync(ClaimsPrincipal actor, string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ServiceReferences.AsNoTracking();
        if (!string.IsNullOrEmpty(agentId))
        {
            query = query.Where(r => r.AgentId == agentId);
        }
        var rows = (await query.OrderByDescending(r => r.IssuedOn).Take(300).ToListAsync(cancellationToken))
            .Where(r => ServiceReferenceRules.MaySee(scope, r.AgentId))
            .ToList();
        var names = await AdministrationService.NamesAsync(db, rows.Select(r => r.AgentId), cancellationToken);
        return rows
            .Select(r => new ServiceReferenceRow(r.Id, r.AgentId, names.GetValueOrDefault(r.AgentId, "—"), r.Reason, r.Grade,
                r.IssuedOn, r.IssuedByName))
            .ToList();
    }

    public async Task<EvaluationGrade?> SuggestGradeAsync(string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.MayAdministrationRead())
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var grades = await db.AgentEvaluations.AsNoTracking()
            .Where(e => e.AgentId == agentId)
            .Select(e => e.Grade)
            .ToListAsync(cancellationToken);
        return ServiceReferenceRules.SuggestedGrade(grades);
    }

    public async Task<string> IssueAsync(ServiceReferenceInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        if (!ServiceReferenceRules.MayIssue(actor, input.AgentId))
        {
            throw new UnauthorizedAccessException("Das eigene Dienstzeugnis stellt jemand anderes aus.");
        }
        var clean = HtmlCleanup.Clean(input.TextHtml);
        if (HtmlCleanup.PlainText(clean).Length == 0)
        {
            throw new InvalidOperationException("Das Dienstzeugnis braucht einen Text.");
        }
        if (clean.Length > MaxTextLength)
        {
            throw new InvalidOperationException($"Der Text darf höchstens {MaxTextLength} Zeichen haben.");
        }
        if (input.Grade is { } grade)
        {
            EvaluationRules.RequireValidGrade(grade);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await PersonalRelationRules.RequireNotRelatedAsync(db, actor, input.AgentId, cancellationToken);
        // leavers have one too, so any agent of the house, not only the selectable ones
        var agent = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == input.AgentId && u.Rank != null && u.PartnerAgency == null, cancellationToken)
            ?? throw new InvalidOperationException("Dienstzeugnisse gibt es nur für Agenten des FIB.");
        var row = new ServiceReference
        {
            AgentId = agent.Id,
            Reason = input.Reason,
            Grade = input.Grade,
            TextHtml = clean,
            IssuedOn = (input.IssuedOn ?? DateTime.UtcNow).Date,
            IssuedByName = actor.GetCodename(),
            RankSnapshot = agent.Rank,
            DivisionSnapshot = DivisionRules.Of(agent).Home,
            ServiceStart = agent.ReleasedAt ?? agent.RegisteredAt,
        };
        db.ServiceReferences.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task<ServiceReferencePrint?> GetPrintAsync(string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ServiceReferences.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null || !ServiceReferenceRules.MaySee(scope, row.AgentId))
        {
            return null;
        }
        var agent = await db.Users.AsNoTracking()
            .Where(u => u.Id == row.AgentId)
            .Select(u => new { u.Codename, u.RealName, u.Rank, u.Division, u.IsTRU })
            .FirstOrDefaultAsync(cancellationToken);
        var own = string.Equals(scope.MeId, row.AgentId, StringComparison.OrdinalIgnoreCase);
        var shown = own
            ? agent?.RealName
            : RealNameScope.For(actor).Of(row.AgentId, agent?.RealName, agent?.Rank, agent?.Division, agent?.IsTRU ?? false);
        var name = !string.IsNullOrWhiteSpace(shown) ? shown : agent?.Codename ?? "—";
        var ranks = await db.AgentRankHistories.AsNoTracking()
            .Where(h => h.AgentId == row.AgentId && h.Timestamp <= row.IssuedOn.AddDays(1))
            .OrderBy(h => h.Timestamp)
            .ToListAsync(cancellationToken);
        var transfers = await db.DivisionTransfers.AsNoTracking()
            .Where(t => t.AgentId == row.AgentId && t.At <= row.IssuedOn.AddDays(1))
            .OrderBy(t => t.At)
            .ToListAsync(cancellationToken);
        var awards = await (
                from a in db.DecorationAwards.AsNoTracking()
                join d in db.Decorations.AsNoTracking() on a.DecorationId equals d.Id
                where a.AgentId == row.AgentId && a.AwardedOn <= row.IssuedOn.AddDays(1)
                orderby a.AwardedOn
                select new { d.Name, a.AwardedOn })
            .ToListAsync(cancellationToken);
        return new ServiceReferencePrint(row, name, ranks, transfers, awards.Select(a => (a.Name, a.AwardedOn)).ToList());
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ServiceReferences.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }
        await RequireMayManageAsync(db, actor, row.AgentId, cancellationToken);
        db.ServiceReferences.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    // the issue rule for every later write: never the own file, never a relative's
    private static async Task RequireMayManageAsync(AppDbContext db, ClaimsPrincipal actor, string agentId,
        CancellationToken cancellationToken)
    {
        if (!ServiceReferenceRules.MayIssue(actor, agentId))
        {
            throw new UnauthorizedAccessException("Das eigene Dienstzeugnis verwaltet jemand anderes.");
        }
        await PersonalRelationRules.RequireNotRelatedAsync(db, actor, agentId, cancellationToken);
    }

    public async Task<List<ServiceReference>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ServiceReferences.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.IsDeleted)
            .OrderByDescending(r => r.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ServiceReferences.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id && r.IsDeleted, cancellationToken);
        if (row is null)
        {
            return;
        }
        await RequireMayManageAsync(db, actor, row.AgentId, cancellationToken);
        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
