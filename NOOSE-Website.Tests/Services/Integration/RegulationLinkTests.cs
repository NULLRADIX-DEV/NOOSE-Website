using System.Security.Claims;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;
using NSubstitute;
using static NOOSE_Website.Tests.Services.Integration.RegulationFixture;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A regulation paragraph and a warrant as link targets: every internal agent sees a paragraph, person plus case decide a warrant.</summary>
public sealed class RegulationLinkTests
{
    private static LinkService Links(SqliteTestContext ctx) => new(ctx.Factory, Substitute.For<IThreatScoreService>());

    private static ClaimsPrincipal Trainee() => Plain("trainee");

    private static ClaimsPrincipal SectionChief() => ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.SectionChief).Build();

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

    /// <summary>Foreword "pre" with "p0", section 1 "s1" with "p1", "p-rep" (repealed) and "p-trash" (trashed), trashed section "s-gone" with "p-orphan".</summary>
    private static void SeedRegulation(NOOSE_Website.Data.AppDbContext db)
    {
        db.RegulationSections.AddRange(
            SectionRow("pre", 5, "Vorwort", preamble: true),
            SectionRow("s1", 10, "Allgemeines"),
            SectionRow("s-gone", 20, "Weg", configure: s => { s.IsDeleted = true; s.DeletedAt = Stamp; }));
        db.RegulationParagraphs.AddRange(
            ParagraphRow("p0", "pre", "Leitbild"),
            ParagraphRow("p1", "s1", "Funkdisziplin", RegulationAudience.Hrt),
            ParagraphRow("p-rep", "s1", "Altregel", configure: p => { p.RepealedAt = Stamp; p.RepealReason = "überholt"; }),
            ParagraphRow("p-trash", "s1", "Weggeworfen", configure: p => { p.IsDeleted = true; p.DeletedAt = Stamp; }),
            ParagraphRow("p-orphan", "s-gone", "Verwaist"));
    }

    [Fact]
    public void A_paragraph_and_a_warrant_are_link_targets_and_anchors()
    {
        Assert.Contains(nameof(RegulationParagraph), LinkService.KnownTypes);
        Assert.Contains(nameof(Warrant), LinkService.KnownTypes);
        Assert.Contains(nameof(RegulationParagraph), RecordBatch.Linkable);
        Assert.Contains(nameof(Warrant), RecordBatch.Linkable);
        Assert.Contains(nameof(RegulationParagraph), RecordBatch.LinkAnchors);
        Assert.Contains(nameof(Warrant), RecordBatch.LinkAnchors);

        // a paragraph reads from its row alone and may be mentioned; a warrant needs person and case, so it may not
        Assert.Contains(nameof(RegulationParagraph), LinkService.ReferenceTypes);
        Assert.DoesNotContain(nameof(Warrant), LinkService.ReferenceTypes);
        // the section and the versions are no targets of their own
        Assert.DoesNotContain(nameof(RegulationSection), LinkService.KnownTypes);
        Assert.DoesNotContain(nameof(RegulationParagraphVersion), LinkService.KnownTypes);
    }

    [Fact]
    public async Task A_paragraph_link_shows_its_citation_to_every_internal_agent_whatever_its_audience()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Cases.Add(Seed.Case("c1", "Ermittlung"));
            SeedRegulation(seed);
            foreach (var id in new[] { "p0", "p1", "p-rep", "p-trash", "p-orphan" })
            {
                seed.Links.Add(LinkRow(nameof(Case), "c1", nameof(RegulationParagraph), id));
            }
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        async Task<List<LinkDisplay>> For(ClaimsPrincipal user)
            => await svc.GetForRecordAsync(nameof(Case), "c1", ViewerScope.From(user));

        // a trainee in the CID reads the HRT paragraph too; trashed rows and trashed sections drop out
        var trainee = await For(Trainee());
        Assert.Equal("p-rep,p0,p1", Sorted(trainee.Select(l => l.OtherId)));
        var link = Assert.Single(trainee, l => l.OtherId == "p1");
        Assert.Equal("DVO Abschnitt 1 · Funkdisziplin", link.OtherDesignation);
        Assert.Equal("/dienstverordnung/absatz/p1", link.Href);
        Assert.Equal("DVO Vorwort · Leitbild", Assert.Single(trainee, l => l.OtherId == "p0").OtherDesignation);

        Assert.Equal("p-rep,p0,p1", Sorted((await For(SectionChief())).Select(l => l.OtherId)));
        // a partner never reaches the case, let alone the paragraph
        Assert.Empty(await For(Partner()));
    }

    [Fact]
    public async Task A_case_listed_on_the_paragraph_follows_the_case_rules()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Cases.Add(Seed.Case("c1", "Ermittlung"));
            SeedRegulation(seed);
            seed.Links.Add(LinkRow(nameof(RegulationParagraph), "p1", nameof(Case), "c1"));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        var onParagraph = Assert.Single(await svc.GetForRecordAsync(nameof(RegulationParagraph), "p1", ViewerScope.From(Trainee())));
        Assert.Equal((nameof(Case), "c1"), (onParagraph.OtherType, onParagraph.OtherId));
        Assert.Empty(await svc.GetForRecordAsync(nameof(RegulationParagraph), "p1", ViewerScope.From(Partner())));
    }

    [Fact]
    public async Task Linking_a_paragraph_the_actor_cannot_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Cases.Add(Seed.Case("c1", "Ermittlung"));
            SeedRegulation(seed);
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        // a forged, trashed or orphaned id is refused alike, even for leadership
        foreach (var id in new[] { "gibt-es-nicht", "p-trash", "p-orphan" })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(
                nameof(Case), "c1", nameof(RegulationParagraph), id, null, SectionChief()));
        }
        // a partner may not link the regulation anywhere
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CreateAsync(
            nameof(Case), "c1", nameof(RegulationParagraph), "p1", null, Partner()));
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(await db.Links.ToListAsync());
        }

        // any internal agent links any live paragraph, whatever its audience
        await svc.CreateAsync(nameof(Case), "c1", nameof(RegulationParagraph), "p1", null, Trainee());

        await using var after = ctx.NewContext();
        Assert.Equal("p1", Assert.Single(await after.Links.ToListAsync()).TargetId);
    }

    [Fact]
    public async Task Linking_to_a_warrant_the_actor_cannot_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            SeedRegulation(seed);
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(
            nameof(RegulationParagraph), "p1", nameof(Warrant), "w-vs", null, Trainee()));
        await svc.CreateAsync(nameof(RegulationParagraph), "p1", nameof(Warrant), "w-open", null, Trainee());
        await svc.CreateAsync(nameof(RegulationParagraph), "p1", nameof(Warrant), "w-vs", null, SectionChief());

        await using var db = ctx.NewContext();
        Assert.Equal(2, await db.Links.CountAsync());
    }

    [Fact]
    public async Task A_warrant_lists_its_paragraph_and_the_paragraph_its_warrant()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.Add(Seed.Person("p1", "Otto"));
            seed.Warrants.Add(WarrantRow("w1", "p1", "FIB-AN-2026-0001"));
            SeedRegulation(seed);
            seed.Links.Add(LinkRow(nameof(Warrant), "w1", nameof(RegulationParagraph), "p1"));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);
        var viewer = ViewerScope.From(Trainee());

        var onWarrant = Assert.Single(await svc.GetForRecordAsync(nameof(Warrant), "w1", viewer));
        Assert.Equal((nameof(RegulationParagraph), "p1"), (onWarrant.OtherType, onWarrant.OtherId));
        Assert.Equal("DVO Abschnitt 1 · Funkdisziplin", onWarrant.OtherDesignation);
        Assert.Equal("/dienstverordnung/absatz/p1", onWarrant.Href);

        var onParagraph = Assert.Single(await svc.GetForRecordAsync(nameof(RegulationParagraph), "p1", viewer));
        Assert.Equal((nameof(Warrant), "w1"), (onParagraph.OtherType, onParagraph.OtherId));
        Assert.Equal($"{WarrantTypeDisplay.Name(WarrantType.Arrest)} FIB-AN-2026-0001", onParagraph.OtherDesignation);
        Assert.Equal("/anordnungen/w1", onParagraph.Href);
    }

    [Fact]
    public async Task A_warrant_whose_person_is_hidden_drops_out_of_the_paragraph_links()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            SeedRegulation(seed);
            seed.Links.AddRange(
                LinkRow(nameof(Warrant), "w-open", nameof(RegulationParagraph), "p1"),
                LinkRow(nameof(Warrant), "w-vs", nameof(RegulationParagraph), "p1"));
            await seed.SaveChangesAsync();
        }
        var svc = Links(ctx);

        var trainee = await svc.GetForRecordAsync(nameof(RegulationParagraph), "p1", ViewerScope.From(Trainee()));
        var chief = await svc.GetForRecordAsync(nameof(RegulationParagraph), "p1", ViewerScope.From(SectionChief()));

        Assert.Equal("w-open", Assert.Single(trainee).OtherId);
        Assert.Equal("w-open,w-vs", Sorted(chief.Select(l => l.OtherId)));
        // and from the warrant's own side the hidden one is no page at all
        Assert.Empty(await svc.GetForRecordAsync(nameof(Warrant), "w-vs", ViewerScope.From(Trainee())));
    }

    [Fact]
    public async Task The_records_reference_names_a_paragraph_never_classified()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            SeedRegulation(seed);
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var map = await RecordsReference.ResolveAsync(db,
            [(nameof(RegulationParagraph), "p1"), (nameof(RegulationParagraph), "p0"),
             (nameof(RegulationParagraph), "p-rep"), (nameof(RegulationParagraph), "p-trash")]);

        var p1 = map[(nameof(RegulationParagraph), "p1")];
        Assert.False(p1.Classified);
        Assert.Equal("DVO Abschnitt 1 · Funkdisziplin", p1.Display);
        Assert.Equal("/dienstverordnung/absatz/p1", p1.Href);
        Assert.Equal("DVO Vorwort · Leitbild", map[(nameof(RegulationParagraph), "p0")].Display);
        // a repealed paragraph stays readable, so it keeps its label
        var repealed = map[(nameof(RegulationParagraph), "p-rep")];
        Assert.Equal("DVO Abschnitt 1 · Altregel", repealed.Display);
        Assert.False(repealed.Classified);
        // a trashed one is unresolved
        Assert.False(map.ContainsKey((nameof(RegulationParagraph), "p-trash")));
    }

    [Fact]
    public async Task A_mention_of_a_paragraph_reads_its_citation_for_every_agent()
    {
        using var ctx = new SqliteTestContext();
        // a mention token only carries a GUID
        var id = Guid.NewGuid().ToString();
        await using (var seed = ctx.NewContext())
        {
            seed.RegulationSections.Add(SectionRow("s1", 10, "Allgemeines"));
            seed.RegulationParagraphs.Add(ParagraphRow(id, "s1", "Funkdisziplin", RegulationAudience.Administration));
            await seed.SaveChangesAsync();
        }

        var mentions = new MentionService(ctx.Factory, Substitute.For<ISearchService>());
        var text = $"Siehe {MentionParser.Token(nameof(RegulationParagraph), id)}.";

        foreach (var (isLeadership, meId) in new[] { (false, "trainee"), (true, "chief") })
        {
            var reference = Assert.Single((await mentions.ResolveAsync(text, isLeadership, meId)).Where(s => s.IsReference));
            Assert.False(reference.Hidden);
            Assert.Equal("DVO Abschnitt 1 · Funkdisziplin", reference.Text);
            Assert.Equal($"/dienstverordnung/absatz/{id}", reference.Href);
        }
    }

    [Fact]
    public async Task The_search_parent_resolver_knows_paragraphs_and_warrants()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.People.AddRange(Seed.Person("p-open", "Otto"), Seed.Person("p-vs", "Vera", p => p.IsClassified = true));
            seed.Warrants.AddRange(WarrantRow("w-open", "p-open", "FIB-AN-2026-0001"), WarrantRow("w-vs", "p-vs", "FIB-AN-2026-0002"));
            SeedRegulation(seed);
            await seed.SaveChangesAsync();
        }
        (string, string)[] refs =
        [
            (nameof(RegulationParagraph), "p1"), (nameof(RegulationParagraph), "p-rep"),
            (nameof(RegulationParagraph), "p-trash"), (nameof(RegulationParagraph), "p-orphan"),
            (nameof(Warrant), "w-open"), (nameof(Warrant), "w-vs"),
        ];

        async Task<IReadOnlyDictionary<(string Type, string Id), ParentView>> Resolve(ClaimsPrincipal user)
        {
            await using var db = ctx.NewContext();
            return await SearchParentResolver.ResolveVisibleAsync(db, refs,
                new SearchViewer(user, ViewerScope.From(user), null, DateTime.UtcNow));
        }

        var chief = await Resolve(SectionChief());
        Assert.Equal(4, chief.Count);
        var paragraph = chief[(nameof(RegulationParagraph), "p1")];
        Assert.Equal("Funkdisziplin", paragraph.Title);
        Assert.Equal("DVO Abschnitt 1 · Funkdisziplin", paragraph.CaseNumber);
        Assert.Contains((nameof(RegulationParagraph), "p-rep"), chief.Keys);
        var warrant = chief[(nameof(Warrant), "w-open")];
        Assert.Equal("Wohnung Grove Street", warrant.Title);
        Assert.Equal("FIB-AN-2026-0001", warrant.CaseNumber);

        // absent means hide: the warrant of a classified person drops out, the paragraph does not
        var trainee = await Resolve(Trainee());
        Assert.Equal(new[] { (nameof(RegulationParagraph), "p-rep"), (nameof(RegulationParagraph), "p1"), (nameof(Warrant), "w-open") },
            trainee.Keys.Order());

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
        var user = viewer == "chief" ? SectionChief() : Trainee();

        var entries = (await new TimelineService(ctx.Factory).GetTimelineAsync(nameof(Case), "c1", user))
            .Where(e => e.Category == TimelineCategory.Link).OrderBy(e => e.Timestamp).ToList();

        Assert.Equal(expected, string.Join(',', entries.Select(e => e.Title)));
        // a hidden one carries no link to the page
        Assert.All(entries, e => Assert.Equal(e.Title.Contains("nicht verfügbare", StringComparison.Ordinal), e.Href is null));
        Assert.All(entries.Where(e => e.Href is not null), e => Assert.StartsWith("/anordnungen/", e.Href, StringComparison.Ordinal));
    }
}
