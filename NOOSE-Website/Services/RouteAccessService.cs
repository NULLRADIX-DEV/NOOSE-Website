using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Answers whether a viewer may open an address that <see cref="AuditEntityDisplay.Route"/> hands out.</summary>
public interface IRouteAccessService
{
    /// <summary>The routes among <paramref name="routes"/> the viewer may open; null entries and unknown pages never are.</summary>
    Task<IReadOnlySet<string>> OpenableAsync(ClaimsPrincipal user, IEnumerable<string?> routes,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRouteAccessService" />
/// <remarks>
/// Same rules the router applies: every authorize attribute of the page, combined, plus the policy of the section
/// when the page wraps it in an AuthorizeView (<see cref="SectionPolicies"/>). Nothing here compares roles.
/// </remarks>
public sealed class RouteAccessService(IAuthorizationService authorization, IAuthorizationPolicyProvider policies)
    : IRouteAccessService
{
    public async Task<IReadOnlySet<string>> OpenableAsync(ClaimsPrincipal user, IEnumerable<string?> routes,
        CancellationToken cancellationToken = default)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        // many rows point at the same page and section, so each is asked once
        var verdicts = new Dictionary<(Type Page, string? Section), bool>();
        foreach (var route in routes.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, tab) = Parse(route);
            var page = PageRouteTable.Find(path);
            if (page is null)
            {
                continue;
            }
            var sectionPolicy = tab is null ? null : SectionPolicies.For(path, tab);
            if (!verdicts.TryGetValue((page, sectionPolicy), out var allowed))
            {
                allowed = await PageAllowsAsync(page, user)
                    && (sectionPolicy is null || (await authorization.AuthorizeAsync(user, sectionPolicy)).Succeeded);
                verdicts[(page, sectionPolicy)] = allowed;
            }
            if (allowed)
            {
                open.Add(route);
            }
        }
        return open;
    }

    private async Task<bool> PageAllowsAsync(Type page, ClaimsPrincipal user)
    {
        var data = page.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
        if (page.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any() || data.Count == 0)
        {
            return true;
        }
        var policy = await AuthorizationPolicy.CombineAsync(policies, data);
        return policy is null || (await authorization.AuthorizeAsync(user, null, policy)).Succeeded;
    }

    private static (string Path, string? Tab) Parse(string route)
    {
        var split = route.IndexOf('?', StringComparison.Ordinal);
        if (split < 0)
        {
            return (route.TrimEnd('/'), null);
        }
        var path = route[..split].TrimEnd('/');
        var tab = route[(split + 1)..].Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == "tab")
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();
        return (path, tab);
    }
}
