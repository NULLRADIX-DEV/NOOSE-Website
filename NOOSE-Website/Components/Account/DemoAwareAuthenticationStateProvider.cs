using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Account;

/// <summary>Carries auth state into interactive components and revalidates it (kill-switch). Never revalidates a demo principal away.</summary>
/// <remarks>
/// Registered only on instances that are not the demo (Program.cs). It used to present anonymous circuits as the
/// demo agent when the database flag DemoModusAktiv was on, which on production would have opened the real data to
/// every visitor. The demo instance has its own provider, see <see cref="NOOSE_Website.Infrastructure.DemoInstance"/>.
/// </remarks>
internal sealed class DemoAwareAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    // keep identical to the SecurityStampValidator interval in Program.cs
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // No SetAuthenticationState here on purpose (would re-notify mid-resolution).
        AuthenticationState? state = null;
        try
        {
            state = await base.GetAuthenticationStateAsync();
        }
        catch
        {
            /* circuit without a seeded auth state: stays anonymous */
        }

        return state ?? new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // the synthetic demo principal is static and always valid
        if (authenticationState.User.IsDemo())
        {
            return true;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Agent>>();
            return await ValidateAsync(userManager, authenticationState.User);
        }
        catch
        {
            /* transient DB fault: keep the session, the next tick decides */
            return true;
        }
    }

    private async Task<bool> ValidateAsync(UserManager<Agent> userManager, ClaimsPrincipal principal)
    {
        var agent = await userManager.GetUserAsync(principal);
        // not Active-only: an applicant and a citizen hold a real session too, and rejecting them here signed
        // every civilian circuit out within 30 seconds
        if (agent is null || !AgentStatusRules.MayHoldSession(agent.Status))
        {
            return false;
        }

        if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }

        var stampInCookie = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        var currentStamp = await userManager.GetSecurityStampAsync(agent);
        return stampInCookie == currentStamp;
    }
}
