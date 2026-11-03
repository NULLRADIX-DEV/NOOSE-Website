using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Infrastructure.Search;
using NOOSE_Website.Services.Search;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>Drift guards for the phonetic side index and the command palette.</summary>
/// <remarks>
/// Both cover the same shape of half-finished change: a capability declared on a catalog row that nothing behind it
/// actually fills. The failure is silent — the category simply never produces a hit — so it has to be a build error.
/// </remarks>
public class SearchIndexCoverageTests
{
    private static string Source(params string[] relative)
    {
        var root = Path.GetDirectoryName(Here())!;
        // .../NOOSE-Website.Tests/Services -> repo root -> NOOSE-Website
        var project = Path.GetFullPath(Path.Combine(root, "..", "..", "NOOSE-Website"));
        return File.ReadAllText(Path.Combine(new[] { project }.Concat(relative).ToArray()));
    }

    private static string Here([CallerFilePath] string path = "") => path;

    /// <summary>The record types <see cref="NOOSE_Website.Services.SearchIndexProjection.For"/> can emit a row for.
    /// Read from the source because a switch expression cannot be reflected over.</summary>
    private static HashSet<string> ProjectedTypes()
        => Regex.Matches(Source("Services", "SearchIndexProjection.cs"), @"Build\(nameof\((\w+)\)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    // ---- side index ----

    [Fact]
    public void Every_side_indexed_category_has_a_branch_in_the_projection()
    {
        // a category marked phonetic with no projection arm never writes an index row: the catalog claims a
        // capability the interceptor does not have
        var projected = ProjectedTypes();
        Assert.NotEmpty(projected);

        var claimed = SearchCatalog.Clrs(SearchTraits.SideIndexed).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(claimed, projected);
    }

    [Fact]
    public void Every_projected_type_is_a_side_indexed_category()
    {
        // the other direction: index rows nothing ever reads back, written on every SaveChanges of that type
        var claimed = SearchCatalog.Clrs(SearchTraits.SideIndexed).ToHashSet(StringComparer.Ordinal);

        var stray = ProjectedTypes().Where(t => !claimed.Contains(t)).ToArray();
        Assert.Empty(stray);
    }

    [Fact]
    public void The_backfill_covers_every_side_indexed_category()
    {
        // the interceptor keeps ongoing changes indexed; without a backfill line the existing corpus stays empty,
        // which reads to the user as "phonetic search is flaky" rather than "was never built"
        var worker = Source("Infrastructure", "Search", "SearchIndexBackfillWorker.cs");
        var indexed = Regex.Matches(worker, @"IndexAllAsync\(db, db\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(indexed);

        // the DbSet names are plural/renamed, so compare on the entity behind each set
        var sets = typeof(NOOSE_Website.Data.AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .ToDictionary(p => p.Name, p => p.PropertyType.GetGenericArguments()[0].Name, StringComparer.Ordinal);
        // Users is IdentityDbContext's own property, not a DbSet<> declared here
        sets["Users"] = "Agent";

        var covered = indexed
            .Where(sets.ContainsKey)
            .Select(name => sets[name])
            .ToHashSet(StringComparer.Ordinal);

        var missing = ProjectedTypes()
            // an alias pays into its person's index and has no set of its own to walk
            .Where(t => !covered.Contains(t))
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// A tripwire, not a computation: there is no way to assert "someone remembered". The count below is the
    /// number of side-indexed categories at the last bump, so adding one makes this fail and the next reader
    /// has to come here - and the message tells them what to do. The old form compared the version against a
    /// hardcoded 2 and could therefore never fail again.
    /// </summary>
    [Fact]
    public void The_backfill_version_is_bumped_whenever_the_projection_changes()
    {
        const int sideIndexedAtLastBump = 13;
        const int versionAtLastBump = 5;

        var sideIndexed = SearchCatalog.Categories.Count(c => c.Has(SearchTraits.SideIndexed));

        Assert.True(
            sideIndexed == sideIndexedAtLastBump && SearchIndexBackfillWorker.Version >= versionAtLastBump,
            $"{sideIndexed} seitenindizierte Kategorien, zuletzt waren es {sideIndexedAtLastBump}. "
            + "Eine neue braucht eine Zeile in SearchIndexProjection, eine IndexAllAsync-Zeile im Backfill, "
            + "ein hochgezähltes SearchIndexBackfillWorker.Version und die beiden Zahlen hier - sonst "
            + "indiziert eine Bestandsinstallation den neuen Typ nie nach.");
    }

    // ---- palette and side-index provider overrides ----

    private static bool Declares(object provider, string method)
        => provider.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == method);

    [Fact]
    public void Every_palette_category_has_a_provider_that_implements_QuickAsync()
    {
        // the default interface method returns an empty list, so a Quick category without an override is simply
        // never in the palette and nobody notices
        using var ctx = new SqliteTestContext();
        var providers = SearchTestHost.Providers(ctx).ToDictionary(p => p.Category, StringComparer.Ordinal);

        foreach (var category in SearchCatalog.Clrs(SearchTraits.Quick))
        {
            Assert.True(providers.ContainsKey(category), $"{category} ist Quick, hat aber keinen Provider.");
            Assert.True(Declares(providers[category], nameof(ISearchProvider.QuickAsync)),
                $"{category} ist Quick, überschreibt QuickAsync aber nicht.");
        }
    }

    [Fact]
    public void Every_side_indexed_category_has_a_provider_that_implements_ResolveIdsAsync()
    {
        // same trap: the index finds the candidate id and the default override throws it away again
        using var ctx = new SqliteTestContext();
        var providers = SearchTestHost.Providers(ctx).ToDictionary(p => p.Category, StringComparer.Ordinal);

        foreach (var category in SearchCatalog.Clrs(SearchTraits.SideIndexed))
        {
            Assert.True(providers.ContainsKey(category), $"{category} ist SideIndexed, hat aber keinen Provider.");
            Assert.True(Declares(providers[category], nameof(ISearchProvider.ResolveIdsAsync)),
                $"{category} ist SideIndexed, überschreibt ResolveIdsAsync aber nicht.");
        }
    }
}
