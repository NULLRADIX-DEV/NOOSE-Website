using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NOOSE_Website.Authorization;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>The real route access check on the real policies.</summary>
public static class RouteAccessTestHost
{
    public static IRouteAccessService Create()
    {
        var provider = new ServiceCollection().AddLogging().AddNooseAuthorization().BuildServiceProvider();
        return new RouteAccessService(provider.GetRequiredService<IAuthorizationService>(),
            provider.GetRequiredService<IAuthorizationPolicyProvider>());
    }
}
