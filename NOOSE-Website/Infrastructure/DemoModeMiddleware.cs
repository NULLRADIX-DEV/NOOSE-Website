using Microsoft.Extensions.Configuration;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Infrastructure;

/// <summary>On the demo instance, presents anonymous visitors as the read-only demo agent so the whole app is browsable without login. Login and framework paths stay anonymous.</summary>
public sealed class DemoModeMiddleware(RequestDelegate next, IConfiguration configuration)
{
    // only the demo instance, never a database flag (see DemoInstance)
    private readonly bool _demoInstance = DemoInstance.IsDemo(configuration);

    // login + framework + asset paths must not be hijacked
    private static readonly string[] InfrastructurePrefixes =
    [
        "/Account", "/signin-discord", "/health", "/_blazor", "/_framework", "/system/logo",
    ];

    /// <summary>Infrastructure plus every public route: an anonymous visitor outside must stay anonymous.</summary>
    /// <remarks>
    /// Derived from <see cref="PublicRoutes"/> rather than repeated. /gesucht used to be listed by hand, and
    /// /gefasst is a sibling route rather than a child of it — it would have been missed the same way.
    /// </remarks>
    public static readonly string[] ExcludedPrefixes = [.. InfrastructurePrefixes, .. PublicRoutes.Prefixes];

    public async Task InvokeAsync(HttpContext context)
    {
        if (_demoInstance && context.User.Identity?.IsAuthenticated != true && !IsExcluded(context.Request.Path))
        {
            context.User = DemoIdentity.BuildPrincipal();
        }

        await next(context);
    }

    private static bool IsExcluded(PathString path)
    {
        foreach (var prefix in ExcludedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
