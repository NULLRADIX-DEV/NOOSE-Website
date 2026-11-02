using System.Security.Claims;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Personnel file per agent: rank history (read), notes, and promotion requests. Rank changes live in the agent management service (SecurityStamp/UserManager).</summary>
public interface IPersonnelFileService
{
    Task<List<AgentRankHistory>> GetRankHistoryAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Personnel-file entries; kind == null returns every kind (newest entry-date first).</summary>
    Task<List<AgentNote>> GetNotesAsync(string agentId, AgentNoteKind? kind = null, CancellationToken cancellationToken = default);
    /// <summary>Create a personnel-file entry - leadership only; posts a Discord embed pinging the subject.</summary>
    Task<AgentNote> NoteCreateAsync(string agentId, AgentNoteKind kind, string? artFreetext, DateTime entryDate,
        IReadOnlyCollection<string> executorAgentIds, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Delete a note - author or leadership.</summary>
    Task NoteDeleteAsync(string noteId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<AgentPromotionRequest>> GetPromotionRequestsAsync(string agentId, CancellationToken cancellationToken = default);
    /// <summary>Open promotion requests the actor may decide (for the inbox): a Section Chief sees the own division, the Board and admins all, everyone else none.</summary>
    Task<List<AgentPromotionRequest>> GetOpenPromotionRequestsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Count of <see cref="GetOpenPromotionRequestsAsync"/>; the badge and the dashboard read this, so they never disagree with the list.</summary>
    Task<int> CountOpenPromotionRequestsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Request a promotion - leadership only; one open request per agent.</summary>
    Task<AgentPromotionRequest> PromotionRequestAsync(string agentId, Rank targetRank, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
