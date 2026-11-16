using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Administration;

/// <summary>Disciplinary proceedings: opening, suspending, closing with a notice or a discontinuation.</summary>
public interface IDisciplinaryService
{
    /// <summary>Proceedings the viewer may see; <paramref name="closed"/> picks the closed ones instead of the running ones.</summary>
    Task<IReadOnlyList<DisciplinaryRow>> GetListAsync(bool closed, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The closed proceedings against one agent, as their personnel file shows them.</summary>
    Task<IReadOnlyList<DisciplinaryRow>> GetForAgentAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<DisciplinaryView?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Whether the agent qualifies for the simplified hearing (trainee or on probation).</summary>
    Task<bool> QualifiesForSimplifiedAsync(string agentId, CancellationToken cancellationToken = default);

    Task<string> OpenAsync(DisciplinaryOpenInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task UpdateGroundsAsync(string id, string grounds, bool provisionallySuspended, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Suspends a running proceeding (criminal case pending) or resumes it.</summary>
    Task SetSuspendedAsync(string id, bool suspended, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task CloseAsync(string id, DisciplinaryCloseInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<DisciplinaryProceeding>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDisciplinaryService" />
public class DisciplinaryService(IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumbers,
    INotificationService notifications, ILogger<DisciplinaryService> logger) : IDisciplinaryService
{
    public const string CaseNumberPrefix = "DV";

    public static string Href(string id) => $"/administration/disziplinar/{id}";

    public async Task<IReadOnlyList<DisciplinaryRow>> GetListAsync(bool closed, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.MayAdministrationRead())
        {
            return [];
        }
        var scope = ViewerScope.From(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DisciplinaryProceedings.AsNoTracking();
        query = closed
            ? query.Where(d => d.Status == DisciplinaryStatus.Discontinued || d.Status == DisciplinaryStatus.Decided)
            : query.Where(d => d.Status == DisciplinaryStatus.Open || d.Status == DisciplinaryStatus.Suspended);
        var rows = await query.OrderByDescending(d => d.OpenedAt).Take(500).ToListAsync(cancellationToken);
        return await RowsAsync(db, rows, scope, cancellationToken);
    }

    public async Task<IReadOnlyList<DisciplinaryRow>> GetForAgentAsync(string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DisciplinaryProceedings.AsNoTracking()
            .Where(d => d.AgentId == agentId
                && (d.Status == DisciplinaryStatus.Discontinued || d.Status == DisciplinaryStatus.Decided))
            .OrderByDescending(d => d.ClosedAt)
            .ToListAsync(cancellationToken);
        return await RowsAsync(db, rows, scope, cancellationToken);
    }

    private static async Task<IReadOnlyList<DisciplinaryRow>> RowsAsync(AppDbContext db, List<DisciplinaryProceeding> rows,
        ViewerScope scope, CancellationToken cancellationToken)
    {
        var visible = new List<DisciplinaryProceeding>(rows.Count);
        foreach (var row in rows)
        {
            if (await DisciplinaryRules.MaySeeAsync(db, row.AgentId, row.Status, scope, cancellationToken))
            {
                visible.Add(row);
            }
        }
        var names = await AdministrationService.NamesAsync(db, visible.Select(r => r.AgentId), cancellationToken);
        return visible
            .Select(r => new DisciplinaryRow(r.Id, r.CaseNumber, r.AgentId, names.GetValueOrDefault(r.AgentId, "—"),
                r.Status, r.Simplified, r.ProvisionallySuspended, r.OpenedAt, r.ClosedAt, r.Measure))
            .ToList();
    }

    public async Task<DisciplinaryView?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.DisciplinaryProceedings.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (row is null || !await DisciplinaryRules.MaySeeAsync(db, row.AgentId, row.Status, ViewerScope.From(actor), cancellationToken))
        {
            return null;
        }
        var agent = await db.Users.AsNoTracking()
            .Where(u => u.Id == row.AgentId)
            .Select(u => new { u.Codename, u.RealName, u.Rank })
            .FirstOrDefaultAsync(cancellationToken);
        var own = string.Equals(actor.GetAgentId(), row.AgentId, StringComparison.OrdinalIgnoreCase);
        var codename = agent?.Codename ?? "—";
        // the notice names the person; the clear name only where the viewer may read it
        var name = (own || actor.MayRealNameSee()) && !string.IsNullOrWhiteSpace(agent?.RealName) ? agent.RealName! : codename;
        var mayHandle = false;
        if (!own && actor.MayAdministrationWrite())
        {
            try
            {
                await DisciplinaryRules.RequireMayHandleAsync(db, actor, row.AgentId, cancellationToken);
                mayHandle = true;
            }
            catch (UnauthorizedAccessException) { /* ignore */ }
        }
        return new DisciplinaryView(row, codename, name, agent?.Rank, mayHandle);
    }

    public async Task<bool> QualifiesForSimplifiedAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rank = await db.Users.AsNoTracking().Where(u => u.Id == agentId).Select(u => u.Rank).FirstOrDefaultAsync(cancellationToken);
        var onProbation = await db.AgentProbations.AsNoTracking()
            .AnyAsync(p => p.AgentId == agentId && p.ClosedAt == null, cancellationToken);
        return DisciplinaryRules.MayBeSimplified(rank, onProbation);
    }

    public async Task<string> OpenAsync(DisciplinaryOpenInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        var grounds = Html(input.Grounds) ?? throw new InvalidOperationException("Bitte den Anlass des Verfahrens angeben.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await DisciplinaryRules.RequireMayHandleAsync(db, actor, input.AgentId, cancellationToken);
        var agent = await db.Users.AsNoTracking()
            .Where(u => u.Id == input.AgentId && u.Rank != null && u.PartnerAgency == null)
            .Select(u => new { u.Id, u.Rank })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Verfahren laufen nur gegen Agenten des FIB.");
        if (input.Simplified)
        {
            var onProbation = await db.AgentProbations.AsNoTracking()
                .AnyAsync(p => p.AgentId == agent.Id && p.ClosedAt == null, cancellationToken);
            if (!DisciplinaryRules.MayBeSimplified(agent.Rank, onProbation))
            {
                throw new InvalidOperationException("Das vereinfachte Anhörungsverfahren gilt nur für Trainees und Agenten in der Probezeit.");
            }
        }

        var row = new DisciplinaryProceeding
        {
            AgentId = agent.Id,
            Grounds = grounds,
            Simplified = input.Simplified,
            ProvisionallySuspended = input.ProvisionallySuspended,
            OpenedAt = DateTime.UtcNow,
            RightsNotice = DisciplinaryRules.DefaultRightsNotice,
        };
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        row.CaseNumber = await caseNumbers.NextAsync(db, CaseNumberPrefix, cancellationToken);
        db.DisciplinaryProceedings.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return row.Id;
    }

    public async Task UpdateGroundsAsync(string id, string grounds, bool provisionallySuspended, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        var text = Html(grounds) ?? throw new InvalidOperationException("Bitte den Anlass des Verfahrens angeben.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PendingAsync(db, id, actor, cancellationToken);
        row.Grounds = text;
        row.ProvisionallySuspended = provisionallySuspended;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSuspendedAsync(string id, bool suspended, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PendingAsync(db, id, actor, cancellationToken);
        row.Status = suspended ? DisciplinaryStatus.Suspended : DisciplinaryStatus.Open;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CloseAsync(string id, DisciplinaryCloseInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        var facts = input.Decide ? Html(input.Facts) : null;
        var reasoning = Html(input.Reasoning) ?? throw new InvalidOperationException("Bitte die Entscheidung begründen.");
        // a plain text field, shown as plain text
        var notice = AdministrationService.Trimmed(input.RightsNotice, DisciplinaryRules.MaxTextLength, "Die Belehrung");
        if (input.Decide)
        {
            if (input.Measure is null)
            {
                throw new InvalidOperationException("Bitte die verhängte Maßnahme wählen.");
            }
            if (facts is null)
            {
                throw new InvalidOperationException("Eine Disziplinarverfügung braucht eine Sachverhaltsdarstellung.");
            }
            if (notice is null)
            {
                throw new InvalidOperationException("Eine Disziplinarverfügung braucht eine Belehrung.");
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PendingAsync(db, id, actor, cancellationToken);
        row.Status = input.Decide ? DisciplinaryStatus.Decided : DisciplinaryStatus.Discontinued;
        row.Measure = input.Decide ? input.Measure : null;
        row.Facts = facts;
        row.Reasoning = reasoning;
        row.RightsNotice = input.Decide ? notice : null;
        row.ClosedAt = DateTime.UtcNow;
        row.DecidedByName = actor.GetCodename();
        row.ProvisionallySuspended = false;
        row.RankAtClose = await db.Users.AsNoTracking().Where(u => u.Id == row.AgentId).Select(u => u.Rank).FirstOrDefaultAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var title = input.Decide
                ? $"Gegen dich ist eine Disziplinarverfügung ergangen ({row.CaseNumber})."
                : $"Das Disziplinarverfahren {row.CaseNumber} gegen dich wurde eingestellt.";
            await notifications.NotifyManyInAppAsync(new[] { row.AgentId }, NotificationType.DisciplinaryConcluded, title,
                Href(row.Id), actor.GetAgentId(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Bell for closed proceeding {Id} failed.", row.Id);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.DisciplinaryProceedings.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }
        await DisciplinaryRules.RequireMayHandleAsync(db, actor, row.AgentId, cancellationToken);
        db.DisciplinaryProceedings.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<DisciplinaryProceeding>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.DisciplinaryProceedings.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.IsDeleted)
            .OrderByDescending(d => d.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.DisciplinaryProceedings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == id && d.IsDeleted, cancellationToken);
        if (row is null)
        {
            return;
        }
        await DisciplinaryRules.RequireMayHandleAsync(db, actor, row.AgentId, cancellationToken);
        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<DisciplinaryProceeding> PendingAsync(AppDbContext db, string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var row = await db.DisciplinaryProceedings.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Das Verfahren wurde nicht gefunden.");
        await DisciplinaryRules.RequireMayHandleAsync(db, actor, row.AgentId, cancellationToken);
        if (!DisciplinaryRules.IsPending(row.Status))
        {
            throw new InvalidOperationException("Das Verfahren ist bereits abgeschlossen.");
        }
        return row;
    }

    private static string? Html(string? html)
    {
        var clean = HtmlCleanup.Clean(html);
        if (HtmlCleanup.PlainText(clean).Length == 0)
        {
            return null;
        }
        if (clean.Length > DisciplinaryRules.MaxTextLength)
        {
            throw new InvalidOperationException($"Ein Text darf höchstens {DisciplinaryRules.MaxTextLength} Zeichen haben.");
        }
        return clean;
    }
}
