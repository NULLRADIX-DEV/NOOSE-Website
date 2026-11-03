using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="SearchService"/> against in-memory SQLite.</summary>
public sealed class SearchServiceTests
{
    private static SearchService NewService(SqliteTestContext ctx) => SearchTestHost.NewService(ctx);

    // internal viewer that may read classified and all taskforces.
    private static ClaimsPrincipal Leadership(string meId = "lead")
        => ClaimsPrincipalBuilder.Agent(meId).WithRank(Rank.Director).Build();

    // internal viewer without classified read; sees only own/assigned restricted records.
    private static ClaimsPrincipal Plain(string meId = "agent-1")
        => ClaimsPrincipalBuilder.Agent(meId).WithRank(Rank.Trainee).Build();

    // external partner viewer: only released, non-classified records.
    private static ClaimsPrincipal Partner(PartnerAgency agency = PartnerAgency.DoJ, string partnerAgentId = "pa1")
        => ClaimsPrincipalBuilder.Agent(partnerAgentId).AsPartner(agency, PartnerRank.Chief).Build();

    private static SearchCriteria Text(string? text, bool fuzzy = false, bool max = false)
        => new() { Text = text, Fuzzy = fuzzy, MaxMode = max };

    private static SearchResultGroup? Group(SearchResults results, string category)
        => results.Groups.FirstOrDefault(g => g.Category == category);

    private static PartnerShare Share(string type, string id, PartnerAgency agency = PartnerAgency.DoJ)
        => new() { EntityType = type, EntityId = id, Agency = agency, PartnerAgentId = null };

    // ---- SearchAsync: people ------------------------------------------------

    [Fact]
    public async Task SearchAsync_People_MatchesByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton Meier"));
            db.People.Add(Seed.Person("p2", "Bruno Schulz"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Anton"), Leadership());

        var people = Group(groups, nameof(Person));
        Assert.NotNull(people);
        var hit = Assert.Single(people!.Hit);
        Assert.Equal("p1", hit.TargetId);
    }

    [Fact]
    public async Task SearchAsync_People_MatchesByCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton", p => p.CaseNumber = "FIB-P-2026-9001"));
            db.People.Add(Seed.Person("p2", "Bruno", p => p.CaseNumber = "FIB-P-2026-9002"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("9001"), Leadership());

        var hit = Assert.Single(Group(groups, nameof(Person))!.Hit);
        Assert.Equal("p1", hit.TargetId);
    }

    [Fact]
    public async Task SearchAsync_ExcludesClassifiedPeople_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("open", "Open Person"));
            db.People.Add(Seed.Person("secret", "Secret Person", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forPlain = await svc.SearchAsync(Text("Person"), Plain());
        var forLead = await svc.SearchAsync(Text("Person"), Leadership());

        Assert.Equal(new[] { "open" }, Group(forPlain, nameof(Person))!.Hit.Select(h => h.TargetId).ToArray());
        Assert.Equal(2, Group(forLead, nameof(Person))!.Hit.Count);
    }

    [Fact]
    public async Task SearchAsync_EmptyText_ListsAllVisiblePeople()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton"));
            db.People.Add(Seed.Person("p2", "Bruno"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // no text and no tags: still lists all visible persons for browsing.
        var groups = await svc.SearchAsync(Text("   "), Leadership());

        Assert.Equal(2, Group(groups, nameof(Person))!.Hit.Count);
    }

    // ---- SearchAsync: other record types ------------------------------------

    [Fact]
    public async Task SearchAsync_Factions_MatchesByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Grove Street"));
            db.Factions.Add(Seed.Faction("f2", "Ballas"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Grove"), Leadership());

        var hit = Assert.Single(Group(groups, nameof(Faction))!.Hit);
        Assert.Equal("f1", hit.TargetId);
    }

    [Fact]
    public async Task SearchAsync_Cases_MatchesByTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(Seed.Case("c1", "Mordfall Vinewood"));
            db.Cases.Add(Seed.Case("c2", "Diebstahl Downtown"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Mordfall"), Leadership());

        var hit = Assert.Single(Group(groups, nameof(Case))!.Hit);
        Assert.Equal("c1", hit.TargetId);
    }

    [Fact]
    public async Task SearchAsync_Laws_MatchByTitleAndBook()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(new Law { Id = "l1", LawBook = "StGB", Paragraph = "§ 263", Title = "Betrug", Text = "Volltext" });
            db.Laws.Add(new Law { Id = "l2", LawBook = "StVO", Paragraph = "§ 1", Title = "Ampel", Text = "Volltext" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var byTitle = await svc.SearchAsync(Text("Betrug"), Leadership());
        var byBook = await svc.SearchAsync(Text("StVO"), Leadership());

        Assert.Equal("l1", Assert.Single(Group(byTitle, nameof(Law))!.Hit).TargetId);
        Assert.Equal("l2", Assert.Single(Group(byBook, nameof(Law))!.Hit).TargetId);
    }

    [Fact]
    public async Task SearchAsync_Jobs_RestrictedHiddenFromNonMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(new Job { Id = "open", Title = "Open Job", CaseNumber = "FIB-A-2026-0001", IsRestricted = false, CreatedById = "someone" });
            db.Jobs.Add(new Job { Id = "restr", Title = "Restricted Job", CaseNumber = "FIB-A-2026-0002", IsRestricted = true, CreatedById = "someone" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forPlain = await svc.SearchAsync(Text("Job"), Plain());
        var forLead = await svc.SearchAsync(Text("Job"), Leadership());

        Assert.Equal(new[] { "open" }, Group(forPlain, nameof(Job))!.Hit.Select(h => h.TargetId).ToArray());
        Assert.Equal(2, Group(forLead, nameof(Job))!.Hit.Count);
    }

    [Fact]
    public async Task SearchAsync_Taskforces_OnlyVisibleToMembers()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "tf1", Name = "Alpha Taskforce", CaseNumber = "FIB-TF-2026-0001" });
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf1", AgentId = "agent-1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forMember = await svc.SearchAsync(Text("Alpha"), Plain("agent-1"));
        var forOutsider = await svc.SearchAsync(Text("Alpha"), Plain("other"));

        Assert.Equal("tf1", Assert.Single(Group(forMember, nameof(Taskforce))!.Hit).TargetId);
        Assert.Null(Group(forOutsider, nameof(Taskforce)));
    }

    [Fact]
    public async Task SearchAsync_PersonGroups_MatchesByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(new PersonGroup { Id = "g1", Name = "Kartell Nord", CaseNumber = "FIB-G-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Kartell"), Leadership());

        Assert.Equal("g1", Assert.Single(Group(groups, nameof(PersonGroup))!.Hit).TargetId);
    }

    [Fact]
    public async Task SearchAsync_Parties_MatchesByName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(new Party { Id = "pt1", Name = "Buergerpartei", CaseNumber = "FIB-PT-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Buergerpartei"), Leadership());

        Assert.Equal("pt1", Assert.Single(Group(groups, nameof(Party))!.Hit).TargetId);
    }

    [Fact]
    public async Task SearchAsync_Operations_MatchesByTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(new Operation { Id = "op1", Title = "Operation Donnerschlag", CaseNumber = "FIB-OP-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Donnerschlag"), Leadership());

        Assert.Equal("op1", Assert.Single(Group(groups, nameof(Operation))!.Hit).TargetId);
    }

    [Fact]
    public async Task SearchAsync_AgentActivities_MatchesByTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(new AgentActivity
            {
                Id = "act1",
                Title = "Streife Innenstadt",
                ContentHtml = "<p>Bericht</p>",
                ActivityDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // activities carry no classification: visible even to a plain viewer.
        var groups = await svc.SearchAsync(Text("Streife"), Plain());

        Assert.Equal("act1", Assert.Single(Group(groups, nameof(AgentActivity))!.Hit).TargetId);
    }

    // ---- SearchAsync: tags & category scoping -------------------------------

    [Fact]
    public async Task SearchAsync_TagFilter_ReturnsOnlyTaggedRecords()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tags.Add(new Tag { Id = "t1", Name = "Waffenhandel" });
            db.People.Add(Seed.Person("p1", "Tagged Person"));
            db.People.Add(Seed.Person("p2", "Untagged Person"));
            db.TagMappings.Add(new TagMapping { TagId = "t1", EntityType = nameof(Person), EntityId = "p1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var criteria = new SearchCriteria { TagIds = { "t1" } };
        var groups = await svc.SearchAsync(criteria, Leadership());

        Assert.Equal("p1", Assert.Single(Group(groups, nameof(Person))!.Hit).TargetId);
    }

    [Fact]
    public async Task SearchAsync_CategoryFilter_LimitsToRequestedCategory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Target Person"));
            db.Factions.Add(Seed.Faction("f1", "Target Faction"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var criteria = new SearchCriteria { Text = "Target", Categories = { nameof(Person) } };
        var groups = await svc.SearchAsync(criteria, Leadership());

        Assert.NotNull(Group(groups, nameof(Person)));
        Assert.Null(Group(groups, nameof(Faction)));
    }

    // ---- SearchAsync: content categories (docs/sources/comments) ------------

    [Fact]
    public async Task SearchAsync_PersonDocContent_ResolvesToPerson()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mustermann"));
            db.PersonDocs.Add(new PersonDoc
            {
                Id = "d1",
                PersonId = "p1",
                Reason = "Drogenschmuggel Aussage",
                Timestamp = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // term only in the doc, not the person's name -> only the doc category matches.
        var groups = await svc.SearchAsync(Text("Drogenschmuggel"), Leadership());

        var docs = Group(groups, nameof(PersonDoc));
        Assert.NotNull(docs);
        Assert.Equal("p1", Assert.Single(docs!.Hit).TargetId);
        Assert.Null(Group(groups, nameof(Person)));
    }

    [Fact]
    public async Task SearchAsync_SourceContent_ResolvesToParentRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mustermann"));
            db.Sources.Add(new Source { Id = "s1", EntityType = nameof(Person), EntityId = "p1", Title = "Ermittlungsbericht" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Ermittlungsbericht"), Leadership());

        var sources = Group(groups, nameof(Source));
        Assert.NotNull(sources);
        var hit = Assert.Single(sources!.Hit);
        Assert.Equal("p1", hit.TargetId);
        Assert.Equal(nameof(Person), hit.TargetType);
    }

    [Fact]
    public async Task SearchAsync_CommentContent_ResolvesToParentRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Comments.Add(new Comment { Id = "c1", EntityType = nameof(Faction), EntityId = "f1", Text = "streng geheim Waffen" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("geheim"), Leadership());

        var comments = Group(groups, nameof(Comment));
        Assert.NotNull(comments);
        var hit = Assert.Single(comments!.Hit);
        Assert.Equal("f1", hit.TargetId);
        Assert.Equal(nameof(Faction), hit.TargetType);
    }

    [Fact]
    public async Task SearchAsync_CommentContent_DropsClassifiedParent_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.IsClassified = true));
            db.Comments.Add(new Comment { Id = "c1", EntityType = nameof(Faction), EntityId = "f1", Text = "streng geheim Waffen" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forPlain = await svc.SearchAsync(Text("geheim"), Plain());
        var forLead = await svc.SearchAsync(Text("geheim"), Leadership());

        // classified parent hidden from a plain viewer, visible to leadership.
        Assert.Null(Group(forPlain, nameof(Comment)));
        Assert.NotNull(Group(forLead, nameof(Comment)));
    }

    // ---- SearchAsync: fuzzy & max mode --------------------------------------

    [Fact]
    public async Task SearchAsync_Fuzzy_MatchesTypo_ButExactModeDoesNot()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var withFuzzy = await svc.SearchAsync(Text("Antom", fuzzy: true), Leadership());
        var withoutFuzzy = await svc.SearchAsync(Text("Antom", fuzzy: false), Leadership());

        Assert.Equal("p1", Assert.Single(Group(withFuzzy, nameof(Person))!.Hit).TargetId);
        Assert.Null(Group(withoutFuzzy, nameof(Person)));
    }

    [Fact]
    public async Task SearchAsync_MaxMode_SearchesSideFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.Estate = "Grovehaus"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var normal = await svc.SearchAsync(Text("Grovehaus", max: false), Leadership());
        var maxMode = await svc.SearchAsync(Text("Grovehaus", max: true), Leadership());

        // estate is a side field: only reached in max mode.
        Assert.Null(Group(normal, nameof(Faction)));
        Assert.Equal("f1", Assert.Single(Group(maxMode, nameof(Faction))!.Hit).TargetId);
    }

    // ---- SearchAsync: partner scope -----------------------------------------

    [Fact]
    public async Task SearchAsync_Partner_ReturnsOnlySharedNonClassifiedRecords()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Target Alpha"));
            db.People.Add(Seed.Person("p2", "Target Beta"));
            db.People.Add(Seed.Person("p3", "Target Gamma", p => p.IsClassified = true));
            db.PartnerShares.Add(Share(nameof(Person), "p1"));            // shared, visible
            db.PartnerShares.Add(Share(nameof(Person), "p3"));            // shared but classified -> hidden
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var groups = await svc.SearchAsync(Text("Target"), Partner());

        Assert.Equal(new[] { "p1" }, Group(groups, nameof(Person))!.Hit.Select(h => h.TargetId).ToArray());
    }

    // ---- QuickSearchAsync ---------------------------------------------------

    [Fact]
    public async Task QuickSearchAsync_EmptyText_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var hits = await svc.QuickSearchAsync("   ", Leadership());

        Assert.Empty(hits);
    }

    [Fact]
    public async Task QuickSearchAsync_MatchesAcrossCategories()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Ballas Boss"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var hits = await svc.QuickSearchAsync("Ballas", Leadership());

        Assert.Contains(hits, h => h.Category == nameof(Person) && h.TargetId == "p1");
        Assert.Contains(hits, h => h.Category == nameof(Faction) && h.TargetId == "f1");
    }

    [Fact]
    public async Task QuickSearchAsync_ExcludesClassified_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("open", "Ziel Open"));
            db.People.Add(Seed.Person("secret", "Ziel Secret", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var forPlain = await svc.QuickSearchAsync("Ziel", Plain());
        var forLead = await svc.QuickSearchAsync("Ziel", Leadership());

        Assert.Equal(new[] { "open" }, forPlain.Select(h => h.TargetId).ToArray());
        Assert.Equal(2, forLead.Count(h => h.Category == nameof(Person)));
    }

    [Fact]
    public async Task QuickSearchAsync_RespectsMaxLimit()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.People.Add(Seed.Person($"p{i}", $"Kandidat {i}", p => p.CaseNumber = $"FIB-P-2026-90{i}"));
            }
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var hits = await svc.QuickSearchAsync("Kandidat", Leadership(), max: 2);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task QuickSearchAsync_Fuzzy_MatchesTypo()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // "Antom" is not a substring of "Anton" but within edit distance.
        var hits = await svc.QuickSearchAsync("Antom", Leadership());

        Assert.Contains(hits, h => h.Category == nameof(Person) && h.TargetId == "p1");
    }

    [Fact]
    public async Task QuickSearchAsync_Partner_ReturnsOnlySharedRecords()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Ziel Alpha"));
            db.People.Add(Seed.Person("p2", "Ziel Beta"));
            db.PartnerShares.Add(Share(nameof(Person), "p1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var hits = await svc.QuickSearchAsync("Ziel", Partner());

        var hit = Assert.Single(hits);
        Assert.Equal("p1", hit.TargetId);
    }
}
