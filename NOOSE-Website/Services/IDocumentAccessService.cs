using System.Security.Claims;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>One internal agent on a document's access list; Excluded = access currently revoked.</summary>
public record DocumentAccessEntry(string AgentId, string Codename, string? RealName, Rank? Rank, bool Excluded, OrgUnit Unit = default);

/// <summary>Materializes who has access to a document and manages per-agent revocations.</summary>
public interface IDocumentAccessService
{
    /// <summary>True if the actor may view and manage this document's access list.</summary>
    Task<bool> CanManageAccessAsync(string documentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Internal active agents with role-based access, each flagged if currently revoked.</summary>
    Task<IReadOnlyList<DocumentAccessEntry>> GetAccessListAsync(string documentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Revoke one agent's access to a document.</summary>
    Task RevokeAsync(string documentId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Restore a previously revoked agent's access.</summary>
    Task RestoreAsync(string documentId, string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
