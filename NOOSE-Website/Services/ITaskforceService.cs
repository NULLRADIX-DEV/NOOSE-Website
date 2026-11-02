using System.Security.Claims;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>Taskforce business logic: list, detail, CRUD, approval, agents, history.</summary>
public interface ITaskforceService
{
    Task<List<Taskforce>> GetListAsync(bool mayAll, string? meId, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);
    Task<Taskforce?> GetDetailAsync(string id, bool mayAll, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);
    Task<List<Taskforce>> GetTrashAsync(CancellationToken cancellationToken = default);
    Task<List<Taskforce>> SearchAsync(string? searchText, bool mayAll, string? meId, int max = 20, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null);

    /// <summary>Requested taskforces, oldest first.</summary>
    Task<List<Taskforce>> GetRequestedAsync(CancellationToken cancellationToken = default);

    Task<Taskforce> CreateAsync(TaskforceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RefreshAsync(string id, TaskforceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Moves the record to the trash; the reason is required and lands on its timeline.</summary>
    Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Files the record away: out of listings, pickers and the default search, still fully readable.</summary>
    Task ArchiveAsync(string id, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Brings the record back into the active stock.</summary>
    Task UnarchiveAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Set approval status, leadership only.</summary>
    Task ApprovalSetAsync(string id, TaskforceStatus @new, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Assigned agents, leads first.</summary>
    Task<List<TaskforceAgent>> GetAgentsAsync(string taskforceId, CancellationToken cancellationToken = default);

    /// <summary>Lead agents only.</summary>
    Task<List<TaskforceAgent>> GetLeadAsync(string taskforceId, CancellationToken cancellationToken = default);

    /// <summary>Allocate agent as member.</summary>
    Task AgentAllocateAsync(string taskforceId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Remove agent allocation.</summary>
    Task AgentRemoveAsync(string allocationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Set allocation role, leadership only.</summary>
    Task RoleSetAsync(string allocationId, TaskforceRole role, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Audit history for taskforce and allocations.</summary>
    Task<List<AuditLog>> GetHistoryAsync(string taskforceId, bool mayAll, string? meId, CancellationToken cancellationToken = default);
}
