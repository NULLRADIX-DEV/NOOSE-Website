namespace NOOSE_Website.Services.Public;

/// <summary>The single truth about which routes are meant for the outside world.</summary>
/// <remarks>
/// Drives the <c>X-Robots-Tag</c> middleware and is what <c>wwwroot/robots.txt</c> is checked against. The module
/// nav routes are taken from <see cref="PublicModules"/> instead of repeated here, so a new module cannot be public
/// in the nav and internal to the crawler at the same time.
/// <para>
/// Being listed is a statement about indexing only. It grants nothing: every page keeps its own authorization, and
/// <c>/buerger</c> is deliberately absent because a citizen's own account pages are private, not public.
/// </para>
/// </remarks>
public static class PublicRoutes
{
    /// <summary>Public routes that no module owns (legal pages, the second hazard list, the crawler file).</summary>
    /// <remarks>
    /// A route a module already names in <see cref="PublicModules.All"/> does not belong here — that is the repetition
    /// this class exists to avoid. <c>/info</c> was listed here before the editorial module carried it.
    /// </remarks>
    public static readonly IReadOnlyList<string> ExtraPrefixes =
    [
        // no "/karriere": the Careers module names it as its NavRoute, and Prefixes collects those without
        // consulting Available - so listing it here was the very repetition the remark above forbids
        "/datenschutz",
        "/nutzungsbedingungen",
        "/gefahr",
        "/robots.txt",
    ];

    /// <summary>Static assets a crawler needs to render the public pages.</summary>
    public static readonly IReadOnlyList<string> AssetPrefixes =
    [
        "/_framework",
        "/_content",
        "/css",
        "/lib",
        "/app.css",
        "/FibIcon.png",
        "/favicon.png",
        "/apple-touch-icon.png",
    ];

    /// <summary>Every public path prefix: module nav routes plus the extras. Assets are not part of this.</summary>
    public static readonly IReadOnlyList<string> Prefixes =
        PublicModules.All
            .Select(m => m.NavRoute)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Concat(ExtraPrefixes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    /// <summary>True for the landing page, a public route or one of its children, or a shared asset.</summary>
    public static bool IsPublic(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return true;
        }
        foreach (var prefix in Prefixes)
        {
            if (Matches(path, prefix))
            {
                return true;
            }
        }
        foreach (var prefix in AssetPrefixes)
        {
            if (Matches(path, prefix))
            {
                return true;
            }
        }
        return false;
    }

    // segment match, so /gesuchte-liste is not covered by /gesucht
    private static bool Matches(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
