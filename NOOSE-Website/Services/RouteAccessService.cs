using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Whether a viewer may open an address from <see cref="AuditEntityDisplay.Route"/>.</summary>
public interface IRouteAccessService
{
    /// <summary>The routes among <paramref name="routes"/> the viewer may open.</summary>
    Task<IReadOnlySet<string>> OpenableAsync(ClaimsPrincipal user, IEnumerable<string?> routes,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRouteAccessService" />
public sealed class RouteAccessService(IAuthorizationService authorization, IAuthorizationPolicyProvider policies,
    IPartnerAgencyProfileService partnerProfiles, IPartnerVisibilityPolicyService partnerPolicy)
    : IRouteAccessService
{
    public async Task<IReadOnlySet<string>> OpenableAsync(ClaimsPrincipal user, IEnumerable<string?> routes,
        CancellationToken cancellationToken = default)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        // verdict cache
        var verdicts = new Dictionary<(Type Page, string? Section), bool>();
        PartnerGate? partner = null;
        foreach (var route in routes.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // follow legacy redirects
            var (path, tab) = Parse(LegacyRoutes.Target(route) ?? route);
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
            if (allowed && user.IsPartner())
            {
                // once per call
                partner ??= new PartnerGate(await partnerProfiles.GetFeaturesAsync(user, cancellationToken),
                    await partnerPolicy.GetAllowedTypesAsync(user, cancellationToken));
                // layout-relative path
                allowed = !PartnerRoutes.IsBlocked(path.TrimStart('/'), partner.Features, partner.AllowedTypes);
            }
            if (allowed)
            {
                open.Add(route);
            }
        }
        return open;
    }

    private sealed record PartnerGate(PartnerFeature Features, IReadOnlySet<string>? AllowedTypes);

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
