using System.Security.Claims;

namespace NOOSE_Website.Services;

/// <summary>Builds an agent's principal outside a request, from the account as it stands now.</summary>
/// <remarks>The same claims a login writes into the cookie, read fresh: a background run in an agent's name must
/// carry today's rank and flags, never the ones from when the job was set up.</remarks>
public interface IAgentPrincipalService
{
    /// <summary>The principal, or null when the account no longer exists.</summary>
    Task<ClaimsPrincipal?> BuildAsync(string agentId, CancellationToken cancellationToken = default);
}
