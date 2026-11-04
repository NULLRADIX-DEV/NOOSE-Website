using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Common;

/// <summary>The personal calendar feed; the secret in the path is the only credential, so this is deliberately anonymous.</summary>
public static class CalendarFeedEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapNooseCalendarFeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(CalendarFeedRules.SiteRoute);

        group.MapGet("/{file}", async (
            string file,
            [FromServices] ICalendarFeedService feed,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var token = CalendarFeedRules.TokenFromSegment(file);
            var body = token is null ? null : await feed.RenderAsync(token, cancellationToken);
            if (body is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.ContentDisposition = "inline; filename=\"fib-kalender.ics\"";
            return Results.Text(body, IcsWriter.ContentType);
        })
        .AllowAnonymous();

        return group;
    }
}

/// <summary>Limits for the feed route: a tight bucket per secret and a looser one per caller.</summary>
public static class CalendarFeedRateLimits
{
    public const int PerSecretPerMinute = 6;
    public const int PerCallerPerMinute = 120;

    public static bool Applies(HttpContext context)
        => context.Request.Path.StartsWithSegments(CalendarFeedRules.SiteRoute, StringComparison.OrdinalIgnoreCase);

    public static RateLimitPartition<string> PerSecret(HttpContext context)
    {
        if (!Applies(context))
        {
            return RateLimitPartition.GetNoLimiter<string>("kalender-abo-frei");
        }
        var segment = context.Request.Path.Value![CalendarFeedRules.SiteRoute.Length..].Trim('/');
        // a path without the secret shape shares one bucket per caller instead of minting a bucket per garbage string
        var token = CalendarFeedRules.TokenFromSegment(segment);
        var key = token is null ? "ungueltig:" + Caller(context) : "geheim:" + CalendarFeedToken.Hash(token)[..16];
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => Window(PerSecretPerMinute));
    }

    public static RateLimitPartition<string> PerCaller(HttpContext context)
        => Applies(context)
            ? RateLimitPartition.GetFixedWindowLimiter("kalender-abo-ip:" + Caller(context), _ => Window(PerCallerPerMinute))
            : RateLimitPartition.GetNoLimiter<string>("kalender-abo-frei");

    private static string Caller(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };
}
