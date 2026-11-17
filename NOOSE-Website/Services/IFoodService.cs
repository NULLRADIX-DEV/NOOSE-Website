using System.Security.Claims;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <summary>Food requests: requested → approved/rejected → issued, with the issue booked as a withdrawal from the evidence room.</summary>
public interface IFoodService
{
    /// <summary>The caller's own requests, newest first.</summary>
    Task<List<FoodRequestDisplay>> GetOwnAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Requests visible to the caller, optionally filtered by stage.</summary>
    Task<List<FoodRequestDisplay>> GetVisibleAsync(FoodRequestStatus? status, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Recent requests of one agent for the personnel file; empty when the caller may not see them.</summary>
    Task<List<FoodRequestDisplay>> GetForAgentAsync(string agentId, int max, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Requests awaiting a decision (leadership inbox and nav badge).</summary>
    Task<int> GetOpenCountAsync(CancellationToken cancellationToken = default);

    Task<FoodRequest?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The request linked to an evidence-room entry, if any and visible.</summary>
    Task<FoodRequest?> GetByEvidenceEntryAsync(string evidenceEntryId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<FoodRequest> CreateAsync(FoodRequestInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Decides a requested (or previously rejected) request; quantities may be cut per line.</summary>
    Task DecideAsync(string id, bool approved, FoodDecisionInput decision, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The requester pulls their own still-open request.</summary>
    Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Takes an approval back (to requested, or straight to rejected) and frees the weekly limit.</summary>
    Task RevokeApprovalAsync(string id, bool reject, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Issues the approved quantities as one withdrawal from the FIB stock; booking and request commit together.</summary>
    Task IssueAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Reverses an issue: the withdrawal is removed and the request goes back to approved.</summary>
    Task CancelIssueAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<FoodRequest>> GetTrashAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
