using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Graph;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="GraphService"/> against in-memory SQLite.</summary>
public sealed class GraphServiceTests
{
    // MaxNode in the service (private const).
    private const int MaxNode = 1500;

    private static GraphService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => sees classified nodes.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // JuniorAgent: not leadership, classified nodes hidden.
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.DoJ, PartnerRank.Member).Build();

    // ---- GetGraphAsync -----------------------------------------------------

    [Fact]
    public async Task GetGraphAsync_ReturnsEmpty_ForPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Partner());

        Assert.Empty(result.Node);
        Assert.Empty(result.Edges);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GetGraphAsync_PartnerWithTheGraphFunction_SeesReleasedRecordsOnly_NeverDocs()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.People.Add(Seed.Person("p3", "Geheim"));
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.IsBadFaction = true));
            db.PersonDocs.Add(new PersonDoc { Id = "d1", PersonId = "p1", Timestamp = DateTime.UtcNow });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p3" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "PersonDoc", TargetId = "d1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.DoJ, Features = PartnerFeature.Graph });
            db.PartnerReleaseRules.Add(new PartnerReleaseRule { Agency = PartnerAgency.DoJ, EntityType = "Faction", Scope = PartnerRuleScope.BadFactions });
            db.PartnerReleaseRules.Add(new PartnerReleaseRule { Agency = PartnerAgency.DoJ, EntityType = "Person", Scope = PartnerRuleScope.BadFactionMembers });
            db.PartnerShares.Add(new PartnerShare { EntityType = "Person", EntityId = "p2", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetGraphAsync(new GraphQuery(), Partner());

        Assert.Equal(new[] { "Faction:f1", "Person:p1", "Person:p2" }, result.Node.Select(n => n.Id).OrderBy(i => i));
        Assert.All(result.Edges, e => Assert.DoesNotContain("PersonDoc", e.Source + e.Target));
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public async Task GetGraphAsync_Partner_HonoursTheRankAllowlist_AndSeesReleasedTaskforces()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Taskforces.Add(new NOOSE_Website.Data.Entities.Taskforces.Taskforce { Id = "tf1", Name = "TF", CaseNumber = "NOOSE-T-2026-9001" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Taskforce", TargetId = "tf1" });
            db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.DoJ, Features = PartnerFeature.Graph });
            foreach (var (type, id) in new[] { ("Person", "p1"), ("Faction", "f1"), ("Taskforce", "tf1") })
            {
                db.PartnerShares.Add(new PartnerShare { EntityType = type, EntityId = id, Agency = PartnerAgency.DoJ });
            }
            var cfg = new NOOSE_Website.Models.Common.PartnerVisibilityConfig();
            cfg.Ranks[NOOSE_Website.Models.Common.PartnerVisibilityConfig.RankKey(PartnerAgency.DoJ, PartnerRank.Member)] =
                new NOOSE_Website.Models.Common.PartnerRankVisibility { Types = { "Person", "Taskforce" } };
            db.SystemSettings.Add(new SystemSetting { Key = "PartnerRangSichtbarkeit", Value = System.Text.Json.JsonSerializer.Serialize(cfg) });
            db.SaveChanges();
        }
        var policy = new PartnerVisibilityPolicyService(ctx.Factory, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        var result = await new GraphService(ctx.Factory, policy).GetGraphAsync(new GraphQuery(), Partner());

        Assert.Equal(new[] { "Person:p1", "Taskforce:tf1" }, result.Node.Select(n => n.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task FindPathAsync_PartnerWithTheGraphFunction_NeverRoutesThroughAnUnreleasedRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("hidden", "Mittelsmann"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "hidden" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "hidden", TargetType = "Person", TargetId = "p2" });
            db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.DoJ, Features = PartnerFeature.Graph });
            db.PartnerShares.Add(new PartnerShare { EntityType = "Person", EntityId = "p1", Agency = PartnerAgency.DoJ });
            db.PartnerShares.Add(new PartnerShare { EntityType = "Person", EntityId = "p2", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }

        var result = await NewService(ctx).FindPathAsync("Person", "p1", "Person", "p2", Partner());

        Assert.False(result.Found);
    }

    [Fact]
    public async Task GetGraphAsync_BuildsFullGraph_FromLinks_AndResolvesMetadata()
    {
        using var ctx = new SqliteTestContext();
        var p1 = Seed.Person("p1", "Max", p => p.Classification = Classification.SuspicionCase);
        using (var db = ctx.NewContext())
        {
            db.People.Add(p1);
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2", Label = "kennt" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        Assert.Equal(2, result.Node.Count);
        Assert.False(result.Truncated);

        var node = Assert.Single(result.Node, n => n.Id == "Person:p1");
        Assert.Equal("Person", node.Type);
        Assert.Equal("Max", node.Designation);
        Assert.Equal(p1.CaseNumber, node.Subtitle);
        Assert.Equal("/personen/p1", node.Href);
        Assert.Equal((int)Classification.SuspicionCase, node.ClassificationLevel);
        Assert.False(node.IsClassified);

        var edge = Assert.Single(result.Edges);
        Assert.Equal("Person:p1", edge.Source);
        Assert.Equal("Person:p2", edge.Target);
        Assert.Equal("kennt", edge.Label);
        Assert.False(edge.Automatic);
    }

    [Fact]
    public async Task GetGraphAsync_IncludesFactionMembershipEdges()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { PersonId = "p1", FactionId = "f1", IsLead = true });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        Assert.Equal(2, result.Node.Count);
        Assert.Contains(result.Node, n => n.Id == "Faction:f1" && n.Type == "Faction");
        var edge = Assert.Single(result.Edges);
        Assert.Equal("Person:p1", edge.Source);
        Assert.Equal("Faction:f1", edge.Target);
        Assert.Equal("Leitung", edge.Label);
        // membership edges are system-maintained.
        Assert.True(edge.Automatic);
    }

    [Fact]
    public async Task GetGraphAsync_IncludesPersonRelationEdges_MappedByType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.PersonRelations.Add(new PersonRelation { PersonAId = "p1", PersonBId = "p2", Type = RelationType.Enemy });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        var edge = Assert.Single(result.Edges);
        // Enemy relation maps to a conflict edge with the German display label.
        Assert.Equal(LinkKind.Conflict, edge.Kind);
        Assert.Equal("Feind", edge.Label);
        Assert.False(edge.Automatic);
    }

    [Fact]
    public async Task GetGraphAsync_HidesClassifiedNodes_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Junior());

        // classified node dropped; the edge touching it drops with it.
        var node = Assert.Single(result.Node);
        Assert.Equal("Person:p2", node.Id);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task GetGraphAsync_ShowsClassifiedNodes_ToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        Assert.Equal(2, result.Node.Count);
        Assert.Contains(result.Node, n => n.Id == "Person:p1" && n.IsClassified);
        Assert.Single(result.Edges);
    }

    [Fact]
    public async Task GetGraphAsync_AppliesTypeFilter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(TypeFilter: new[] { "Person" }), Leader());

        // faction node filtered out, along with the edge that touched it.
        Assert.Equal(2, result.Node.Count);
        Assert.All(result.Node, n => Assert.Equal("Person", n.Type));
        var edge = Assert.Single(result.Edges);
        Assert.Equal("Person:p1", edge.Source);
        Assert.Equal("Person:p2", edge.Target);
    }

    [Fact]
    public async Task GetGraphAsync_AppliesKindFilter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.People.Add(Seed.Person("p3", "Anna"));
            db.People.Add(Seed.Person("p4", "Bea"));
            db.PersonRelations.Add(new PersonRelation { PersonAId = "p1", PersonBId = "p2", Type = RelationType.Enemy }); // conflict
            db.PersonRelations.Add(new PersonRelation { PersonAId = "p3", PersonBId = "p4", Type = RelationType.Ally });  // alliance
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(KindFilter: LinkKind.Conflict), Leader());

        // only the conflict edge survives; the alliance endpoints never become nodes.
        var edge = Assert.Single(result.Edges);
        Assert.Equal(LinkKind.Conflict, edge.Kind);
        Assert.Equal(2, result.Node.Count);
        Assert.Contains(result.Node, n => n.Id == "Person:p1");
        Assert.Contains(result.Node, n => n.Id == "Person:p2");
    }

    [Fact]
    public async Task GetGraphAsync_FocusMode_ReturnsNeighborhood()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.People.Add(Seed.Person("p3", "Anna"));
            db.People.Add(Seed.Person("p4", "Bea"));
            // chain p1 - p2 - p3 - p4
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p2", TargetType = "Person", TargetId = "p3" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p3", TargetType = "Person", TargetId = "p4" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(FocusType: "Person", FocusId: "p1", Depth: 1), Leader());

        // one hop around p1: p1 and p2 only.
        Assert.Equal(2, result.Node.Count);
        Assert.Contains(result.Node, n => n.Id == "Person:p1");
        Assert.Contains(result.Node, n => n.Id == "Person:p2");
        Assert.DoesNotContain(result.Node, n => n.Id == "Person:p3");
        var edge = Assert.Single(result.Edges);
        Assert.Equal("Person:p1", edge.Source);
        Assert.Equal("Person:p2", edge.Target);
    }

    [Fact]
    public async Task GetGraphAsync_FocusMode_ReturnsEmpty_WhenFocusNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // focus node is classified -> invisible to a junior -> whole graph empties.
        var result = await svc.GetGraphAsync(new GraphQuery(FocusType: "Person", FocusId: "p1", Depth: 2), Junior());

        Assert.Empty(result.Node);
        Assert.Empty(result.Edges);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GetGraphAsync_Truncates_WhenOverMaxNode()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            var people = new List<Person> { Seed.Person("hub", "Hub") };
            var links = new List<Link>();
            const int spokes = MaxNode + 10; // hub + spokes > MaxNode
            for (var i = 0; i < spokes; i++)
            {
                var id = $"s{i}";
                people.Add(Seed.Person(id, id));
                links.Add(new Link { SourceType = "Person", SourceId = "hub", TargetType = "Person", TargetId = id });
            }
            db.People.AddRange(people);
            db.Links.AddRange(links);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        Assert.True(result.Truncated);
        Assert.Equal(MaxNode, result.Node.Count);
        // hub has the highest degree -> always kept.
        Assert.Contains(result.Node, n => n.Id == "Person:hub");
    }

    [Fact]
    public async Task GetGraphAsync_FocusMode_MarksTheFocusNode()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(
            new GraphQuery(FocusType: "Person", FocusId: "p1", Depth: 1, MarkType: "Person", MarkId: "p1"),
            Leader());

        Assert.True(Assert.Single(result.Node, n => n.Id == "Person:p1").IsFocus);
        Assert.False(Assert.Single(result.Node, n => n.Id == "Person:p2").IsFocus);
    }

    [Fact]
    public async Task GetGraphAsync_FullNetwork_MarksRecord_WithoutRestrictingTheGraph()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.People.Add(Seed.Person("p3", "Anna"));
            // chain p1 - p2 - p3
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p2", TargetType = "Person", TargetId = "p3" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // no focus -> no radius cut, but the pick stays marked.
        var result = await svc.GetGraphAsync(new GraphQuery(MarkType: "Person", MarkId: "p1"), Leader());

        Assert.Equal(3, result.Node.Count);
        Assert.True(Assert.Single(result.Node, n => n.Id == "Person:p1").IsFocus);
        Assert.Equal(1, result.Node.Count(n => n.IsFocus));
    }

    [Fact]
    public async Task GetGraphAsync_MarksNothing_WithoutMarkQuery()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        Assert.NotEmpty(result.Node);
        Assert.DoesNotContain(result.Node, n => n.IsFocus);
    }

    [Fact]
    public async Task GetGraphAsync_MarkedRecordWithoutEdges_StillAppears()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.People.Add(Seed.Person("solo", "Einzelgänger"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(MarkType: "Person", MarkId: "solo"), Leader());

        Assert.True(Assert.Single(result.Node, n => n.Id == "Person:solo").IsFocus);
    }

    [Fact]
    public async Task GetGraphAsync_MarkedRecord_SurvivesTruncation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // the marked record has no edges at all -> strictly the lowest degree -> first to be cut.
            var people = new List<Person> { Seed.Person("hub", "Hub"), Seed.Person("solo", "Einzelgänger") };
            var links = new List<Link>();
            const int spokes = MaxNode + 10; // hub + spokes > MaxNode
            for (var i = 0; i < spokes; i++)
            {
                var id = $"s{i}";
                people.Add(Seed.Person(id, id));
                links.Add(new Link { SourceType = "Person", SourceId = "hub", TargetType = "Person", TargetId = id });
            }
            db.People.AddRange(people);
            db.Links.AddRange(links);
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(MarkType: "Person", MarkId: "solo"), Leader());

        Assert.True(result.Truncated);
        Assert.Equal(MaxNode + 1, result.Node.Count);
        Assert.True(Assert.Single(result.Node, n => n.Id == "Person:solo").IsFocus);
    }

    [Fact]
    public async Task GetGraphAsync_SetsPhotoUrl_WhenPersonHasPhoto()
    {
        using var ctx = new SqliteTestContext();
        var photoId = "photo-1";
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = photoId,
                PersonId = "p1",
                FileNameSaved = "a.jpg",
                OriginalName = "a.jpg",
                ContentType = "image/jpeg",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetGraphAsync(new GraphQuery(), Leader());

        var node = Assert.Single(result.Node, n => n.Id == "Person:p1");
        Assert.Equal($"/dateien/personen/foto/{photoId}", node.PhotoUrl);
    }

    // ---- FindPathAsync -----------------------------------------------------

    [Fact]
    public async Task FindPathAsync_ReturnsNotFound_ForPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.FindPathAsync("Person", "p1", "Person", "p2", Partner());

        Assert.False(result.Found);
        Assert.Empty(result.Node);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task FindPathAsync_FindsShortestPath()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.People.Add(Seed.Person("p3", "Anna"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p2", TargetType = "Person", TargetId = "p3" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.FindPathAsync("Person", "p1", "Person", "p3", Leader());

        Assert.True(result.Found);
        Assert.Equal(3, result.Node.Count);
        Assert.Equal("Person:p1", result.Node[0].Id);
        Assert.Equal("Person:p2", result.Node[1].Id);
        Assert.Equal("Person:p3", result.Node[2].Id);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public async Task FindPathAsync_ReturnsNotFound_WhenNoPath()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // both endpoints exist but nothing connects them.
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.FindPathAsync("Person", "p1", "Person", "p2", Leader());

        Assert.False(result.Found);
        Assert.Empty(result.Node);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task FindPathAsync_ReturnsTrue_ForSameSourceAndTarget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.FindPathAsync("Person", "p1", "Person", "p1", Leader());

        Assert.True(result.Found);
        var node = Assert.Single(result.Node);
        Assert.Equal("Person:p1", node.Id);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task FindPathAsync_ReturnsNotFound_WhenEndpointClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // source is classified -> invisible to a junior -> path cannot resolve.
        var result = await svc.FindPathAsync("Person", "p1", "Person", "p2", Junior());

        Assert.False(result.Found);
        Assert.Empty(result.Node);
    }
}
