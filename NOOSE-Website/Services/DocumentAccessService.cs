using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Shares;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

public class DocumentAccessService(IDbContextFactory<AppDbContext> dbFactory, DocumentAccessBroadcaster broadcaster)
    : IDocumentAccessService
{
    private const Rank LeadershipFloor = Rank.SpecialAgentInCharge;

    public async Task<bool> CanManageAccessAsync(string documentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.CreatedById, d.OwnerTaskforceId })
            .FirstOrDefaultAsync(cancellationToken);
        return doc is not null && await MayManageAsync(db, doc.CreatedById, doc.OwnerTaskforceId, actor, cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentAccessEntry>> GetAccessListAsync(string documentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.IsClassified, d.IsTRUClassified, d.IsHRBClassified, d.OwnerTaskforceId, d.CreatedById })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Dokument nicht gefunden.");

        await RequireManageAsync(db, doc.CreatedById, doc.OwnerTaskforceId, actor, cancellationToken);

        // diverges from DocumentViewerScope on purpose: read-only supervision can read the file but is
        // never listed anywhere, so this answers "who is listed", not "who can read"
        var q = db.Users.AsNoTracking().OnlySelectable();

        // classification gate against stored fields: mayClassified(a) = IsAdmin || Rank >= floor
        if (doc.IsClassified)
            q = q.Where(a => a.IsAdmin || a.Rank >= LeadershipFloor);
        else if (doc.IsTRUClassified)
            q = q.Where(a => a.IsAdmin || a.Rank >= LeadershipFloor || a.IsTRU);
        else if (doc.IsHRBClassified)
            q = q.Where(a => a.IsAdmin || a.Rank >= LeadershipFloor || a.IsHRB);

        // taskforce-internal: members and leadership/admin only
        if (doc.OwnerTaskforceId is { } tfId)
            q = q.Where(a => a.IsAdmin || a.Rank >= LeadershipFloor
                || db.TaskforceAgents.Any(ta => ta.TaskforceId == tfId && ta.AgentId == a.Id));

        var agents = await q
            .OrderBy(a => a.Codename)
            .Select(a => new { a.Id, a.Codename, a.RealName, a.Rank })
            .ToListAsync(cancellationToken);

        var excluded = (await db.DocumentAccessExclusions.AsNoTracking()
                .Where(x => x.DocumentId == documentId)
                .Select(x => x.AgentId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return agents
            .Select(a => new DocumentAccessEntry(a.Id, a.Codename, a.RealName, a.Rank, excluded.Contains(a.Id)))
            .ToList();
    }

    public async Task RevokeAsync(string documentId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        if (agentId == actor.GetAgentId())
            throw new InvalidOperationException("Du kannst dir den Zugriff nicht selbst entziehen.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.CreatedById, d.OwnerTaskforceId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Dokument nicht gefunden.");
        await RequireManageAsync(db, doc.CreatedById, doc.OwnerTaskforceId, actor, cancellationToken);

        var target = await db.Users.AsNoTracking()
            .Where(a => a.Id == agentId)
            .Select(a => new { a.IsAdmin, a.PartnerAgency, a.Rank, a.IsTeamLead })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Agent nicht gefunden.");
        if (target.IsAdmin)
            throw new InvalidOperationException("Einem Admin kann der Zugriff nicht entzogen werden.");
        if (target.PartnerAgency != null)
            throw new InvalidOperationException("Nur interne Agenten stehen auf der Einsichtsliste.");
        // mirrors the read side: an unlisted target would leave an exclusion nobody can undo
        if (target.IsTeamLead)
            throw new InvalidOperationException("Nur interne Agenten stehen auf der Einsichtsliste.");
        // non-leadership actors must not revoke leadership (admins already blocked above)
        if (!actor.IsLeadership() && target.Rank >= LeadershipFloor)
            throw new InvalidOperationException("Der Führung kann der Zugriff nur durch die Führung entzogen werden.");

        var existing = await db.DocumentAccessExclusions
            .FirstOrDefaultAsync(x => x.DocumentId == documentId && x.AgentId == agentId, cancellationToken);
        if (existing is null)
        {
            db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { DocumentId = documentId, AgentId = agentId });
            await db.SaveChangesAsync(cancellationToken);
        }
        broadcaster.Report(documentId);
    }

    public async Task RestoreAsync(string documentId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.CreatedById, d.OwnerTaskforceId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Dokument nicht gefunden.");
        await RequireManageAsync(db, doc.CreatedById, doc.OwnerTaskforceId, actor, cancellationToken);

        var row = await db.DocumentAccessExclusions
            .FirstOrDefaultAsync(x => x.DocumentId == documentId && x.AgentId == agentId, cancellationToken);
        if (row is not null)
        {
            db.DocumentAccessExclusions.Remove(row); // soft-delete via interceptor = restore
            await db.SaveChangesAsync(cancellationToken);
        }
        broadcaster.Report(documentId);
    }

    // leadership, document creator, or a lead of the owning taskforce may manage access
    private static async Task<bool> MayManageAsync(AppDbContext db, string? createdById, string? ownerTaskforceId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.IsLeadership())
            return true;
        var me = actor.GetAgentId();
        if (me is null)
            return false;
        if (createdById == me)
            return true;
        if (ownerTaskforceId is { } tfId)
            return await db.TaskforceAgents
                .AnyAsync(a => a.TaskforceId == tfId && a.AgentId == me && a.Role != TaskforceRole.Member, cancellationToken);
        return false;
    }

    private static async Task RequireManageAsync(AppDbContext db, string? createdById, string? ownerTaskforceId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!await MayManageAsync(db, createdById, ownerTaskforceId, actor, cancellationToken))
            throw new UnauthorizedAccessException(
                "Die Einsichtsliste verwaltet nur die Führung, der Ersteller oder die Leitung der zugehörigen Taskforce.");
    }
}
