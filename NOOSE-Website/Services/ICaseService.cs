using System.Security.Claims;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Cases;

namespace NOOSE_Website.Services;

/// <summary>Case records: list/detail (classified-filtered), CRUD, trash, rank-gated classification, involved agents (with case lead), and history.</summary>
public interface ICaseService
{
    Task<List<Case>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default);
    Task<Case?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default);
    Task<List<Case>> GetTrashAsync(CancellationToken cancellationToken = default);
    Task<List<Case>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>Search cases the viewer may see per their full secrecy scope (TRU/HRB audience, not just leadership).</summary>
    Task<List<Case>> SearchAsync(string? searchText, ViewerScope scope, int max = 20, CancellationToken cancellationToken = default);

    Task<Case> CreateAsync(CaseInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RefreshAsync(string id, CaseInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Moves the record to the trash; the reason is required and lands on its timeline.</summary>
    Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Files the record away: out of listings, pickers and the default search, still fully readable.</summary>
    Task ArchiveAsync(string id, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Brings the record back into the active stock.</summary>
    Task UnarchiveAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Set the classification; "secured state-threatening" requires Senior Special Agent+ or Admin, a downgrade a justification.</summary>
    Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default);

    /// <summary>Agents assigned to the case (with agent data; case lead first).</summary>
    Task<List<CaseAgent>> GetAgentsAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>Assignments marked as case lead (with agent data).</summary>
    Task<List<CaseAgent>> GetCaseLeadAsync(string caseId, CancellationToken cancellationToken = default);

    /// <summary>Assign an agent; allowed for leadership or a case lead; marking as case lead is leadership-only.</summary>
    Task AgentAllocateAsync(string caseId, string agentId, bool asCaseLead, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Remove an assignment; allowed for leadership or a case lead.</summary>
    Task AgentRemoveAsync(string allocationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Set/clear the case-lead mark on an assignment; leadership only.</summary>
    Task CaseLeadSetAsync(string allocationId, bool @is, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Audit entries for the case and its assignments/links (classified-filtered).</summary>
    Task<List<AuditLog>> GetHistoryAsync(string caseId, bool isLeadership, CancellationToken cancellationToken = default);
}
