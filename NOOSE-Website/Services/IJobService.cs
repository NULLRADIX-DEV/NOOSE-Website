using System.Security.Claims;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Jobs;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Tasks/to-dos team board: all active agents see all jobs (no classification concept).</summary>
public interface IJobService
{
    /// <summary>Team board: all jobs, optionally only own (creator or assigned). Newest first.</summary>
    Task<List<JobRow>> GetTeamBoardAsync(bool onlyMy, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Open jobs assigned to the caller, soonest due first.</summary>
    Task<List<JobRow>> GetMyOpenAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Loads a job; null when restricted and not visible to the caller.</summary>
    Task<Job?> GetDetailAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<Job>> GetTrashAsync(CancellationToken cancellationToken = default);
    /// <summary>Job search for pickers; restricted jobs only for participants/supervision (mayAll = may read classified).</summary>
    Task<List<Job>> SearchAsync(string? searchText, bool mayAll, string? meId, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>Create a job, assign it to the given active agents and notify them (except the creator).</summary>
    Task<Job> CreateAsync(JobInput input, IReadOnlyList<string> agentIds, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RefreshAsync(string id, JobInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Set status (creator, assignee or leadership); "Done" notifies the creator.</summary>
    Task StatusSetAsync(string id, JobStatus status, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Agents assigned to the job (by codename).</summary>
    Task<List<JobAssignment>> GetAssignmentsAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Assign an agent (creator or leadership); notifies the agent unless they are the actor.</summary>
    Task AgentAssignAsync(string jobId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task AgentRemoveAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Audit entries for the job incl. assignments/links.</summary>
    Task<List<AuditLog>> GetHistoryAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Public display name of a referenced record if visible to the caller; else null. Never a real name.</summary>
    Task<string?> ReferenceDisplayAsync(string entityType, string entityId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <summary>Row/card for the jobs team board (public codenames only). Class so the kanban board can update Status optimistically on drag.</summary>
public sealed class JobRow
{
    public string Id { get; set; } = string.Empty;
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public JobPriority Priority { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? CreatorCodename { get; set; }
    public IReadOnlyList<string> AssignedCodenames { get; set; } = Array.Empty<string>();
    public bool MayStatusChange { get; set; }
}
