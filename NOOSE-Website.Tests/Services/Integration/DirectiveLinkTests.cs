using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A directive and a warrant as link targets: the level of the row, and person plus case, decide who sees the link.</summary>
public sealed class DirectiveLinkTests
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LinkService Links(SqliteTestContext ctx) => new(ctx.Factory, Substitute.For<IThreatScoreService>());

    private static ClaimsPrincipal Trainee() => ClaimsPrincipalBuilder.Agent("trainee").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Tru() => ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).AsTru().Build();

    private static ClaimsPrincipal Hrb() => ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).AsHrb().Build();

    private static ClaimsPrincipal Chief() => ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.SectionChief).Build();

    private static ClaimsPrincipal Partner() => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director)
        .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    private static Directive Row(string id, int number, string title, DocumentClassification level = DocumentClassification.None)
        => new()
        {
            Id = id,
            Number = number,
            Title = title,
            Classification = level,
            CurrentVersion = 1,
            AcknowledgeFromVersion = 1,
            CreatedAt = Stamp,
        };

    private static Warrant WarrantRow(string id, string personId, string caseNumber, WarrantType type = WarrantType.Arrest)
        => new()
        {
            Id = id,
            CaseNumber = caseNumber,
            Type = type,
            Status = WarrantStatus.Signed,
            PersonId = personId,
            Subject = "Wohnung Grove Street",
            CreatedAt = Stamp,
        };

    private static Link LinkRow(string sourceType, string sourceId, string targetType, string targetId)
        => new() { SourceType = sourceType, SourceId = sourceId, TargetType = targetType, TargetId = targetId };

    private static string Sorted(IEnumerable<string> ids) => string.Join(',', ids.Order(StringComparer.Ordinal));

    [Fact]
    public void A_directive_and_a_warrant_are_link_targets_and_anchors()
    {
        Assert.Contains(nameof(Directive), LinkService.KnownTypes);
        Assert.Contains(nameof(Warrant), LinkService.KnownTypes);
        Assert.Contains(nameof(Directive), RecordBatch.Linkable);
        Assert.Contains(nameof(Warrant), RecordBatch.Linkable);
        Assert.Contains(nameof(Directive), RecordBatch.LinkAnchors);
        Assert.Contains(nameof(Warrant), RecordBatch.LinkAnchors);

        // a directive reads from its row alone and may be mentioned; a warrant needs person and case, so it may not
        Assert.Contains(nameof(Directive), LinkService.ReferenceTypes);
        Assert.DoesNotContain(nameof(Warrant), LinkService.ReferenceTypes);
    }

    [Fact]
    public async Task A_tru_directive_link_shows_to_tru_members_and_hides_from_others()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Cases.Add(Seed.Case("c1", "Ermittlung"));
            seed.Directives.AddRange(
                Row("d-tru", 1, "Zugriffsplan", DocumentClassification.Tru),
                Row("d-hrb", 2, "Bewerberlage", DocumentClassification.Hrb),
                Row("d-open", 3, "Funkdisziplin"));
            foreach (var id in new[] { "d-tru", "d-hrb", "d-open" })
            {
                seed.Links.Add(LinkRow(nameof(Case), "c1", nameof(Directive), id));
            }
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        async Task<List<LinkDisplay>> For(ClaimsPrincipal user)
            => await svc.GetForRecordAsync(nameof(Case), "c1", ViewerScope.From(user));

        // the exact level: a TRU member sees the TRU directive, which "leadership only" would have hidden
        var tru = await For(Tru());
        Assert.Equal("d-open,d-tru", Sorted(tru.Select(l => l.OtherId)));
        var truLink = Assert.Single(tru, l => l.OtherId == "d-tru");
        Assert.Equal("DA-01 Zugriffsplan", truLink.OtherDesignation);
        Assert.Equal("/dienstanweisungen/d-tru", truLink.Href);

        Assert.Equal("d-hrb,d-open", Sorted((await For(Hrb())).Select(l => l.OtherId)));
        Assert.Equal("d-open", Sorted((await For(Trainee())).Select(l => l.OtherId)));
        Assert.Equal("d-hrb,d-open,d-tru", Sorted((await For(Chief())).Select(l => l.OtherId)));
        // a partner never reaches the case, let alone the directive
        Assert.Empty(await For(Partner()));
    }

    [Fact]
    public async Task Linking_to_a_directive_the_actor_cannot_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Cases.Add(Seed.Case("c1", "Ermittlung"));
            seed.Directives.Add(Row("d-tru", 1, "Zugriffsplan", DocumentClassification.Tru));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(
            nameof(Case), "c1", nameof(Directive), "d-tru", null, Trainee()));
        // a forged id is refused alike, even for leadership
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(
            nameof(Case), "c1", nameof(Directive), "gibt-es-nicht", null, Chief()));
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(await db.Links.ToListAsync());
        }

        await svc.CreateAsync(nameof(Case), "c1", nameof(Directive), "d-tru", null, Tru());

        await using var after = ctx.NewContext();
        Assert.Equal("d-tru", Assert.Single(await after.Links.ToListAsync()).TargetId);
    }

    [Fact]
    public async Task Linking_to_a_warrant_the_actor_cannot_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            seed.Directives.Add(Row("d1", 1, "Zugriffsplan"));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(
            nameof(Directive), "d1", nameof(Warrant), "w-vs", null, Trainee()));
        await svc.CreateAsync(nameof(Directive), "d1", nameof(Warrant), "w-open", null, Trainee());
        await svc.CreateAsync(nameof(Directive), "d1", nameof(Warrant), "w-vs", null, Chief());

        await using var db = ctx.NewContext();
        Assert.Equal(2, await db.Links.CountAsync());
    }

    [Fact]
    public async Task A_warrant_lists_its_directive_and_the_directive_its_warrant()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.Add(Seed.Person("p1", "Otto"));
            seed.Warrants.Add(WarrantRow("w1", "p1", "FIB-AN-2026-0001"));
            seed.Directives.Add(Row("d1", 1, "Zugriffsplan"));
            seed.Links.Add(LinkRow(nameof(Warrant), "w1", nameof(Directive), "d1"));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);
        var viewer = ViewerScope.From(Trainee());

        var onWarrant = Assert.Single(await svc.GetForRecordAsync(nameof(Warrant), "w1", viewer));
        Assert.Equal((nameof(Directive), "d1"), (onWarrant.OtherType, onWarrant.OtherId));
        Assert.Equal("DA-01 Zugriffsplan", onWarrant.OtherDesignation);
        Assert.Equal("/dienstanweisungen/d1", onWarrant.Href);

        var onDirective = Assert.Single(await svc.GetForRecordAsync(nameof(Directive), "d1", viewer));
        Assert.Equal((nameof(Warrant), "w1"), (onDirective.OtherType, onDirective.OtherId));
        Assert.Equal($"{WarrantTypeDisplay.Name(WarrantType.Arrest)} FIB-AN-2026-0001", onDirective.OtherDesignation);
        Assert.Equal("/anordnungen/w1", onDirective.Href);
    }

    [Fact]
    public async Task A_warrant_whose_person_is_hidden_drops_out_of_the_directive_links()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            seed.Directives.Add(Row("d1", 1, "Zugriffsplan"));
            seed.Links.AddRange(
                LinkRow(nameof(Warrant), "w-open", nameof(Directive), "d1"),
                LinkRow(nameof(Warrant), "w-vs", nameof(Directive), "d1"));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        var trainee = await svc.GetForRecordAsync(nameof(Directive), "d1", ViewerScope.From(Trainee()));
        var chief = await svc.GetForRecordAsync(nameof(Directive), "d1", ViewerScope.From(Chief()));

        Assert.Equal("w-open", Assert.Single(trainee).OtherId);
        Assert.Equal("w-open,w-vs", Sorted(chief.Select(l => l.OtherId)));
        // and from the warrant's own side the hidden one is no page at all
        Assert.Empty(await svc.GetForRecordAsync(nameof(Warrant), "w-vs", ViewerScope.From(Trainee())));
    }

    [Fact]
    public async Task A_mention_of_a_classified_directive_reads_as_classified_below_leadership()
    {
        using var ctx = new SqliteTestContext();
        // a mention token only carries a GUID
        var openId = Guid.NewGuid().ToString();
        var truId = Guid.NewGuid().ToString();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.AddRange(
                Row(openId, 1, "Funkdisziplin"),
                Row(truId, 2, "Zugriffsplan", DocumentClassification.Tru));
            await seed.SaveChangesAsync();
        }

        // the resolver has no viewer, so every level counts as classified
        await using (var db = ctx.NewContext())
        {
            var map = await RecordsReference.ResolveAsync(db,
                [(nameof(Directive), openId), (nameof(Directive), truId)]);
            Assert.False(map[(nameof(Directive), openId)].Classified);
            Assert.True(map[(nameof(Directive), truId)].Classified);
            Assert.Equal("DA-01 Funkdisziplin", map[(nameof(Directive), openId)].Display);
            Assert.Equal($"/dienstanweisungen/{openId}", map[(nameof(Directive), openId)].Href);
        }

        var mentions = new MentionService(ctx.Factory, Substitute.For<ISearchService>());
        var text = $"Siehe {MentionParser.Token(nameof(Directive), openId)} und {MentionParser.Token(nameof(Directive), truId)}.";

        var plain = (await mentions.ResolveAsync(text, isLeadership: false, meId: "tru")).Where(s => s.IsReference).ToList();
        Assert.Equal(2, plain.Count);
        Assert.Equal("DA-01 Funkdisziplin", plain[0].Text);
        Assert.False(plain[0].Hidden);
        Assert.True(plain[1].Hidden);
        Assert.Null(plain[1].Href);
        Assert.DoesNotContain("Zugriffsplan", plain[1].Text, StringComparison.Ordinal);

        var lead = (await mentions.ResolveAsync(text, isLeadership: true, meId: "chief")).Where(s => s.IsReference).ToList();
        Assert.All(lead, s => Assert.False(s.Hidden));
        Assert.Equal("DA-02 Zugriffsplan", lead[1].Text);
        Assert.Equal($"/dienstanweisungen/{truId}", lead[1].Href);
    }

    [Fact]
    public async Task The_search_parent_resolver_knows_directives_and_warrants()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            seed.Directives.AddRange(
                Row("d-open", 1, "Funkdisziplin"),
                Row("d-tru", 2, "Zugriffsplan", DocumentClassification.Tru));
            await seed.SaveChangesAsync();
        }
        (string, string)[] refs =
        [
            (nameof(Directive), "d-open"), (nameof(Directive), "d-tru"),
            (nameof(Warrant), "w-open"), (nameof(Warrant), "w-vs"),
        ];

        async Task<IReadOnlyDictionary<(string Type, string Id), ParentView>> Resolve(ClaimsPrincipal user)
        {
            await using var db = ctx.NewContext();
            return await SearchParentResolver.ResolveVisibleAsync(db, refs,
                new SearchViewer(user, ViewerScope.From(user), null, DateTime.UtcNow));
        }

        var chief = await Resolve(Chief());
        Assert.Equal(4, chief.Count);
        var directive = chief[(nameof(Directive), "d-open")];
        Assert.Equal("Funkdisziplin", directive.Title);
        Assert.Equal("DA-01", directive.CaseNumber);
        var warrant = chief[(nameof(Warrant), "w-open")];
        Assert.Equal("Wohnung Grove Street", warrant.Title);
        Assert.Equal("FIB-AN-2026-0001", warrant.CaseNumber);

        // absent means hide: the TRU directive and the warrant of a classified person drop out
        var trainee = await Resolve(Trainee());
        Assert.Equal(new[] { (nameof(Directive), "d-open"), (nameof(Warrant), "w-open") }, trainee.Keys.Order());
        Assert.Contains((nameof(Directive), "d-tru"), (await Resolve(Tru())).Keys);

        Assert.Empty(await Resolve(Partner()));
    }

    [Theory]
    [InlineData("chief", "Verknüpft mit Anordnung FIB-AN-2026-0001,Verknüpft mit Anordnung FIB-AN-2026-0002")]
    [InlineData("trainee", "Verknüpft mit Anordnung FIB-AN-2026-0001,Verknüpft mit nicht verfügbare Akte")]
    public async Task The_timeline_names_a_linked_warrant_only_for_who_may_see_it(string viewer, string expected)
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Cases.Add(Seed.Case("c1", "Ermittlung"));
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            var first = LinkRow(nameof(Case), "c1", nameof(Warrant), "w-open");
            first.CreatedAt = Stamp;
            var second = LinkRow(nameof(Case), "c1", nameof(Warrant), "w-vs");
            second.CreatedAt = Stamp.AddMinutes(1);
            seed.Links.AddRange(first, second);
            await seed.SaveChangesAsync();
        }
        var user = viewer == "chief" ? Chief() : Trainee();

        var entries = (await new TimelineService(ctx.Factory).GetTimelineAsync(nameof(Case), "c1", user))
            .Where(e => e.Category == TimelineCategory.Link).OrderBy(e => e.Timestamp).ToList();

        Assert.Equal(expected, string.Join(',', entries.Select(e => e.Title)));
        // a hidden one carries no link to the page
        Assert.All(entries, e => Assert.Equal(e.Title.Contains("nicht verfügbare", StringComparison.Ordinal), e.Href is null));
        Assert.All(entries.Where(e => e.Href is not null), e => Assert.StartsWith("/anordnungen/", e.Href, StringComparison.Ordinal));
    }
}
