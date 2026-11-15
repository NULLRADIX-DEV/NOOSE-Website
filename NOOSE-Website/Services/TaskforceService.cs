using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Taskforces;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ITaskforceService" />
public class TaskforceService(
    IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumber, INotificationService notifications)
    : ITaskforceService
{
    private static string MentionScope(Taskforce t) => MentionNotify.Scope(t.Purpose, t.Remarks);

    public async Task<List<Taskforce>> GetListAsync(bool mayAll, string? meId, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = partnerAgency is { } agency
            ? db.Taskforces.OnlyPartnerVisible(db, agency, partnerAgentId)
            : db.Taskforces.OnlyVisible(db, mayAll, meId);
        return await query
            .Apply(filter)
            .OrderByDescending(t => t.ModifiedAt ?? t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Taskforce?> GetDetailAsync(string id, bool mayAll, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (partnerAgency is { } agency)
        {
            // partners: only released, non-classified taskforces
            return await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Taskforce), id, agency, partnerAgentId, cancellationToken)
                ? await db.Taskforces.Include(t => t.PartnerAgencies).FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
                : null;
        }
        var taskforce = await db.Taskforces.Include(t => t.PartnerAgencies).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (taskforce is null)
        {
            return null;
        }
        // visible only to leadership/admin or assigned agents
        if (!mayAll
            && !(meId is not null && await db.TaskforceAgents.AnyAsync(a => a.TaskforceId == id && a.AgentId == meId, cancellationToken)))
        {
            return null;
        }
        return taskforce;
    }

    public async Task<List<Taskforce>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Taskforces.IgnoreQueryFilters()
            .Where(t => t.IsDeleted)
            .OrderByDescending(t => t.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Taskforce>> SearchAsync(string? searchText, bool mayAll, string? meId, int max = 20, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = partnerAgency is { } agency
            ? db.Taskforces.OnlyPartnerVisible(db, agency, partnerAgentId)
            : db.Taskforces.OnlyVisible(db, mayAll, meId);

        // a picker must not offer a record that was filed away
        query = query.OnlyActive();

        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(t => t.Name.Contains(s) || t.CaseNumber.Contains(s));
        }

        return await query
            .OrderBy(t => t.Name)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Taskforce>> GetRequestedAsync(CancellationToken cancellationToken = default)
    {
        // leadership-gated approval inbox, no classification filter needed
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Taskforces
            .Where(t => t.Status == TaskforceStatus.Requested)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Taskforce> CreateAsync(TaskforceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var taskforce = new Taskforce
        {
            CaseNumber = await caseNumber.NextAsync(db, "TF", cancellationToken),
            Name = input.Name.Trim(),
            Purpose = input.Purpose.TrimToNull(),
            Scope = input.Scope,
            Status = TaskforceStatus.Requested,
            Remarks = input.Remarks.TrimToNull(),
            IsClassified = input.IsClassified,
        };

        db.Taskforces.Add(taskforce);
        // the creator leads it, so the creator may name the agencies taking part
        SetPartnerAgencies(db, taskforce, input);
        await db.SaveChangesAsync(cancellationToken);

        // auto-assign creator as lead, so a lead always exists
        var creatorId = actor.GetAgentId();
        if (creatorId is not null)
        {
            db.TaskforceAgents.Add(new TaskforceAgent
            {
                TaskforceId = taskforce.Id,
                AgentId = creatorId,
                Role = TaskforceRole.LeadInvestigator,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, MentionScope(taskforce), "einer Taskforce",
            nameof(Taskforce), taskforce.Id, actor, cancellationToken);
        return taskforce;
    }

    public async Task RefreshAsync(string id, TaskforceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var taskforce = await db.Taskforces.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Taskforce '{id}' nicht gefunden.");

        if (taskforce.IsClassified && !actor.IsLeadership())
        {
            throw new UnauthorizedAccessException("Diese Akte ist als Verschlusssache nur für die Führung zugänglich.");
        }

        var oldMentions = MentionScope(taskforce);
        taskforce.Name = input.Name.Trim();
        taskforce.Purpose = input.Purpose.TrimToNull();
        taskforce.Scope = input.Scope;
        taskforce.Remarks = input.Remarks.TrimToNull();
        var current = await db.TaskforcePartnerAgencies.Where(p => p.TaskforceId == id).Select(p => p.Agency).ToListAsync(cancellationToken);
        var wanted = WantedAgencies(input);
        if (!current.ToHashSet().SetEquals(wanted))
        {
            // which agencies take part decides who sees its investigations: leadership or the taskforce lead
            if (!actor.IsLeadership() && !await db.TaskforceAgents.AnyAsync(
                    a => a.TaskforceId == id && a.AgentId == actor.GetAgentId() && a.Role != TaskforceRole.Member, cancellationToken))
            {
                throw new UnauthorizedAccessException("Beteiligte Behörden ändert nur die Führung oder die Leitung der Taskforce.");
            }
            await db.Entry(taskforce).Collection(t => t.PartnerAgencies).LoadAsync(cancellationToken);
            SetPartnerAgencies(db, taskforce, input);
        }
        CriticalChange.NoteSecrecy(db, nameof(Taskforce), id, CriticalChange.TaskforceLevel(taskforce.IsClassified),
            CriticalChange.TaskforceLevel(input.IsClassified), input.SecrecyReason, actor);
        taskforce.IsClassified = input.IsClassified;

        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, oldMentions, MentionScope(taskforce), "einer Taskforce",
            nameof(Taskforce), id, actor, cancellationToken);
    }

    /// <summary>Agencies taking part; only a cross-agency taskforce has any.</summary>
    private static HashSet<PartnerAgency> WantedAgencies(TaskforceInput input)
        => input.Scope == TaskforceScope.CrossAgency ? input.PartnerAgencies.ToHashSet() : new HashSet<PartnerAgency>();

    private static void SetPartnerAgencies(AppDbContext db, Taskforce taskforce, TaskforceInput input)
    {
        var wanted = WantedAgencies(input);
        foreach (var row in taskforce.PartnerAgencies.Where(p => !wanted.Contains(p.Agency)).ToList())
        {
            db.TaskforcePartnerAgencies.Remove(row);
        }
        foreach (var agency in wanted.Where(a => taskforce.PartnerAgencies.All(p => p.Agency != a)))
        {
            db.TaskforcePartnerAgencies.Add(new TaskforcePartnerAgency { TaskforceId = taskforce.Id, Agency = agency });
        }
    }

    public async Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var note = CriticalChange.RequireDeletionReason(reason);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var taskforce = await db.Taskforces.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Taskforce '{id}' nicht gefunden.");
        // a soft delete would strand its investigations without anyone seeing them through the taskforce
        if (await db.Investigations.AnyAsync(i => i.TaskforceId == id
                && (i.Status == InvestigationStatus.Investigating || i.Status == InvestigationStatus.Indicted || i.Status == InvestigationStatus.Trial), cancellationToken))
        {
            throw new InvalidOperationException("Die Taskforce hat noch laufende Ermittlungen. Erst abschließen oder an eine andere Taskforce hängen.");
        }
        db.Taskforces.Remove(taskforce);
        CriticalChange.NoteDeletion(db, nameof(Taskforce), id, note, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var taskforce = await db.Taskforces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Taskforce '{id}' nicht gefunden.");

        taskforce.IsDeleted = false;
        taskforce.DeletedAt = null;
        taskforce.DeletedById = null;
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
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), id, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        if (!await RecordArchive.SetArchivedAsync<Taskforce>(db, id, archived, reason, actor, cancellationToken))
        {
            return;
        }
        // ExecuteUpdate bypasses the audit interceptor, so the row is written by hand
        var note = archived && !string.IsNullOrWhiteSpace(reason)
            ? ManualAudit.Change("Archivgrund", null, reason.Trim())
            : null;
        db.AuditLogs.Add(ManualAudit.Row(nameof(Taskforce), id,
            archived ? AuditAction.Archived : AuditAction.Unarchived, actor, note));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ApprovalSetAsync(string id, TaskforceStatus @new, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // approve/reject/dissolve is leadership-only
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var taskforce = await db.Taskforces.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Taskforce '{id}' nicht gefunden.");

        taskforce.Status = @new;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<TaskforceAgent>> GetAgentsAsync(string taskforceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TaskforceAgents
            .Where(a => a.TaskforceId == taskforceId)
            .Include(a => a.Agent)
            // leads first, then by role, then codename
            .OrderBy(a => a.Role == TaskforceRole.Member)
            .ThenBy(a => a.Role)
            .ThenBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<TaskforceAgent>> GetLeadAsync(string taskforceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TaskforceAgents
            .Where(a => a.TaskforceId == taskforceId && a.Role != TaskforceRole.Member)
            .Include(a => a.Agent)
            .OrderBy(a => a.Role)
            .ThenBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task AgentAllocateAsync(string taskforceId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var taskforce = await db.Taskforces.FirstOrDefaultAsync(t => t.Id == taskforceId, cancellationToken)
            ?? throw new InvalidOperationException($"Taskforce '{taskforceId}' nicht gefunden.");
        if (taskforce.IsClassified && !actor.IsLeadership())
        {
            throw new UnauthorizedAccessException("Diese Akte ist als Verschlusssache nur für die Führung zugänglich.");
        }
        await RequireLeadershipOrLeadAsync(db, taskforceId, actor, cancellationToken);
        // partner read access to a taskforce comes from PartnerShare, never from a membership row
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        if (await db.TaskforceAgents.AnyAsync(a => a.TaskforceId == taskforceId && a.AgentId == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Agent ist der Taskforce bereits zugeteilt.");
        }

        db.TaskforceAgents.Add(new TaskforceAgent
        {
            TaskforceId = taskforceId,
            AgentId = agentId,
            Role = TaskforceRole.Member,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgentRemoveAsync(string allocationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.TaskforceAgents.Include(a => a.Taskforce).FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken);
        if (allocation is null)
        {
            return;
        }
        if (allocation.Taskforce?.IsClassified == true && !actor.IsLeadership())
        {
            throw new UnauthorizedAccessException("Diese Akte ist als Verschlusssache nur für die Führung zugänglich.");
        }
        await RequireLeadershipOrLeadAsync(db, allocation.TaskforceId, actor, cancellationToken);
        db.TaskforceAgents.Remove(allocation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RoleSetAsync(string allocationId, TaskforceRole role, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // assigning roles/leads is leadership-only
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.TaskforceAgents.FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken)
            ?? throw new InvalidOperationException("Zuteilung nicht gefunden.");
        allocation.Role = role;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Throws unless the actor is leadership or a lead of this taskforce.</summary>
    private static async Task RequireLeadershipOrLeadAsync(AppDbContext db, string taskforceId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var agentId = actor.GetAgentId();
        var isLead = agentId is not null && await db.TaskforceAgents
            .AnyAsync(a => a.TaskforceId == taskforceId && a.AgentId == agentId && a.Role != TaskforceRole.Member, cancellationToken);
        if (!isLead)
        {
            throw new UnauthorizedAccessException(
                "Agents zuteilen oder entfernen dürfen nur die Führung oder die Leitung dieser Taskforce.");
        }
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string taskforceId, bool mayAll, string? meId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), taskforceId, mayAll, cancellationToken, meId))
        {
            return new();
        }
        var agentAllocationIds = await db.TaskforceAgents
            .Where(a => a.TaskforceId == taskforceId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        // manual links touching this taskforce, including removed ones so their "removed" entry shows
        var relationIds = await db.Links
            .IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == nameof(Taskforce) && v.SourceId == taskforceId)
                 || (v.TargetType == nameof(Taskforce) && v.TargetId == taskforceId)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string> { taskforceId };
        ids.UnionWith(agentAllocationIds);
        ids.UnionWith(relationIds);
        var types = new[] { nameof(Taskforce), nameof(TaskforceAgent), nameof(Link) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }
}
