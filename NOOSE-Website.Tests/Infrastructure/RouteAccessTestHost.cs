using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>The real route access check on the real policies.</summary>
public static class RouteAccessTestHost
{
    /// <summary>Route access with stubbed partner functions and types.</summary>
    public static IRouteAccessService Create(PartnerFeature features = PartnerFeature.None,
        IReadOnlySet<string>? allowedTypes = null)
    {
        var profiles = Substitute.For<IPartnerAgencyProfileService>();
        profiles.GetFeaturesAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(features);
        var policy = Substitute.For<IPartnerVisibilityPolicyService>();
        policy.GetAllowedTypesAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(allowedTypes);
        return Create(profiles, policy);
    }

    public static IRouteAccessService Create(IPartnerAgencyProfileService profiles, IPartnerVisibilityPolicyService policy)
    {
        var provider = new ServiceCollection().AddLogging().AddNooseAuthorization().BuildServiceProvider();
        return new RouteAccessService(provider.GetRequiredService<IAuthorizationService>(),
            provider.GetRequiredService<IAuthorizationPolicyProvider>(), profiles, policy);
    }
}
