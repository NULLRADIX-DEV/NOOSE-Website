using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Administration;

/// <summary>Transfer requests and the transfer history.</summary>
public interface ITransferService
{
    /// <summary>Requests the viewer may see; <paramref name="agentId"/> narrows to one agent.</summary>
    Task<IReadOnlyList<TransferRow>> GetRequestsAsync(ClaimsPrincipal actor, string? agentId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TransferHistoryRow>> GetHistoryAsync(ClaimsPrincipal actor, string? agentId = null,
        CancellationToken cancellationToken = default);

    Task<string> RequestAsync(TransferInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task HearAsync(string id, string note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DecideAsync(string id, bool grant, string? note, bool confirmOverLimit, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITransferService" />
public class TransferService(IDbContextFactory<AppDbContext> dbFactory, IAgentManagementService agents,
    INotificationService notifications, ILogger<TransferService> logger) : ITransferService
{
    private const string Href = "/administration?tab=versetzungen";

    public async Task<IReadOnlyList<TransferRow>> GetRequestsAsync(ClaimsPrincipal actor, string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.TransferRequests.AsNoTracking();
        if (!string.IsNullOrEmpty(agentId))
        {
            query = query.Where(t => t.AgentId == agentId);
        }
        var rows = (await query.OrderBy(t => t.Status == TransferRequestStatus.Open || t.Status == TransferRequestStatus.Heard ? 0 : 1)
                .ThenByDescending(t => t.CreatedAt)
                .Take(300)
                .ToListAsync(cancellationToken))
            .Where(t => TransferRules.MaySee(scope, t.AgentId, t.CreatedById))
            .ToList();
        var names = await AdministrationService.NamesAsync(db, rows.Select(r => r.AgentId), cancellationToken);
        return rows
            .Select(t => new TransferRow(t.Id, t.AgentId, names.GetValueOrDefault(t.AgentId, "—"), t.FromDivision,
                t.ToDivision, t.Reason, t.Status, t.RequesterName, t.CreatedAt, t.HearingNote, t.HearingByName, t.HearingAt,
                t.DecisionNote, t.DecidedByName, t.DecidedAt, t.CreatedById))
            .ToList();
    }

    public async Task<IReadOnlyList<TransferHistoryRow>> GetHistoryAsync(ClaimsPrincipal actor, string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        var own = !string.IsNullOrEmpty(agentId) && string.Equals(scope.MeId, agentId, StringComparison.OrdinalIgnoreCase);
        if (!scope.MayAdministration && !own)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DivisionTransfers.AsNoTracking();
        if (!string.IsNullOrEmpty(agentId))
        {
            query = query.Where(t => t.AgentId == agentId);
        }
        var rows = await query.OrderByDescending(t => t.At).Take(300).ToListAsync(cancellationToken);
        var names = await AdministrationService.NamesAsync(db, rows.Select(r => r.AgentId), cancellationToken);
        return rows
            .Select(t => new TransferHistoryRow(t.AgentId, names.GetValueOrDefault(t.AgentId, "—"), t.FromDivision,
                t.ToDivision, t.At, t.ByName))
            .ToList();
    }

    public async Task<string> RequestAsync(TransferInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!TransferRules.MayRequest(actor, input.AgentId))
        {
            throw new UnauthorizedAccessException("Eine Versetzung beantragt der Agent selbst, die Führung oder die Administration.");
        }
        var reason = AdministrationService.Trimmed(input.Reason, TransferRules.MaxTextLength, "Die Begründung")
            ?? throw new InvalidOperationException("Bitte begründe den Antrag.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().OnlySelectable().FirstOrDefaultAsync(u => u.Id == input.AgentId, cancellationToken)
            ?? throw new InvalidOperationException("Versetzungen gibt es nur für aktive Agenten des FIB.");
        var from = DivisionRules.Of(agent).Home;
        if (from == input.ToDivision)
        {
            throw new InvalidOperationException("Der Agent gehört dieser Abteilung bereits an.");
        }
        DivisionRules.RequireMayHold(input.ToDivision, agent.Rank);
        if (await db.TransferRequests.AnyAsync(t => t.AgentId == agent.Id
                && (t.Status == TransferRequestStatus.Open || t.Status == TransferRequestStatus.Heard), cancellationToken))
        {
            throw new InvalidOperationException("Für diesen Agenten ist bereits ein Versetzungsantrag offen.");
        }
        var row = new TransferRequest
        {
            AgentId = agent.Id,
            FromDivision = from,
            ToDivision = input.ToDivision,
            Reason = reason,
            RequesterName = actor.GetCodename(),
        };
        db.TransferRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var recipients = await AdministrationService.MemberIdsAsync(db, cancellationToken);
            await notifications.NotifyManyInAppAsync(recipients, NotificationType.TransferRequested,
                $"Versetzungsantrag nach {DivisionDisplay.Name(input.ToDivision)} wartet auf die Anhörung.", Href,
                actor.GetAgentId(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Bell for transfer request {Id} failed.", row.Id);
        }
        return row.Id;
    }

    public async Task HearAsync(string id, string note, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        var text = AdministrationService.Trimmed(note, TransferRules.MaxTextLength, "Die Anhörung")
            ?? throw new InvalidOperationException("Bitte das Ergebnis der Anhörung festhalten.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PendingAsync(db, id, cancellationToken);
        if (!TransferRules.MayHear(actor, row.AgentId))
        {
            throw new UnauthorizedAccessException("Die eigene Anhörung führt jemand anderes.");
        }
        await PersonalRelationRules.RequireNotRelatedAsync(db, actor, row.AgentId, cancellationToken);
        row.HearingNote = text;
        row.HearingByName = actor.GetCodename();
        row.HearingAt = DateTime.UtcNow;
        row.Status = TransferRequestStatus.Heard;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var board = await db.Users.OnlySelectable()
                .Where(u => u.Rank >= DivisionRules.BoardFloor)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyInAppAsync(board, NotificationType.TransferRequested,
                "Ein angehörter Versetzungsantrag wartet auf die Entscheidung.", Href, actor.GetAgentId(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Board bell for transfer request {Id} failed.", row.Id);
        }
    }

    public async Task DecideAsync(string id, bool grant, string? note, bool confirmOverLimit, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireDivisionSet(actor);
        var text = AdministrationService.Trimmed(note, TransferRules.MaxTextLength, "Die Notiz");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PendingAsync(db, id, cancellationToken);
        if (!TransferRules.MayDecide(actor, row.AgentId))
        {
            throw new UnauthorizedAccessException("Über die eigene Versetzung entscheidest du nicht.");
        }
        await PersonalRelationRules.RequireNotRelatedAsync(db, actor, row.AgentId, cancellationToken);
        if (grant && row.Status != TransferRequestStatus.Heard)
        {
            throw new InvalidOperationException("Vor der Genehmigung hört die Administration den Agenten an.");
        }

        // claim first: a second decider finds the row closed and stops
        var outcome = grant ? TransferRequestStatus.Granted : TransferRequestStatus.Refused;
        var before = row.Status;
        var claimed = await db.TransferRequests
            .Where(t => t.Id == id && t.Status == before)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, outcome)
                .SetProperty(t => t.DecisionNote, text)
                .SetProperty(t => t.DecidedByName, actor.GetCodename())
                .SetProperty(t => t.DecidedAt, DateTime.UtcNow), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Über diesen Antrag wurde gerade entschieden.");
        }
        if (grant)
        {
            try
            {
                // the division write keeps the seat check
                await agents.DivisionSetAsync(row.AgentId, row.ToDivision, confirmOverLimit, actor, row.Id);
            }
            catch
            {
                // refused (seat limit, rank): the request goes back to where it was
                await db.TransferRequests.Where(t => t.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.Status, before)
                        .SetProperty(t => t.DecisionNote, (string?)null)
                        .SetProperty(t => t.DecidedByName, (string?)null)
                        .SetProperty(t => t.DecidedAt, (DateTime?)null), CancellationToken.None);
                throw;
            }
        }
        db.AuditLogs.Add(ManualAudit.Row(nameof(TransferRequest), id, AuditAction.Modified, actor,
            ManualAudit.Change("Status", AdministrationDisplay.Name(before), AdministrationDisplay.Name(outcome))));
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            var recipients = new List<string> { row.AgentId };
            if (!string.IsNullOrEmpty(row.CreatedById))
            {
                recipients.Add(row.CreatedById);
            }
            await notifications.NotifyManyInAppAsync(recipients.Distinct().ToList(), NotificationType.TransferDecided,
                grant
                    ? $"Die Versetzung nach {DivisionDisplay.Name(row.ToDivision)} wurde genehmigt."
                    : $"Die Versetzung nach {DivisionDisplay.Name(row.ToDivision)} wurde abgelehnt.",
                $"/personal/{row.AgentId}?tab=administration", actor.GetAgentId(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Bell for decided transfer {Id} failed.", row.Id);
        }
    }

    public async Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PendingAsync(db, id, cancellationToken);
        var me = actor.GetAgentId();
        var mine = string.Equals(me, row.AgentId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(me, row.CreatedById, StringComparison.OrdinalIgnoreCase);
        if (!mine && !actor.MayAdministrationWrite())
        {
            throw new UnauthorizedAccessException("Zurückziehen kann der Antragsteller, der Agent oder die Administration.");
        }
        row.Status = TransferRequestStatus.Withdrawn;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<TransferRequest> PendingAsync(AppDbContext db, string id, CancellationToken cancellationToken)
    {
        var row = await db.TransferRequests.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Der Antrag wurde nicht gefunden.");
        if (!TransferRules.IsPending(row.Status))
        {
            throw new InvalidOperationException("Über diesen Antrag wurde bereits entschieden.");
        }
        return row;
    }
}
