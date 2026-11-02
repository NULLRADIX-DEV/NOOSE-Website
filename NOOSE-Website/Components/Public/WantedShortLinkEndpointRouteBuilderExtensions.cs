using Microsoft.AspNetCore.Mvc;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Components.Public;

/// <summary>Short address from the wanted poster; redirects a live notice to its profile, 404 otherwise.</summary>
public static class WantedShortLinkEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapNooseWantedShortLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet(WantedShortLink.Prefix + "/{code}", async (
            string code,
            [FromServices] IPublicWantedService wanted,
            CancellationToken cancellationToken) =>
        {
            // one 404 for every miss
            var path = await wanted.ResolveShortLinkAsync(code, cancellationToken);
            return path is null ? Results.NotFound() : Results.LocalRedirect(path);
        })
        .AllowAnonymous();
    }
}
