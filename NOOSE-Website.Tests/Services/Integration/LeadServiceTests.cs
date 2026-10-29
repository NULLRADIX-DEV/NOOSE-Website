using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Leads;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="LeadService"/> (signals, dismissal, VS).</summary>
public sealed class LeadServiceTests
{
    private static ClaimsPrincipal Agent(string id = "me") => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("low").WithRank(Rank.JuniorAgent).Build();

    private static LeadService Svc(SqliteTestContext ctx) => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static async Task<List<Lead>> FlatAsync(LeadService svc, ClaimsPrincipal viewer)
        => (await svc.GetFeedAsync(viewer)).SelectMany(g => g.Leads).ToList();

    private static PersonRelation Rel(string a, string b) => new() { PersonAId = a, PersonBId = b, Type = RelationType.Known };

    [Fact]
    public async Task GetFeed_PredictsLink_ForTwoPeopleWithSharedNeighbours()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            foreach (var (id, name) in new[] { ("a", "A"), ("b", "B"), ("c", "C"), ("d", "D") })
            {
                db.People.Add(Seed.Person(id: id, name: name));
            }
            // a & b both connect to c and d, but not to each other
            db.PersonRelations.AddRange(Rel("a", "c"), Rel("a", "d"), Rel("b", "c"), Rel("b", "d"));
            db.SaveChanges();
        }
        var leads = await FlatAsync(Svc(ctx), Leader());

        // display is "Name (Aktenzeichen)"
        Assert.Contains(leads, l => l.Kind == LeadKind.LinkPrediction
            && ((l.PrimaryName.StartsWith("A ", StringComparison.Ordinal) && l.SecondaryName!.StartsWith("B ", StringComparison.Ordinal))
                || (l.PrimaryName.StartsWith("B ", StringComparison.Ordinal) && l.SecondaryName!.StartsWith("A ", StringComparison.Ordinal))));
    }

    private static bool IsPair(Lead l, string x, string y)
        => l.Kind == LeadKind.LinkPrediction
        && ((l.PrimaryId == x && l.SecondaryId == y) || (l.PrimaryId == y && l.SecondaryId == x));

    // a & b both connect to c and d; c & d both connect to a and b -> two candidate pairs
    private static void SeedDiamond(SqliteTestContext ctx, Action<NOOSE_Website.Data.AppDbContext>? extra = null)
    {
        using var db = ctx.NewContext();
        foreach (var (id, name) in new[] { ("a", "A"), ("b", "B"), ("c", "C"), ("d", "D") })
        {
            db.People.Add(Seed.Person(id: id, name: name));
        }
        db.PersonRelations.AddRange(Rel("a", "c"), Rel("a", "d"), Rel("b", "c"), Rel("b", "d"));
        extra?.Invoke(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task GetFeed_SkipsLinkPrediction_WhenPairHasRelation()
    {
        using var ctx = new SqliteTestContext();
        SeedDiamond(ctx, db => db.PersonRelations.Add(Rel("a", "b")));

        var leads = await FlatAsync(Svc(ctx), Leader());

        Assert.DoesNotContain(leads, l => IsPair(l, "a", "b"));
        Assert.Contains(leads, l => IsPair(l, "c", "d"));
    }

    [Fact]
    public async Task GetFeed_SkipsLinkPrediction_WhenPairIsAlreadyColleagues()
    {
        using var ctx = new SqliteTestContext();
        // automatic colleague link: dropped by the graph loader, but still a real connection
        SeedDiamond(ctx, db => db.Links.Add(new Link
        {
            SourceType = nameof(Person), SourceId = "a",
            TargetType = nameof(Person), TargetId = "b",
            Label = ColleaguesSync.FactionColleague, Automatic = true, CreatedAt = DateTime.UtcNow,
        }));

        var leads = await FlatAsync(Svc(ctx), Leader());

        Assert.DoesNotContain(leads, l => IsPair(l, "a", "b"));
        Assert.Contains(leads, l => IsPair(l, "c", "d"));
    }

    [Fact]
    public async Task GetFeed_FlagsRecentConflict()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos"));
            db.Links.Add(new Link
            {
                SourceType = nameof(NOOSE_Website.Data.Entities.Factions.Faction), SourceId = "f1",
                TargetType = nameof(NOOSE_Website.Data.Entities.Factions.Faction), TargetId = "f2",
                Kind = LinkKind.Conflict, Automatic = false, CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        var leads = await FlatAsync(Svc(ctx), Leader());

        Assert.Contains(leads, l => l.Kind == LeadKind.NewConflict);
    }

    [Fact]
    public async Task GetFeed_FlagsStaleHighClassification()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Alt", configure: p =>
            {
                p.Classification = Classification.SuspicionCase;
                p.ModifiedAt = DateTime.UtcNow.AddDays(-60);
            }));
            db.SaveChanges();
        }
        var leads = await FlatAsync(Svc(ctx), Leader());

        Assert.Contains(leads, l => l.Kind == LeadKind.StaleHighClassification && l.PrimaryId == "p1");
    }

    [Fact]
    public async Task Ignore_RemovesLead_AndUndoRestores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Alt", configure: p =>
            {
                p.Classification = Classification.SuspicionCase;
                p.ModifiedAt = DateTime.UtcNow.AddDays(-60);
            }));
            db.SaveChanges();
        }
        var svc = Svc(ctx);
        var lead = Assert.Single(await FlatAsync(svc, Leader()));

        await svc.IgnoreAsync(lead.Key, lead.Kind, Leader());
        Assert.Empty(await FlatAsync(svc, Leader()));

        await svc.UndoIgnoreAsync(lead.Key, Leader());
        Assert.Single(await FlatAsync(svc, Leader()));
    }

    [Fact]
    public async Task Ignore_Throws_ForOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var svc = Svc(ctx);
        var onlyReader = ClaimsPrincipalBuilder.Agent("r").AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.IgnoreAsync("ST|Person|x", LeadKind.StaleHighClassification, onlyReader));
    }

    // ---------- possible duplicates ----------

    private static List<Lead> Duplicates(IEnumerable<Lead> leads) => leads.Where(l => l.Kind == LeadKind.Duplicate).ToList();

    private static void SeedPeople(SqliteTestContext ctx, params Person[] people)
    {
        using var db = ctx.NewContext();
        db.People.AddRange(people);
        db.SaveChanges();
    }

    private static DateTime Day(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetFeed_FlagsDuplicate_OlderRecordFirst()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("new", "Karlos Mendes", p => p.CreatedAt = Day(5)),
            Seed.Person("old", "Carlos Mendez", p => p.CreatedAt = Day(1)),
            Seed.Person("x", "Pablo Escobar"));

        var lead = Assert.Single(Duplicates(await FlatAsync(Svc(ctx), Agent())));

        Assert.Equal("old", lead.PrimaryId);
        Assert.Equal("new", lead.SecondaryId);
        Assert.Equal("DU|new|old", lead.Key);
        Assert.Contains("klingt gleich", lead.Detail);
        Assert.Contains(lead.PrimaryName, lead.Detail);
        Assert.Contains(lead.SecondaryName!, lead.Detail);
    }

    [Fact]
    public async Task GetFeed_FlagsDuplicate_NameAgainstAlias()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("a", "Carlos Mendez"),
            Seed.Person("b", "Wer Anders", p => p.Aliases.Add(new PersonAlias { AliasName = "Dr. Carlos Mendez" })));

        var lead = Assert.Single(Duplicates(await FlatAsync(Svc(ctx), Agent())));

        Assert.Contains("gleicher Name", lead.Detail);
        Assert.Contains("Alias „Dr. Carlos Mendez“", lead.Detail);
    }

    [Fact]
    public async Task GetFeed_SkipsAliasAgainstAlias()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("a", "Carlos Mendez", p => p.Aliases.Add(new PersonAlias { AliasName = "Shorty" })),
            Seed.Person("b", "Pablo Escobar", p => p.Aliases.Add(new PersonAlias { AliasName = "Shorty" })));

        Assert.Empty(Duplicates(await FlatAsync(Svc(ctx), Agent())));
    }

    [Fact]
    public async Task GetFeed_FlagsDuplicate_SharedPhone()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("a", "Carlos Mendez", p => p.PhoneNumbers.Add(new PersonPhone { Number = "555-1" })),
            Seed.Person("b", "Pablo Escobar", p => p.PhoneNumbers.Add(new PersonPhone { Number = "555-1" })));

        var lead = Assert.Single(Duplicates(await FlatAsync(Svc(ctx), Agent())));

        Assert.Contains("gleiche Telefonnummer", lead.Detail);
    }

    [Fact]
    public async Task GetFeed_RanksSameNameAboveSoundAlikeAboveTypo()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("t1", "Pablo Escobar"), Seed.Person("t2", "Pablo Escobat"),
            Seed.Person("s1", "Hans Maier"), Seed.Person("s2", "Hans Meyer"),
            Seed.Person("e1", "Anton Klein"), Seed.Person("e2", "Dr. Anton Klein"));

        var leads = Duplicates(await FlatAsync(Svc(ctx), Agent()));

        Assert.Equal(new[] { "DU|e1|e2", "DU|s1|s2", "DU|t1|t2" }, leads.Select(l => l.Key));
    }

    [Fact]
    public async Task GetFeed_MarksArchivedDuplicate()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("a", "Carlos Mendez"),
            Seed.Person("b", "Carlos Mendez", p => p.IsArchived = true));

        var lead = Assert.Single(Duplicates(await FlatAsync(Svc(ctx), Agent())));

        Assert.Contains("Carlos Mendez ist archiviert", lead.Detail);
    }

    [Fact]
    public async Task GetFeed_IgnoredDuplicate_StaysAway()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx, Seed.Person("a", "Carlos Mendez"), Seed.Person("b", "Karlos Mendes"));
        var lead = Assert.Single(Duplicates(await FlatAsync(Svc(ctx), Agent())));

        // any writer may dismiss; a fresh service (new cache) must still keep it out
        await Svc(ctx).IgnoreAsync(lead.Key, lead.Kind, Agent());

        Assert.Empty(Duplicates(await FlatAsync(Svc(ctx), Agent())));
    }

    [Fact]
    public async Task GetFeed_HidesClassifiedDuplicate_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        SeedPeople(ctx,
            Seed.Person("a", "Carlos Mendez"),
            Seed.Person("b", "Carlos Mendez", p => p.IsClassified = true));
        var svc = Svc(ctx);

        Assert.Single(Duplicates(await FlatAsync(svc, Leader())));
        Assert.Empty(Duplicates(await FlatAsync(svc, Agent())));
    }

    [Fact]
    public async Task GetFeed_HidesClassifiedLead_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Geheim", configure: p =>
            {
                p.Classification = Classification.SecuredStateThreatening;
                p.IsClassified = true;
                p.ModifiedAt = DateTime.UtcNow.AddDays(-60);
            }));
            db.SaveChanges();
        }
        var svc = Svc(ctx);

        Assert.NotEmpty(await FlatAsync(svc, Leader()));
        Assert.Empty(await FlatAsync(svc, Junior()));
    }
}
