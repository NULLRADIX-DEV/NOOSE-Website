using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace NOOSE_Website.E2E;

/// <summary>The routes the browser tests visit, read from the [Route] attributes of the web assembly.</summary>
public static class RouteCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> _static = new(LoadStaticRoutes);

    /// <summary>Every route template without parameters, minus account, error and print pages.</summary>
    public static IReadOnlyList<string> StaticRoutes() => _static.Value;

    /// <summary>
    /// (list route, link prefix): the test opens the list and follows the first link (or row) that leads to
    /// prefix + one segment, which gives a record detail page without knowing any id.
    /// </summary>
    public static IReadOnlyList<(string ListRoute, string LinkPrefix)> DetailSeeds { get; } =
    [
        ("/personen", "/personen/"),
        ("/fraktionen", "/fraktionen/"),
        ("/personengruppen", "/personengruppen/"),
        ("/parteien", "/parteien/"),
        ("/vorgaenge", "/vorgaenge/"),
        ("/operationen", "/operationen/"),
        ("/taskforces", "/taskforces/"),
        ("/dokumente", "/dokumente/"),
        ("/aufgaben", "/aufgaben/"),
        ("/entfuehrungen", "/entfuehrungen/"),
        ("/anordnungen", "/anordnungen/"),
        ("/finanzierungen", "/finanzierungen/"),
        ("/informanten", "/informanten/"),
        ("/hinweise", "/hinweise/"),
        ("/kalender", "/kalender/"),
        ("/brett", "/brett/"),
        ("/praesentationen", "/praesentationen/"),
        ("/besprechungen", "/besprechungen/"),
        ("/aktivitaeten", "/aktivitaeten/"),
        ("/gesetze", "/gesetze/"),
        ("/gesetze", "/gesetze/buch/"),
        ("/lageberichte/archiv", "/lageberichte/"), // /lageberichte itself only redirects
        ("/personal", "/personal/"),
        ("/kasse", "/kasse/buchung/"),
        ("/asservatenkammer", "/asservatenkammer/eintrag/"),
        ("/asservatenkammer", "/asservatenkammer/item/"),
        ("/bewerbungen", "/bewerbungen/"),
        ("/einsatzalarme", "/einsatzalarme/"),
        ("/handbuch", "/handbuch/"),
        ("/gesucht", "/gesucht/"),
        ("/presse", "/presse/"),
    ];

    /// <summary>True for a path that is prefix + exactly one more segment and not itself a static route.</summary>
    public static bool IsDetailPath(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = path[prefix.Length..];
        return rest.Length > 0 && !rest.Contains('/') && !StaticRoutes().Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> LoadStaticRoutes()
    {
        var assembly = typeof(NOOSE_Website.Components.App).Assembly;
        return assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: false))
            .Select(a => a.Template)
            .Where(r => !r.Contains('{'))
            .Where(r => !IsExcluded(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsExcluded(string route)
    {
        if (route.StartsWith("/Account", StringComparison.OrdinalIgnoreCase))
            return true;
        if (route.Equals("/Error", StringComparison.OrdinalIgnoreCase) || route.Equals("/not-found", StringComparison.OrdinalIgnoreCase))
            return true;
        // print layouts keep their paper size on purpose
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(s => s.Equals("druck", StringComparison.OrdinalIgnoreCase) || s.Equals("print", StringComparison.OrdinalIgnoreCase));
    }
}
