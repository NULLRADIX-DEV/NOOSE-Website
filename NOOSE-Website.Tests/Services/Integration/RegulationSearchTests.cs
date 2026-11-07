using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Search;
using static NOOSE_Website.Tests.Services.Integration.RegulationFixture;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Paragraphs in the global search and the palette: heading, section title and current wording, internal agents only.</summary>
public sealed class RegulationSearchTests
{
    private static IReadOnlyList<SearchHit> Paragraphs(SearchResults results)
        => results.Groups.FirstOrDefault(g => g.Category == nameof(RegulationParagraph))?.Hit ?? [];

    /// <summary>Section 1 "Funkverkehr" with "p-call" (v1 old wording, v2 current), section 2 "Waffen" with "p-arms" and repealed "p-old".</summary>
    private static async Task SeedAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.RegulationSections.AddRange(
            SectionRow("s-radio", 10, "Funkverkehr"),
            SectionRow("s-arms", 20, "Bewaffnung"),
            SectionRow("s-gone", 30, "Papierkorb", configure: s => { s.IsDeleted = true; s.DeletedAt = Stamp; }));
        db.RegulationParagraphs.AddRange(
            ParagraphRow("p-call", "s-radio", "Rufnamen", configure: p => p.CurrentVersion = 2),
            ParagraphRow("p-arms", "s-arms", "Waffenmitführung", RegulationAudience.Hrt),
            ParagraphRow("p-old", "s-arms", "Altregelung", configure: p => { p.RepealedAt = Stamp; p.RepealReason = "überholt"; }),
            ParagraphRow("p-trash", "s-arms", "Altregelung weg", configure: p => { p.IsDeleted = true; p.DeletedAt = Stamp; }),
            ParagraphRow("p-orphan", "s-gone", "Altregelung verwaist"));
        db.RegulationVersions.AddRange(
            VersionRow("p-call", 1, html: "<p>Zebra ruft</p>"),
            VersionRow("p-call", 2, html: "<p>Giraffe ruft</p>"),
            VersionRow("p-arms", 1, html: "<p>Nur dienstlich</p>"),
            VersionRow("p-old", 1, html: "<p>Alt</p>"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_finds_a_paragraph_by_heading_section_title_and_current_wording()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var search = SearchTestHost.NewService(ctx);
        var viewer = Plain("trainee");

        async Task<string> Found(string text)
            => string.Join(',', Paragraphs(await search.SearchAsync(new SearchCriteria { Text = text }, viewer))
                .Select(h => h.TargetId).Order(StringComparer.Ordinal));

        var byHeading = Assert.Single(Paragraphs(await search.SearchAsync(new SearchCriteria { Text = "Waffenmit" }, viewer)));
        Assert.Equal("p-arms", byHeading.TargetId);
        Assert.Equal("Waffenmitführung", byHeading.Title);
        Assert.Equal("DVO Abschnitt 2 · Waffenmitführung", byHeading.CaseNumber);
        Assert.Equal("Dienstverordnung", byHeading.Snippet);

        Assert.Equal("p-call", await Found("Funkverk"));
        Assert.Equal("p-call", await Found("Giraffe"));
        // the old wording is history; the wording itself never reaches a snippet
        Assert.Equal(string.Empty, await Found("Zebra"));
        Assert.DoesNotContain(Paragraphs(await search.SearchAsync(new SearchCriteria { Text = "Giraffe" }, viewer)),
            h => h.Snippet.Contains("Giraffe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repealed_paragraph_stays_findable_and_says_so_a_trashed_one_does_not()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var search = SearchTestHost.NewService(ctx);

        var hits = Paragraphs(await search.SearchAsync(new SearchCriteria { Text = "Altregelung" },
            ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsAdmin().Build()));

        var hit = Assert.Single(hits);
        Assert.Equal("p-old", hit.TargetId);
        Assert.Equal("Dienstverordnung · aufgehoben", hit.Snippet);
    }

    [Fact]
    public async Task Quick_search_offers_paragraphs_but_never_to_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var search = SearchTestHost.NewService(ctx);

        var hit = Assert.Single(await search.QuickSearchAsync("Rufnamen", Plain("trainee")), h => h.Category == nameof(RegulationParagraph));
        Assert.Equal("p-call", hit.TargetId);
        Assert.Equal("Rufnamen", hit.Name);
        Assert.Equal("DVO Abschnitt 1 · Rufnamen", hit.CaseNumber);
        // supervision reads the regulation too
        Assert.Contains(await search.QuickSearchAsync("Rufnamen", Supervision()), h => h.Category == nameof(RegulationParagraph));

        Assert.DoesNotContain(await search.QuickSearchAsync("Rufnamen", Partner()), h => h.Category == nameof(RegulationParagraph));
        var results = await search.SearchAsync(new SearchCriteria { Text = "Rufnamen" }, Partner());
        Assert.DoesNotContain(results.Groups, g => g.Category == nameof(RegulationParagraph));
    }
}
