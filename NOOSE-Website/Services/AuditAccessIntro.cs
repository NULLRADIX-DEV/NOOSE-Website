using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NOOSE_Website.Authorization;

namespace NOOSE_Website.Services;

/// <summary>What the intro text of the access log panel offers a viewer.</summary>
public static class AuditAccessIntro
{
    /// <summary>The pattern analysis the intro text points at.</summary>
    public const string CounterIntelRoute = "/nachweis?tab=lagebild";

    /// <summary>
    /// Whether the intro text links the counter-intelligence overview. The panel is open to leadership and
    /// read-only supervision, the overview only to <see cref="Policies.CounterIntel"/>, so everyone else
    /// would land on the first allowed section instead.
    /// </summary>
    public static async Task<bool> LinksCounterIntelAsync(IAuthorizationService authorization, ClaimsPrincipal user)
        => (await authorization.AuthorizeAsync(user, Policies.CounterIntel)).Succeeded;
}
