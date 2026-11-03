using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IOperationService" />
public class OperationService(
    IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumber, IProfileSuggestionService suggestion,
    INotificationService notifications) : IOperationService
{
    private static string MentionScope(Operation o) => MentionNotify.Scope(o.Expiry, o.Result, o.Remarks);

    public async Task<List<Operation>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleOperations(db, scope)
            .Apply(filter)
            .OrderByDescending(o => o.ModifiedAt ?? o.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Operation?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (operation is null || !await Visibility.IsRecordVisibleAsync(db, nameof(Operation), id, scope, cancellationToken))
        {
            return null;
        }
        return operation;
    }

    public async Task<List<Operation>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Operations.IgnoreQueryFilters()
            .Where(o => o.IsDeleted)
            .OrderByDescending(o => o.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Operation>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // a picker must not offer a record that was filed away
        var query = db.Operations.OnlyActive().Where(o => isLeadership || !o.IsClassified);

        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(o => o.Title.Contains(s) || o.CaseNumber.Contains(s));
        }

        return await query
            .OrderBy(o => o.Title)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<Operation> CreateAsync(OperationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        ClassificationHelper.CheckRankGate(input.Classification, actor);
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var operation = new Operation
        {
            CaseNumber = await caseNumber.NextAsync(db, "OP", cancellationToken),
            Title = input.Title.Trim(),
            Type = input.Type.TrimToNull(),
            Status = input.Status,
            Location = input.Location.TrimToNull(),
            Start = input.Start,
            End = input.End,
            Expiry = input.Expiry.TrimToNull(),
            Result = input.Result.TrimToNull(),
            Remarks = input.Remarks.TrimToNull(),
            Classification = input.Classification,
            SecrecyLevel = input.SecrecyLevel,
        };

        if (input.Classification != Classification.Unknown)
        {
            db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Operation), operation.Id, input.Classification, input.ClassificationJustification, actor));
        }

        await SuggestionsStageAsync(db, operation.IsClassified, input.Type, cancellationToken);

        db.Operations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);

        // creator auto-assigned as investigation lead (ensures at least one)
        var creatorId = actor.GetAgentId();
        if (creatorId is not null)
        {
            db.OperationAgents.Add(new OperationAgent
            {
                OperationId = operation.Id,
                AgentId = creatorId,
                IsInvestigationLead = true,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, MentionScope(operation), "einer Operation",
            nameof(Operation), operation.Id, actor, cancellationToken);
        return operation;
    }

    public async Task RefreshAsync(string id, OperationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Operation '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);

        var oldMentions = MentionScope(operation);
        operation.Title = input.Title.Trim();
        operation.Type = input.Type.TrimToNull();
        operation.Status = input.Status;
        operation.Location = input.Location.TrimToNull();
        operation.Start = input.Start;
        operation.End = input.End;
        operation.Expiry = input.Expiry.TrimToNull();
        operation.Result = input.Result.TrimToNull();
        operation.Remarks = input.Remarks.TrimToNull();
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);
        CriticalChange.NoteSecrecy(db, nameof(Operation), id, operation.SecrecyLevel, input.SecrecyLevel, input.SecrecyReason, actor);
        operation.SecrecyLevel = input.SecrecyLevel;

        await SuggestionsStageAsync(db, operation.IsRestricted, input.Type, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, oldMentions, MentionScope(operation), "einer Operation",
            nameof(Operation), operation.Id, actor, cancellationToken);
    }

    public async Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var note = CriticalChange.RequireDeletionReason(reason);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Operation '{id}' nicht gefunden.");
        db.Operations.Remove(operation);
        CriticalChange.NoteDeletion(db, nameof(Operation), id, note, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Operation '{id}' nicht gefunden.");

        operation.IsDeleted = false;
        operation.DeletedAt = null;
        operation.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(string id, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => await SetArchivedAsync(id, true, reason, actor, cancellationToken);

    public async Task UnarchiveAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => await SetArchivedAsync(id, false, null, actor, cancellationToken);

    private async Task SetArchivedAsync(string id, bool archived, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        // a stock decision, not a rank decision
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // filing a record away hides it from everyone, so the actor must be able to see it first
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Operation), id, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        if (!await RecordArchive.SetArchivedAsync<Operation>(db, id, archived, reason, actor, cancellationToken))
        {
            return;
        }
        // ExecuteUpdate bypasses the audit interceptor, so the row is written by hand
        var note = archived && !string.IsNullOrWhiteSpace(reason)
            ? ManualAudit.Change("Archivgrund", null, reason.Trim())
            : null;
        db.AuditLogs.Add(ManualAudit.Row(nameof(Operation), id,
            archived ? AuditAction.Archived : AuditAction.Unarchived, actor, note));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        ClassificationHelper.CheckRankGate(@new, actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Operation '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);

        CriticalChange.NoteDowngrade(db, nameof(Operation), id, operation.Classification, @new, justification, actor);
        operation.Classification = @new;
        db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Operation), id, @new, justification, actor));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ClassificationHelper.HistoryAsync(db, nameof(Operation), id, scope, cancellationToken);
    }

    // scope-filtered operation query
    private static IQueryable<Operation> VisibleOperations(AppDbContext db, ViewerScope scope)
        => scope.PartnerAgency is { } agency
            ? db.Operations.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Operations.OnlyVisible(scope);

    public async Task<List<OperationAgent>> GetAgentsAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.OperationAgents
            .Where(a => a.OperationId == operationId)
            .Include(a => a.Agent)
            .OrderByDescending(a => a.IsInvestigationLead)
            .ThenBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<OperationAgent>> GetInvestigationLeadAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.OperationAgents
            .Where(a => a.OperationId == operationId && a.IsInvestigationLead)
            .Include(a => a.Agent)
            .OrderBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task AgentAllocateAsync(string operationId, string agentId, bool asInvestigationLead, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken)
            ?? throw new InvalidOperationException($"Operation '{operationId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);
        await RequireLeadershipOrELAsync(db, operationId, actor, cancellationToken);
        // only leadership may grant the lead flag
        if (asInvestigationLead)
        {
            Permission.RequireLeadership(actor);
        }
        // same rule as the picker: a stale circuit must not allocate a team lead, partner or ex-agent
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        if (await db.OperationAgents.AnyAsync(a => a.OperationId == operationId && a.AgentId == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Agent ist der Operation bereits zugeteilt.");
        }

        db.OperationAgents.Add(new OperationAgent
        {
            OperationId = operationId,
            AgentId = agentId,
            IsInvestigationLead = asInvestigationLead,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgentRemoveAsync(string allocationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.OperationAgents.Include(a => a.Operation).FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken);
        if (allocation is null)
        {
            return;
        }
        if (allocation.Operation is { } allocOperation)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, allocOperation.SecrecyLevel);
        }
        await RequireLeadershipOrELAsync(db, allocation.OperationId, actor, cancellationToken);
        db.OperationAgents.Remove(allocation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task InvestigationLeadSetAsync(string allocationId, bool @is, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // leadership-only
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.OperationAgents.FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken)
            ?? throw new InvalidOperationException("Zuteilung nicht gefunden.");
        allocation.IsInvestigationLead = @is;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Throws unless the actor is leadership or an investigation lead of this operation.</summary>
    private static async Task RequireLeadershipOrELAsync(AppDbContext db, string operationId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var agentId = actor.GetAgentId();
        var isEL = agentId is not null && await db.OperationAgents
            .AnyAsync(a => a.OperationId == operationId && a.AgentId == agentId && a.IsInvestigationLead, cancellationToken);
        if (!isEL)
        {
            throw new UnauthorizedAccessException(
                "Agents zuteilen oder entfernen dürfen nur die Führung oder ein Ermittlungsleiter dieser Akte.");
        }
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string operationId, bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Operation), operationId, isLeadership, cancellationToken))
        {
            return new();
        }
        var agentAllocationIds = await db.OperationAgents
            .Where(a => a.OperationId == operationId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        // manual links touching this operation, including removed ones so their delete entry shows
        var relationIds = await db.Links
            .IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == nameof(Operation) && v.SourceId == operationId)
                 || (v.TargetType == nameof(Operation) && v.TargetId == operationId)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string> { operationId };
        ids.UnionWith(agentAllocationIds);
        ids.UnionWith(relationIds);
        var types = new[] { nameof(Operation), nameof(OperationAgent), nameof(Link) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Stage the operation type for the shared suggestion catalog; classified records excluded. Caller persists it.</summary>
    private async Task SuggestionsStageAsync(AppDbContext db, bool isClassified, string? type, CancellationToken cancellationToken)
    {
        if (isClassified || string.IsNullOrWhiteSpace(type))
        {
            return;
        }
        await suggestion.StageAsync(db, SuggestionType.OperationType, new[] { type }, cancellationToken);
    }
}
