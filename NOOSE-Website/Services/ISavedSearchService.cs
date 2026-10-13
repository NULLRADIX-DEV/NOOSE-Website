using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Saved searches/smart lists per agent (create, load own, delete own, watch own).</summary>
public interface ISavedSearchService
{
    Task<List<SavedSearch>> GetForAgentAsync(string agentId, CancellationToken cancellationToken = default);

    Task<SavedSearch> SaveAsync(string agentId, string name, SearchCriteria criteria, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, string agentId, CancellationToken cancellationToken = default);

    /// <summary>Switches the watch on one of the actor's own searches. Switching on starts from a fresh baseline.</summary>
    Task SetWatchedAsync(string id, bool watched, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Clears the new-hit marks the page just showed; marks a run added since stay.</summary>
    /// <remarks>Runs when the page is loaded, so it returns quietly for a viewer who may not write.</remarks>
    Task MarkSeenAsync(string id, string? shownUnseenJson, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
