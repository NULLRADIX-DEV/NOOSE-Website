using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Jobs;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IJobService" />
public class JobService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications) : IJobService
{
    public async Task<List<JobRow>> GetTeamBoardAsync(bool onlyMy, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var meId = actor.GetAgentId();
        var query = db.Jobs.OnlyVisible(db, actor.MayClassifiedRead(), meId);
        if (onlyMy && !string.IsNullOrEmpty(meId))
        {
            query = query.Where(a => a.CreatedById == meId
                || db.JobAssignments.Any(z => z.JobId == a.Id && z.AgentId == meId));
        }
        return await RowsAsync(db, query, actor, meId, cancellationToken);
    }

    public async Task<List<JobRow>> GetMyOpenAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Jobs.OnlyVisible(db, actor.MayClassifiedRead(), meId)
            .Where(a => (a.Status == JobStatus.Open || a.Status == JobStatus.InProcessing)
                && db.JobAssignments.Any(z => z.JobId == a.Id && z.AgentId == meId));
        var rows = await RowsAsync(db, query, actor, meId, cancellationToken);
        // soonest due first, undated last, then the more urgent
        return rows
            .OrderBy(r => r.DueDate is null)
            .ThenBy(r => r.DueDate)
            .ThenByDescending(r => r.Priority)
            .ToList();
    }

    /// <summary>Board rows for a visible job query, newest change first.</summary>
    private static async Task<List<JobRow>> RowsAsync(AppDbContext db, IQueryable<Job> query, ClaimsPrincipal actor,
        string? meId, CancellationToken cancellationToken)
    {
        var isLeadership = actor.IsLeadership();
        // flat load, no collection-projection subselect (Pomelo)
        var rows = await query
            .OrderByDescending(a => a.ModifiedAt ?? a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.CaseNumber, a.Title, a.Status, a.Priority, a.DueDate, a.DoneAt, a.CreatedById,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new();
        }

        var ids = rows.Select(r => r.Id).ToList();
        // flat WHERE FK IN, no LATERAL
        var assignments = await db.JobAssignments
            .Where(z => ids.Contains(z.JobId))
            .Select(z => new { z.JobId, z.AgentId, Codename = z.Agent!.Codename })
            .ToListAsync(cancellationToken);
        var assignedPerJob = assignments
            .GroupBy(z => z.JobId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Codename).OrderBy(c => c).ToList());
        var myAssignments = string.IsNullOrEmpty(meId)
            ? new HashSet<string>()
            : assignments.Where(z => z.AgentId == meId).Select(z => z.JobId).ToHashSet();

        // codename is public, never real name
        var creatorIds = rows.Select(r => r.CreatedById).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var creatorNames = await db.Users
            .Where(u => creatorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Codename })
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);

        return rows.Select(r => new JobRow
        {
            Id = r.Id,
            CaseNumber = r.CaseNumber,
            Title = r.Title,
            Status = r.Status,
            Priority = r.Priority,
            DueDate = r.DueDate,
            DoneAt = r.DoneAt,
            CreatorCodename = r.CreatedById is not null && creatorNames.TryGetValue(r.CreatedById, out var name) ? name : null,
            AssignedCodenames = assignedPerJob.TryGetValue(r.Id, out var list) ? list : new List<string>(),
            // read-only supervision may change nothing
            MayStatusChange = actor.MayWrite() && (isLeadership || r.CreatedById == meId || myAssignments.Contains(r.Id)),
        }).ToList();
    }

    public async Task<Job?> GetDetailAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Jobs
            .OnlyVisible(db, actor.MayClassifiedRead(), actor.GetAgentId())
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<List<Job>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Jobs.IgnoreQueryFilters()
            .Where(a => a.IsDeleted)
            .OrderByDescending(a => a.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Job>> SearchAsync(string? searchText, bool mayAll, string? meId, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Jobs.OnlyVisible(db, mayAll, meId);

        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(a => a.Title.Contains(s) || a.CaseNumber.Contains(s));
        }

        return await query
            .OrderBy(a => a.Title)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<Job> CreateAsync(JobInput input, IReadOnlyList<string> agentIds,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var job = new Job
        {
            CaseNumber = await caseNumber.NextAsync(db, "A", cancellationToken),
            Title = input.Title.Trim(),
            Description = input.Description.TrimToNull(),
            Status = input.Status,
            Priority = input.Priority,
            DueDate = input.DueDate,
            IsRestricted = input.IsRestricted,
            DoneAt = JobStatusDisplay.IsCompleted(input.Status) ? DateTime.UtcNow : null,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // only selectable agents; silently dropped so a stale picker cannot fail the create
        var valid = agentIds.Count == 0
            ? new List<string>()
            : await db.Users.OnlySelectable()
                .Where(u => agentIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
        foreach (var agentId in valid.Distinct())
        {
            db.JobAssignments.Add(new JobAssignment { JobId = job.Id, AgentId = agentId });
        }
        if (valid.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        // notify after commit; creator excluded via triggerId, also pings assignees on Discord
        var creatorId = actor.GetAgentId();
        await notifications.NotifyManyAsync(valid.Distinct().ToList(), NotificationType.JobAssigned,
            $"Dir wurde eine Aufgabe zugewiesen: „{job.Title}“.", $"/aufgaben/{job.Id}",
            triggerId: creatorId, cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, job.Description, "einer Aufgabe",
            nameof(Job), job.Id, actor, cancellationToken);

        return job;
    }

    public async Task RefreshAsync(string id, JobInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aufgabe '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(job, actor);

        var alterStatus = job.Status;
        var oldMentions = job.Description;
        job.Title = input.Title.Trim();
        job.Description = input.Description.TrimToNull();
        job.Priority = input.Priority;
        // rescheduled due date re-arms the due reminders
        if (job.DueDate != input.DueDate)
        {
            job.DueReminderStage = JobDueReminderStage.None;
        }
        job.DueDate = input.DueDate;
        job.IsRestricted = input.IsRestricted;
        SetStatus(job, input.Status);
        await db.SaveChangesAsync(cancellationToken);

        await NotifyCreatorOnDoneAsync(job, alterStatus, actor, cancellationToken);
        await MentionNotify.DeltaAsync(notifications, oldMentions, job.Description, "einer Aufgabe",
            nameof(Job), job.Id, actor, cancellationToken);
    }

    public async Task StatusSetAsync(string id, JobStatus status, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aufgabe '{id}' nicht gefunden.");
        await RequireInvolvedOrLeadershipAsync(db, job, actor, cancellationToken);

        var alterStatus = job.Status;
        SetStatus(job, status);
        await db.SaveChangesAsync(cancellationToken);

        await NotifyCreatorOnDoneAsync(job, alterStatus, actor, cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aufgabe '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(job, actor);
        db.Jobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aufgabe '{id}' nicht gefunden.");

        job.IsDeleted = false;
        job.DeletedAt = null;
        job.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<JobAssignment>> GetAssignmentsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.JobAssignments
            .Where(z => z.JobId == jobId)
            .Include(z => z.Agent)
            .OrderBy(z => z.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task AgentAssignAsync(string jobId, string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.FirstOrDefaultAsync(a => a.Id == jobId, cancellationToken)
            ?? throw new InvalidOperationException($"Aufgabe '{jobId}' nicht gefunden.");
        RequireCreatorOrLeadership(job, actor);

        // same rule as the picker: a stale circuit must not assign a team lead, partner or ex-agent
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        if (await db.JobAssignments.AnyAsync(z => z.JobId == jobId && z.AgentId == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Agent ist der Aufgabe bereits zugewiesen.");
        }

        db.JobAssignments.Add(new JobAssignment { JobId = jobId, AgentId = agentId });
        await db.SaveChangesAsync(cancellationToken);

        if (agentId != actor.GetAgentId())
        {
            await notifications.NotifyManyAsync(new[] { agentId }, NotificationType.JobAssigned,
                $"Dir wurde eine Aufgabe zugewiesen: „{job.Title}“.", $"/aufgaben/{job.Id}",
                triggerId: actor.GetAgentId(), cancellationToken);
        }
    }

    public async Task AgentRemoveAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.JobAssignments
            .Include(z => z.Job)
            .FirstOrDefaultAsync(z => z.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return;
        }
        if (assignment.Job is not null)
        {
            RequireCreatorOrLeadership(assignment.Job, actor);
        }
        db.JobAssignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var assignmentIds = await db.JobAssignments
            .Where(z => z.JobId == jobId)
            .Select(z => z.Id)
            .ToListAsync(cancellationToken);

        // manual links touching this job as source or target, incl. removed
        var relationIds = await db.Links
            .IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == nameof(Job) && v.SourceId == jobId)
                 || (v.TargetType == nameof(Job) && v.TargetId == jobId)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string> { jobId };
        ids.UnionWith(assignmentIds);
        ids.UnionWith(relationIds);
        var types = new[] { nameof(Job), nameof(JobAssignment), nameof(Link) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> ReferenceDisplayAsync(string entityType, string entityId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // only resolve what the caller may see
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, actor.MayClassifiedRead(), cancellationToken, actor.GetAgentId()))
        {
            return null;
        }
        var map = await RecordsReference.ResolveAsync(db, new[] { (entityType, entityId) }, cancellationToken,
            mayAllTaskforces: actor.MayAllTaskforcesSee(), meId: actor.GetAgentId());
        return map.TryGetValue((entityType, entityId), out var a) ? a.Display : null;
    }

    /// <summary>Sets status and maintains the done timestamp (set on completion, cleared when reopened).</summary>
    private static void SetStatus(Job job, JobStatus @new)
    {
        var wasCompleted = JobStatusDisplay.IsCompleted(job.Status);
        var isCompleted = JobStatusDisplay.IsCompleted(@new);
        job.Status = @new;
        if (isCompleted && !wasCompleted)
        {
            job.DoneAt = DateTime.UtcNow;
        }
        else if (!isCompleted)
        {
            job.DoneAt = null;
        }
    }

    /// <summary>Notifies the creator when the job was just set to done (and they did not do it themselves).</summary>
    private async Task NotifyCreatorOnDoneAsync(Job job, JobStatus alterStatus,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (job.Status != JobStatus.Done || alterStatus == JobStatus.Done)
        {
            return;
        }
        var creatorId = job.CreatedById;
        if (!string.IsNullOrEmpty(creatorId) && creatorId != actor.GetAgentId())
        {
            await notifications.NotifyAsync(creatorId, NotificationType.JobAssigned,
                $"Aufgabe erledigt: „{job.Title}“.", $"/aufgaben/{job.Id}", cancellationToken);
        }
    }

    private static void RequireCreatorOrLeadership(Job job, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId) && job.CreatedById == meId)
        {
            return;
        }
        throw new UnauthorizedAccessException("Diese Aufgabe darf nur ihr Ersteller oder die Führung bearbeiten.");
    }

    private static async Task RequireInvolvedOrLeadershipAsync(AppDbContext db, Job job,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId)
            && (job.CreatedById == meId
                || await db.JobAssignments.AnyAsync(z => z.JobId == job.Id && z.AgentId == meId, cancellationToken)))
        {
            return;
        }
        throw new UnauthorizedAccessException("Den Status darf nur ein Beteiligter (Ersteller/Zugewiesener) oder die Führung ändern.");
    }
}
