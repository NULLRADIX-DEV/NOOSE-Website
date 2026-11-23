using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>MergedPageSections matches the rail pages.</summary>
public sealed class MergedPageSectionsTests
{
    /// <summary>Rail pages with data-built sections, each with the reason.</summary>
    private static readonly Dictionary<string, DataSections> DataDriven = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/papierkorb"] = new("one section per ITrashService kind, TrashServiceTests guards the list", ListsDataSlugs: true),
        ["/handbuch"] = new("one section per chapter, read from the database", ListsDataSlugs: false),
        ["/dienstverordnung"] = new("one section per DVO section, read from the database", ListsDataSlugs: false),
    };

    private static readonly Regex PageRoute = new(@"^@page\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Rail = new(@"<RecordSectionRail\b", RegexOptions.Compiled);

    private static readonly Regex SectionSlug = new(
        @"<RecordSection\s(?:[^>""]|""[^""]*"")*?(?<=\s)Slug=""([^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    [Fact]
    public void Every_fixed_rail_page_is_listed_with_its_literal_sections()
    {
        var root = SourceRoot();
        Assert.True(Directory.Exists(root), $"Quellordner nicht gefunden: {root}");

        var pages = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => RailPages(Path.GetRelativePath(root, f), File.ReadAllText(f)))
            .ToList();

        Assert.NotEmpty(pages);
        Assert.Empty(Problems(pages, MergedPageSections.ByRoute, DataDriven));
    }

    [Fact]
    public void Literal_and_data_slugs_are_told_apart_and_parameter_routes_skipped()
    {
        const string razor = """
            @page "/demo"
            @page "/demo/{Id}"
            <RecordSectionRail>
                <RecordSection Group="G" Slug="eins" Label="Eins" Icon="@(() => 1)">x</RecordSection>
                <RecordSection
                    Label="Zwei" Slug="zwei">x</RecordSection>
                <RecordSection Label="Slug=nein" Slug="@chapter.Slug">x</RecordSection>
            </RecordSectionRail>
            """;

        var page = Assert.Single(RailPages("Demo.razor", razor));

        Assert.Equal("/demo", page.Route);
        Assert.Equal(new[] { "eins", "zwei" }, page.Literal);
        Assert.True(page.HasDataSlugs);
        Assert.Empty(RailPages("Detail.razor", "@page \"/demo/{Id}\"\n<RecordSectionRail></RecordSectionRail>"));
        Assert.Empty(RailPages("Other.razor", "@page \"/demo\"\n<RecordSection Slug=\"eins\" Label=\"Eins\" />"));
    }

    [Fact]
    public void A_matching_page_has_no_problem()
        => Assert.Empty(Problems([Page("/demo", "eins", "zwei")], Listed("/demo", "zwei", "eins"), NoDataDriven));

    [Theory]
    [InlineData("eins,drei")]
    [InlineData("eins,zwei,drei")]
    [InlineData("eins")]
    public void A_renamed_added_or_removed_slug_is_reported(string onPage)
        => Assert.Single(Problems([Page("/demo", onPage.Split(','))], Listed("/demo", "eins", "zwei"), NoDataDriven));

    [Fact]
    public void A_new_rail_page_in_no_list_is_reported()
        => Assert.Single(Problems([Page("/neu", "eins")], Listed("/demo", "eins"), NoDataDriven), p => p.Contains("/neu"));

    [Fact]
    public void A_listed_route_without_a_rail_page_is_reported()
        => Assert.Single(Problems([], Listed("/weg", "eins"), NoDataDriven), p => p.Contains("/weg"));

    [Fact]
    public void Data_sections_need_a_named_exception_and_the_exception_must_still_apply()
    {
        var dynamicPage = Page("/demo", ["eins"], hasDataSlugs: true);
        var staticPage = Page("/demo", "eins");
        var listed = Listed("/demo", "eins");
        var exception = new Dictionary<string, DataSections> { ["/demo"] = new("per row", ListsDataSlugs: false) };

        Assert.Empty(Problems([dynamicPage], listed, exception));
        Assert.Single(Problems([dynamicPage], listed, NoDataDriven));
        Assert.Single(Problems([staticPage], listed, exception));
        Assert.Single(Problems([], new Dictionary<string, string[]>(), exception), p => p.Contains("DataDriven names /demo"));
    }

    [Fact]
    public void A_list_of_data_slugs_may_exceed_the_literal_sections_but_not_miss_one()
    {
        var exception = new Dictionary<string, DataSections> { ["/demo"] = new("per row", ListsDataSlugs: true) };

        Assert.Empty(Problems([Page("/demo", ["fest"], hasDataSlugs: true)], Listed("/demo", "fest", "zeile"), exception));
        Assert.Single(Problems([Page("/demo", ["fest"], hasDataSlugs: true)], Listed("/demo", "zeile"), exception));
    }

    private sealed record DataSections(string Reason, bool ListsDataSlugs);

    private sealed record RailPage(string File, string Route, string[] Literal, bool HasDataSlugs);

    private static readonly Dictionary<string, DataSections> NoDataDriven = [];

    private static RailPage Page(string route, params string[] literal) => new("Demo.razor", route, literal, false);

    private static RailPage Page(string route, string[] literal, bool hasDataSlugs) => new("Demo.razor", route, literal, hasDataSlugs);

    private static Dictionary<string, string[]> Listed(string route, params string[] slugs) => new() { [route] = slugs };

    private static IEnumerable<RailPage> RailPages(string file, string text)
    {
        if (!Rail.IsMatch(text))
        {
            yield break;
        }
        var slugs = SectionSlug.Matches(text).Select(m => m.Groups[1].Value).ToList();
        var literal = slugs.Where(s => !s.StartsWith('@')).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var route in PageRoute.Matches(text).Select(m => m.Groups[1].Value).Where(r => !r.Contains('{')))
        {
            yield return new RailPage(file, route, literal, slugs.Any(s => s.StartsWith('@')));
        }
    }

    private static List<string> Problems(
        IReadOnlyList<RailPage> pages, IReadOnlyDictionary<string, string[]> listed, IReadOnlyDictionary<string, DataSections> dataDriven)
    {
        var problems = new List<string>();
        foreach (var page in pages)
        {
            if (!listed.TryGetValue(page.Route, out var slugs))
            {
                problems.Add($"{page.File}: {page.Route} has a section rail but is not in MergedPageSections.ByRoute");
                continue;
            }

            dataDriven.TryGetValue(page.Route, out var data);
            if (page.HasDataSlugs && data is null)
            {
                problems.Add($"{page.File}: sections come from data, name {page.Route} in DataDriven with the reason");
            }
            if (!page.HasDataSlugs && data is not null)
            {
                problems.Add($"{page.File}: no section comes from data any more, drop {page.Route} from DataDriven");
            }

            var missing = page.Literal.Except(slugs, StringComparer.Ordinal).ToList();
            var extra = data is { ListsDataSlugs: true } ? new List<string>() : slugs.Except(page.Literal, StringComparer.Ordinal).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                problems.Add($"{page.File}: {page.Route} sections differ from the list, not listed: [{string.Join(", ", missing)}], not on the page: [{string.Join(", ", extra)}]");
            }
        }

        problems.AddRange(dataDriven.Keys
            .Where(route => !pages.Any(p => string.Equals(p.Route, route, StringComparison.OrdinalIgnoreCase)))
            .Select(route => $"DataDriven names {route}, but no page with that route has a section rail"));
        problems.AddRange(listed.Keys
            .Where(route => !pages.Any(p => string.Equals(p.Route, route, StringComparison.OrdinalIgnoreCase)))
            .Select(route => $"MergedPageSections.ByRoute lists {route}, but no page with that route has a section rail"));
        return problems;
    }
}
