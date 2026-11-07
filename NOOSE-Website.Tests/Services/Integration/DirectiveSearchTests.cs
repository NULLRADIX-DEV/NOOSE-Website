using System.Security.Claims;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Directives in the global search and the palette: number and title only, within the row's level.</summary>
public sealed class DirectiveSearchTests
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Trainee() => ClaimsPrincipalBuilder.Agent("trainee").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Tru() => ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).AsTru().Build();

    private static ClaimsPrincipal Chief() => ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.SectionChief).Build();

    private static ClaimsPrincipal Partner() => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director)
        .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    private static Directive Row(string id, int number, string title, string? subject = null,
        DocumentClassification level = DocumentClassification.None, Action<Directive>? configure = null)
    {
        var directive = new Directive
        {
            Id = id,
            Number = number,
            Title = title,
            Subject = subject,
            Classification = level,
            CurrentVersion = 1,
            AcknowledgeFromVersion = 1,
            CreatedAt = Stamp,
        };
        configure?.Invoke(directive);
        return directive;
    }

    private static async Task SeedAsync(SqliteTestContext ctx, params Directive[] rows)
    {
        await using var db = ctx.NewContext();
        db.Directives.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<SearchHit> Directives(SearchResults results)
        => results.Groups.FirstOrDefault(g => g.Category == nameof(Directive))?.Hit ?? [];

    [Fact]
    public async Task Search_finds_a_directive_by_its_number_and_its_title()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx,
            Row("d7", 7, "Waffenmitführung", "Dienstbetrieb"),
            Row("d8", 8, "Funkdisziplin"));
        var search = SearchTestHost.NewService(ctx);
        var viewer = Trainee();

        var byNumber = Assert.Single(Directives(await search.SearchAsync(new SearchCriteria { Text = "DA-07" }, viewer)));
        Assert.Equal("d7", byNumber.TargetId);
        Assert.Equal("DA-07", byNumber.CaseNumber);
        Assert.Equal("Waffenmitführung", byNumber.Title);
        Assert.Equal("Dienstbetrieb", byNumber.Snippet);

        // the spellings an agent types
        Assert.Equal("d7", Assert.Single(Directives(await search.SearchAsync(new SearchCriteria { Text = "da 7" }, viewer))).TargetId);
        Assert.Equal("d8", Assert.Single(Directives(await search.SearchAsync(new SearchCriteria { Text = "Funkdisz" }, viewer))).TargetId);
        Assert.Equal("d7", Assert.Single(Directives(await search.SearchAsync(new SearchCriteria { Text = "Waffen" }, viewer))).TargetId);
        // a bare number is no directive
        Assert.Empty(Directives(await search.SearchAsync(new SearchCriteria { Text = "07" }, viewer)));
    }

    [Fact]
    public async Task A_subject_alone_is_no_match()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Row("d1", 1, "Waffenmitführung", "Einsatz"));
        var search = SearchTestHost.NewService(ctx);

        Assert.Empty(Directives(await search.SearchAsync(new SearchCriteria { Text = "Einsatz" }, Trainee())));
        Assert.Empty(await search.QuickSearchAsync("Einsatz", Trainee()));
    }

    [Fact]
    public async Task A_repealed_directive_stays_findable_and_says_so()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx,
            Row("old", 1, "Altregelung", "Dienstbetrieb", configure: d => { d.RepealedAt = Stamp; d.RepealReason = "überholt"; }),
            Row("older", 2, "Altregelung ohne Sachgebiet", configure: d => { d.RepealedAt = Stamp; d.RepealReason = "überholt"; }),
            Row("live", 3, "Altregelung neu"));
        var search = SearchTestHost.NewService(ctx);

        var hits = Directives(await search.SearchAsync(new SearchCriteria { Text = "Altregelung" }, Trainee()));

        Assert.Equal("Dienstbetrieb · aufgehoben", hits.Single(h => h.TargetId == "old").Snippet);
        Assert.Equal("aufgehoben", hits.Single(h => h.TargetId == "older").Snippet);
        Assert.Equal(string.Empty, hits.Single(h => h.TargetId == "live").Snippet);
    }

    [Fact]
    public async Task A_trashed_directive_is_not_found()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Row("gone", 1, "Altregelung", configure: d => { d.IsDeleted = true; d.DeletedAt = Stamp; }));

        Assert.Empty(Directives(await SearchTestHost.NewService(ctx).SearchAsync(new SearchCriteria { Text = "Altregelung" }, Chief())));
    }

    [Fact]
    public async Task Search_hides_a_classified_directive_from_an_uncleared_agent()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx,
            Row("d-tru", 1, "Zugriffsplan", level: DocumentClassification.Tru),
            Row("d-lead", 2, "Zugriffsplan Führung", level: DocumentClassification.Leadership),
            Row("d-open", 3, "Zugriffsplan offen"));
        var search = SearchTestHost.NewService(ctx);

        async Task<string> Found(ClaimsPrincipal user)
            => string.Join(',', Directives(await search.SearchAsync(new SearchCriteria { Text = "Zugriffsplan" }, user))
                .Select(h => h.TargetId).Order(StringComparer.Ordinal));

        Assert.Equal("d-open", await Found(Trainee()));
        Assert.Equal("d-open,d-tru", await Found(Tru()));
        Assert.Equal("d-lead,d-open,d-tru", await Found(Chief()));

        // the palette and the picker behind it follow the same gate
        var palette = await search.QuickSearchAsync("Zugriffsplan", Trainee());
        Assert.Equal("d-open", Assert.Single(palette, h => h.Category == nameof(Directive)).TargetId);
    }

    [Fact]
    public async Task Quick_search_offers_directives_but_never_to_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Row("d1", 1, "Funkdisziplin"));
        var search = SearchTestHost.NewService(ctx);

        var hit = Assert.Single(await search.QuickSearchAsync("DA-01", Trainee()), h => h.Category == nameof(Directive));
        Assert.Equal("d1", hit.TargetId);
        Assert.Equal("Funkdisziplin", hit.Name);
        Assert.Equal("DA-01", hit.CaseNumber);

        Assert.DoesNotContain(await search.QuickSearchAsync("DA-01", Partner()), h => h.Category == nameof(Directive));
        var results = await search.SearchAsync(new SearchCriteria { Text = "Funkdisziplin" }, Partner());
        Assert.DoesNotContain(results.Groups, g => g.Category == nameof(Directive));
    }
}
