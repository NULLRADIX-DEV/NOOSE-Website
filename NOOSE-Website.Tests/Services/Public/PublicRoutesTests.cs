using System.Runtime.CompilerServices;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>Which routes count as public — the basis of both the noindex header and robots.txt.</summary>
public class PublicRoutesTests
{
    private static string RobotsPath([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website", "wwwroot", "robots.txt"));

    [Theory]
    [InlineData("/")]
    [InlineData("/karriere")]
    [InlineData("/gesucht")]
    [InlineData("/gesucht/FIB-FA-2026-0001")]
    [InlineData("/gesucht/FIB-FA-2026-0001/foto")]
    [InlineData("/gesucht/FIB-FA-2026-0001/druck")]
    [InlineData("/gefasst")]
    [InlineData("/GESUCHT")]
    [InlineData("/datenschutz")]
    [InlineData("/nutzungsbedingungen")]
    [InlineData("/info/auftrag")]
    [InlineData("/faq")]
    [InlineData("/gefahr/personen")]
    [InlineData("/robots.txt")]
    public void Public_paths_are_indexable(string path)
        => Assert.True(PublicRoutes.IsPublic(path), path);

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/personen")]
    [InlineData("/personen/abc")]
    [InlineData("/fraktionen")]
    [InlineData("/einstellungen")]
    [InlineData("/nachweis")]
    [InlineData("/fahndung")]
    [InlineData("/suche")]
    [InlineData("/Account/Login")]
    [InlineData("/portal")]
    public void Internal_paths_are_not_indexable(string path)
        => Assert.False(PublicRoutes.IsPublic(path), path);

    [Fact]
    public void The_citizens_own_account_area_is_not_indexable()
    {
        // /buerger is private, not public: it shows one citizen their own submissions
        Assert.False(PublicRoutes.IsPublic("/buerger"));
        Assert.False(PublicRoutes.IsPublic("/buerger/profil"));
    }

    [Fact]
    public void Matching_stops_at_a_segment_boundary()
    {
        // /gesucht must not make /gesuchte-liste public
        Assert.False(PublicRoutes.IsPublic("/gesuchte-liste"));
        Assert.False(PublicRoutes.IsPublic("/karriere-intern"));
    }

    [Fact]
    public void Internal_fahndung_stays_internal_next_to_public_gesucht()
    {
        // the two boards share a topic and nothing else; /fahndung is the internal one
        Assert.False(PublicRoutes.IsPublic("/fahndung"));
        Assert.True(PublicRoutes.IsPublic("/gesucht"));
    }

    [Fact]
    public void The_internal_tip_desk_stays_internal_next_to_the_public_form()
    {
        // same trap as /fahndung next to /gesucht, one letter apart: /hinweis is the form, /hinweise is the desk
        Assert.True(PublicRoutes.IsPublic("/hinweis"));
        Assert.False(PublicRoutes.IsPublic("/hinweise"));
        Assert.False(PublicRoutes.IsPublic("/hinweise/abc"));
        Assert.False(PublicRoutes.IsPublic("/buerger/hinweise"));
    }

    [Fact]
    public void Robots_txt_exists_and_disallows_everything_by_default()
    {
        var text = File.ReadAllText(RobotsPath());

        Assert.Contains("User-agent: *", text, StringComparison.Ordinal);
        Assert.Contains("Disallow: /", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_public_prefix_is_covered_by_an_allow_line()
    {
        // robots.txt matches by prefix, so "Allow: /gefahr" covers /gefahr/fraktionen — an exact-name check here
        // would demand a line per sub-route and drift the moment a module adds one
        var lines = AllowLines();

        var missing = PublicRoutes.Prefixes
            .Where(p => p != "/robots.txt")
            .Where(p => !lines.Any(l => p.Equals(l, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(l + "/", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(missing.Length == 0,
            "Öffentlich, aber von keiner Allow-Zeile gedeckt: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_allow_line_really_points_at_a_public_path()
    {
        var wrong = AllowLines()
            .Where(l => l != "/$")
            .Where(l => !PublicRoutes.IsPublic(l))
            .ToArray();

        Assert.True(wrong.Length == 0,
            "In robots.txt erlaubt, gilt aber als intern: " + string.Join(", ", wrong));
    }

    [Fact]
    public void EveryInternalRouteBehindAPublicPrefix_IsDisallowedExplicitly()
    {
        // A robots.txt rule matches by string prefix; PublicRoutes.Matches stops at the segment boundary. That gap is
        // real: "Allow: /lage" also permits /lageberichte, "Allow: /hinweis" the internal tip desk. The noindex header
        // is right either way, but the two layers must not contradict each other — phase 14a renamed a public route
        // for exactly this reason and missed the collision coming from the other side.
        var allow = AllowLines().Where(l => l != "/$").ToArray();
        var disallow = DisallowLines().Where(l => l != "/").ToArray();

        var offenders = InternalRoutes()
            .Select(route => (Route: route, Allow: Longest(allow, route)))
            .Where(x => x.Allow is not null)
            // the longest matching rule wins, and Allow wins a tie, so the Disallow has to be strictly longer
            .Where(x => (Longest(disallow, x.Route)?.Length ?? 0) <= x.Allow!.Length)
            .Select(x => $"{x.Route} (durch Allow: {x.Allow})")
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Intern, aber von einer Allow-Zeile mitgedeckt: " + string.Join(", ", offenders));
    }

    private static string? Longest(IEnumerable<string> rules, string path)
        => rules.Where(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();

    /// <summary>Every @page route of the app that PublicRoutes does not consider public.</summary>
    private static string[] InternalRoutes([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website", "Components"));
        Assert.True(Directory.Exists(root), $"Komponentenordner nicht gefunden: {root}");

        var routes = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(f), "@page\\s+\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value))
            // the parameter part says nothing about the prefix; /hinweise/{Id} collides exactly like /hinweise
            .Select(r => r.Split('{')[0].TrimEnd('/'))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !PublicRoutes.IsPublic(r))
            .Order()
            .ToArray();

        // a wrong path would otherwise leave the fact above green forever
        Assert.NotEmpty(routes);
        return routes;
    }

    private static IReadOnlyList<string> DisallowLines()
        => File.ReadAllLines(RobotsPath())
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["Disallow:".Length..].Trim())
            .ToList();

    private static IReadOnlyList<string> AllowLines()
        => File.ReadAllLines(RobotsPath())
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Allow:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["Allow:".Length..].Trim())
            .ToList();
}
