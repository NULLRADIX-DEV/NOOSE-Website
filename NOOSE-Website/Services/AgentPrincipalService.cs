using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using NOOSE_Website.Data.Entities;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IAgentPrincipalService" />
public class AgentPrincipalService(UserManager<Agent> users, IUserClaimsPrincipalFactory<Agent> claims)
    : IAgentPrincipalService
{
    public async Task<ClaimsPrincipal?> BuildAsync(string agentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var agent = await users.FindByIdAsync(agentId);
        // the login factory, so no second copy of the claim rules exists
        return agent is null ? null : await claims.CreateAsync(agent);
    }
}
