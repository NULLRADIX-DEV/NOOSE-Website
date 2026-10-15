using System.Text.Json;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Services.Search;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A presentation as a link target and as a search hit: both follow <see cref="PresentationVisibility"/>.</summary>
/// <remarks>The viewers come from <see cref="PresentationServiceTests"/>; the expected decks repeat its matrix.</remarks>
public sealed class PresentationLinkTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Decks = ["pub", "tru", "hrb", "rank", "tf", "priv"];

    private static LinkService Links(SqliteTestContext ctx) => new(ctx.Factory, Substitute.For<IThreatScoreService>());

    /// <summary>The access matrix of the presentation tests, titled "Lagebild …" and linked from person p1.</summary>
    private static void SeedDecksLinkedFromPerson(SqliteTestContext ctx)
    {
        PresentationServiceTests.SeedAgents(ctx);
        using var db = ctx.NewContext();
        Presentation Deck(string id, PresentationAccess access, AnnouncementAudience? audience = null,
            string? targetId = null, Rank? minRank = null)
            => new()
            {
                Id = id, Title = $"Lagebild {id}", Access = access, Audience = audience, TargetId = targetId,
                MinRank = minRank, OwnerId = "own", CreatedAt = Ts,
            };
        db.Presentations.AddRange(
            Deck("pub", PresentationAccess.Public),
            Deck("tru", PresentationAccess.Closed, AnnouncementAudience.TruUnit),
            Deck("hrb", PresentationAccess.Closed, AnnouncementAudience.HrbUnit),
            Deck("rank", PresentationAccess.Closed, AnnouncementAudience.FromRank, minRank: Rank.SpecialAgent),
            Deck("tf", PresentationAccess.Closed, AnnouncementAudience.Taskforce, targetId: "tf1"),
            Deck("priv", PresentationAccess.Private));
        db.PresentationInvites.Add(new PresentationInvite { PresentationId = "priv", AgentId = "inv", CreatedAt = Ts });
        db.People.Add(Seed.Person("p1", "Max"));
        foreach (var id in Decks)
        {
            db.Links.Add(new Link { SourceType = nameof(Person), SourceId = "p1", TargetType = nameof(Presentation), TargetId = id });
        }
        db.SaveChanges();
    }

    private static string Sorted(IEnumerable<string> ids) => string.Join(',', ids.Order(StringComparer.Ordinal));

    [Theory]
    [InlineData("own", "hrb,priv,pub,rank,tf,tru")]
    [InlineData("tru", "pub,rank,tru")]
    [InlineData("hrb", "hrb,pub,rank")]
    [InlineData("jr", "pub")]
    [InlineData("tfm", "pub,rank,tf")]
    [InlineData("inv", "priv,pub")]
    [InlineData("lead", "hrb,priv,pub,rank,tf,tru")]
    [InlineData("sup", "hrb,priv,pub,rank,tf,tru")]
    public async Task A_person_lists_only_the_linked_presentations_the_viewer_may_see(string viewer, string expected)
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);

        var links = await Links(ctx).GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(PresentationServiceTests.Viewers[viewer]()));

        Assert.Equal(expected, Sorted(links.Where(l => l.OtherType == nameof(Presentation)).Select(l => l.OtherId)));
        Assert.All(links, l =>
        {
            Assert.Equal($"Lagebild {l.OtherId}", l.OtherDesignation);
            Assert.Equal($"/praesentationen/{l.OtherId}", l.Href);
        });
    }

    [Fact]
    public async Task A_trashed_presentation_drops_out_of_the_link_list()
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        using (var db = ctx.NewContext())
        {
            db.Presentations.Remove(db.Presentations.Single(p => p.Id == "pub"));
            db.SaveChanges();
        }

        var links = await Links(ctx).GetForRecordAsync(nameof(Person), "p1", ViewerScope.From(PresentationServiceTests.Viewers["lead"]()));

        Assert.DoesNotContain(links, l => l.OtherId == "pub");
        Assert.Contains(links, l => l.OtherId == "priv");
    }

    [Fact]
    public async Task The_presentation_lists_its_links_only_for_who_may_see_it()
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        var svc = Links(ctx);

        var invited = await svc.GetForRecordAsync(nameof(Presentation), "priv", ViewerScope.From(PresentationServiceTests.Viewers["inv"]()));
        var outsider = await svc.GetForRecordAsync(nameof(Presentation), "priv", ViewerScope.From(PresentationServiceTests.Viewers["jr"]()));

        var link = Assert.Single(invited);
        Assert.Equal((nameof(Person), "p1"), (link.OtherType, link.OtherId));
        Assert.Empty(outsider);
    }

    [Fact]
    public async Task Linking_a_presentation_the_actor_may_not_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.SaveChanges();
        }
        var svc = Links(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(
            nameof(Person), "p2", nameof(Presentation), "priv", null, PresentationServiceTests.Viewers["jr"]()));
        await svc.CreateAsync(nameof(Person), "p2", nameof(Presentation), "priv", null, PresentationServiceTests.Viewers["inv"]());

        var links = await svc.GetForRecordAsync(nameof(Person), "p2", ViewerScope.From(PresentationServiceTests.Viewers["inv"]()));
        Assert.Equal("priv", Assert.Single(links).OtherId);
    }

    [Fact]
    public async Task A_selection_from_the_search_can_be_linked_to_a_presentation()
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.SaveChanges();
        }
        var svc = Links(ctx);
        IReadOnlyCollection<(string, string)> picked = [(nameof(Person), "p2")];

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateManyAsync(
            nameof(Presentation), "priv", picked, null, PresentationServiceTests.Viewers["jr"]()));
        var outcome = await svc.CreateManyAsync(nameof(Presentation), "priv", picked, null, PresentationServiceTests.Viewers["inv"]());

        Assert.Equal(1, outcome.Done);
    }

    [Theory]
    [InlineData("inv", "priv,pub")]
    [InlineData("jr", "pub")]
    [InlineData("lead", "hrb,priv,pub,rank,tf,tru")]
    public async Task The_timeline_names_a_linked_presentation_only_for_who_may_see_it(string viewer, string expected)
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        using (var db = ctx.NewContext())
        {
            foreach (var link in db.Links)
            {
                link.Label = $"Notiz {link.TargetId}";
            }
            db.SaveChanges();
        }

        var entries = (await new TimelineService(ctx.Factory).GetTimelineAsync(nameof(Person), "p1",
                PresentationServiceTests.Viewers[viewer]()))
            .Where(e => e.Category == TimelineCategory.Link).ToList();

        // a hidden one is left out entirely, label included, like on the link list
        Assert.All(entries, e =>
        {
            Assert.StartsWith("Verknüpft mit Lagebild ", e.Title, StringComparison.Ordinal);
            var id = e.Title["Verknüpft mit Lagebild ".Length..];
            Assert.Equal($"/praesentationen/{id}", e.Href);
            Assert.Equal($"Notiz {id}", e.Detail);
        });
        Assert.Equal(expected, Sorted(entries.Select(e => e.Title["Verknüpft mit Lagebild ".Length..])));
    }

    [Fact]
    public async Task A_link_label_is_found_only_when_the_viewer_may_see_the_presentation_end()
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        using (var db = ctx.NewContext())
        {
            db.Links.Single(l => l.TargetId == "priv").Label = "Einsatzunterlage";
            db.SaveChanges();
        }
        var search = SearchTestHost.NewService(ctx);
        IReadOnlyList<SearchHit> LinkHits(SearchResults r) => r.Groups.FirstOrDefault(g => g.Category == nameof(Link))?.Hit ?? [];

        var invited = LinkHits(await search.SearchAsync(new SearchCriteria { Text = "Einsatzunterlage" }, PresentationServiceTests.Viewers["inv"]()));
        var outsider = LinkHits(await search.SearchAsync(new SearchCriteria { Text = "Einsatzunterlage" }, PresentationServiceTests.Viewers["jr"]()));

        Assert.Contains("Lagebild priv", Assert.Single(invited).Snippet, StringComparison.Ordinal);
        Assert.Empty(outsider);
    }

    // ---------- chronicle and NOOSEI ----------

    /// <summary>Seeds the decks and labels the link to the private one, which only "inv" may see.</summary>
    private static void SeedWithLabelledPrivateLink(SqliteTestContext ctx)
    {
        SeedDecksLinkedFromPerson(ctx);
        using var db = ctx.NewContext();
        var link = db.Links.Single(l => l.TargetId == "priv");
        link.Label = "Einsatzunterlage";
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = nameof(Link), EntityId = link.Id, Action = AuditAction.Created, Timestamp = Ts,
            AgentId = "own", AgentName = "own",
        });
        db.SaveChanges();
    }

    [Theory]
    [InlineData("inv", true)]
    [InlineData("jr", false)]
    public async Task The_chronicle_names_a_linked_presentation_or_drops_the_line(string viewer, bool sees)
    {
        using var ctx = new SqliteTestContext();
        SeedWithLabelledPrivateLink(ctx);

        var result = await new GlobalChronikService(ctx.Factory).GetEventsAsync(
            new ChronikQuery(Ts.AddDays(-1), Ts.AddDays(1), null, null), PresentationServiceTests.Viewers[viewer]());

        var details = result.Events.Select(e => e.Detail ?? string.Empty).ToList();
        if (sees)
        {
            Assert.Contains("mit Lagebild priv · Einsatzunterlage", details);
        }
        else
        {
            Assert.DoesNotContain(details, d => d.Contains("Einsatzunterlage", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("inv", true)]
    [InlineData("jr", false)]
    public async Task Noosei_lists_a_linked_presentation_only_for_who_may_see_it(string viewer, bool sees)
    {
        using var ctx = new SqliteTestContext();
        SeedWithLabelledPrivateLink(ctx);
        var user = PresentationServiceTests.Viewers[viewer]();

        var related = (await new ListRelatedTool(ctx.Factory).InvokeAsync(
            JsonDocument.Parse("""{"typ":"Person","id":"p1"}""").RootElement.Clone(), NooseiToolContext.From(user))).Text;
        string dossier;
        await using (var db = ctx.NewContext())
        {
            dossier = (await DossierContextBuilder.BuildAsync(db, nameof(Person), "p1", ViewerScope.From(user)))!.Value.Text;
        }

        foreach (var text in new[] { related, dossier })
        {
            Assert.Contains("Lagebild pub", text, StringComparison.Ordinal);
            Assert.Equal(sees, text.Contains("Lagebild priv", StringComparison.Ordinal));
            // a hidden one leaves no trace, the label included
            Assert.Equal(sees, text.Contains("Einsatzunterlage", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_cached_brief_names_no_presentation()
    {
        using var ctx = new SqliteTestContext();
        SeedWithLabelledPrivateLink(ctx);
        await using var db = ctx.NewContext();

        // the cache is built on least privilege, which no presentation reaches
        var brief = (await DossierContextBuilder.BuildAsync(db, nameof(Person), "p1", null))!.Value.Text;

        Assert.DoesNotContain("Lagebild", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Einsatzunterlage", brief, StringComparison.Ordinal);
    }

    // ---------- search ----------

    private static IReadOnlyList<SearchHit> Presentations(SearchResults results)
        => results.Groups.FirstOrDefault(g => g.Category == nameof(Presentation))?.Hit ?? [];

    [Theory]
    [InlineData("own", "hrb,priv,pub,rank,tf,tru")]
    [InlineData("tru", "pub,rank,tru")]
    [InlineData("jr", "pub")]
    [InlineData("tfm", "pub,rank,tf")]
    [InlineData("inv", "priv,pub")]
    [InlineData("lead", "hrb,priv,pub,rank,tf,tru")]
    public async Task The_search_finds_only_the_presentations_the_viewer_may_see(string viewer, string expected)
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);

        var results = await SearchTestHost.NewService(ctx).SearchAsync(new SearchCriteria { Text = "Lagebild" },
            PresentationServiceTests.Viewers[viewer]());

        Assert.Equal(expected, Sorted(Presentations(results).Select(h => h.TargetId)));
    }

    [Fact]
    public async Task The_search_finds_a_presentation_by_description_and_slide_title_but_not_by_notes()
    {
        using var ctx = new SqliteTestContext();
        PresentationServiceTests.SeedAgents(ctx);
        using (var db = ctx.NewContext())
        {
            db.Presentations.Add(new Presentation
            {
                Id = "d1", Title = "Wochenbriefing", Description = "Hafenrazzia im Überblick", Access = PresentationAccess.Public,
                OwnerId = "own", CreatedAt = Ts,
                Slides =
                [
                    new PresentationSlide { Id = "s1", Layout = SlideLayout.Title, Title = "Zugriffsplan", Notes = "Geheimwort", Sorting = 1, CreatedAt = Ts },
                ],
            });
            db.SaveChanges();
        }
        var search = SearchTestHost.NewService(ctx);
        var viewer = PresentationServiceTests.Viewers["jr"]();

        Assert.Equal("d1", Assert.Single(Presentations(await search.SearchAsync(new SearchCriteria { Text = "Hafenrazzia" }, viewer))).TargetId);
        Assert.Equal("d1", Assert.Single(Presentations(await search.SearchAsync(new SearchCriteria { Text = "Zugriffsplan" }, viewer))).TargetId);
        Assert.Empty(Presentations(await search.SearchAsync(new SearchCriteria { Text = "Geheimwort" }, viewer)));
    }

    [Fact]
    public async Task A_partner_gets_no_presentation_category_at_all()
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        var partner = ClaimsPrincipalBuilder.Agent("p1").AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();

        var results = await SearchTestHost.NewService(ctx).SearchAsync(new SearchCriteria { Text = "Lagebild" }, partner);

        Assert.DoesNotContain(results.Groups, g => g.Category == nameof(Presentation));
    }

    [Theory]
    [InlineData("inv", "priv,pub")]
    [InlineData("jr", "pub")]
    public async Task The_palette_offers_the_visible_presentations_but_the_mention_picker_none(string viewer, string expected)
    {
        using var ctx = new SqliteTestContext();
        SeedDecksLinkedFromPerson(ctx);
        var search = SearchTestHost.NewService(ctx);
        var user = PresentationServiceTests.Viewers[viewer]();

        var palette = await search.QuickSearchAsync("Lagebild", user, 8);
        var mentions = await new MentionService(ctx.Factory, search).CandidatesAsync("Lagebild", user);

        Assert.Equal(expected, Sorted(palette.Where(h => h.Category == nameof(Presentation)).Select(h => h.TargetId)));
        // the mention resolver knows neither groups nor invites, so a token would never resolve
        Assert.DoesNotContain(mentions, m => m.Type == nameof(Presentation));
    }

    [Fact]
    public void A_presentation_is_a_link_target_and_an_anchor_but_no_reference()
    {
        Assert.Contains(nameof(Presentation), LinkService.KnownTypes);
        Assert.Contains(nameof(Presentation), RecordBatch.Linkable);
        Assert.Contains(nameof(Presentation), RecordBatch.LinkAnchors);
        // mentions and compromised-record rows resolve through the scope-free resolver
        Assert.Equal(LinkService.KnownTypes.Where(t => t != nameof(Presentation)), LinkService.ReferenceTypes);
    }
}
