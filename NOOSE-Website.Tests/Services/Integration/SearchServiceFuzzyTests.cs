using System.Security.Claims;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Search;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for the phonetic/stem side-index path in <see cref="SearchService"/>.
/// Uses „Mayr" vs query „Meier" — edit distance 3 (Levenshtein misses it), same Cologne code (side-index catches it).</summary>
public sealed class SearchServiceFuzzyTests
{
    private static SearchService Svc(SqliteTestContext ctx) => SearchTestHost.NewService(ctx);
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.Trainee).Build();
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("l").WithRank(Rank.Director).Build();

    private static SearchCriteria Fuzzy(string text) => new() { Text = text, Fuzzy = true };

    [Fact]
    public async Task Fuzzy_PhoneticSideIndex_FindsSoundalikeBeyondLevenshtein()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Mayr"));
            db.SearchPhoneticKeys.Add(new SearchPhoneticKey
            {
                EntityType = nameof(Person), EntityId = "p1", SourceId = "p1", Key = ColognePhonetic.Encode("Mayr"),
            });
            db.SaveChanges();
        }

        var groups = await Svc(ctx).SearchAsync(Fuzzy("Meier"), Junior());

        var people = groups.Groups.FirstOrDefault(g => g.Category == nameof(Person));
        Assert.NotNull(people);
        Assert.Contains(people!.Hit, h => h.TargetId == "p1");
    }

    [Fact]
    public async Task Fuzzy_PhoneticSideIndex_HidesClassifiedFromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Mayr", p => p.IsClassified = true));
            db.SearchPhoneticKeys.Add(new SearchPhoneticKey
            {
                EntityType = nameof(Person), EntityId = "p1", SourceId = "p1", Key = ColognePhonetic.Encode("Mayr"),
            });
            db.SaveChanges();
        }

        var asJunior = await Svc(ctx).SearchAsync(Fuzzy("Meier"), Junior());
        Assert.DoesNotContain(asJunior.Groups.SelectMany(g => g.Hit), h => h.TargetId == "p1");

        var asLeader = await Svc(ctx).SearchAsync(Fuzzy("Meier"), Leader());
        Assert.Contains(asLeader.Groups.SelectMany(g => g.Hit), h => h.TargetId == "p1");
    }

    // ---- the three types added in stage 7 ----

    /// <summary>Writes an index row by hand rather than through the interceptor: SqliteTestContext registers no
    /// interceptors, so a seeded entity never indexes itself.</summary>
    private static SearchPhoneticKey Key(string type, string id, string word)
        => new() { EntityType = type, EntityId = id, SourceId = id, Key = ColognePhonetic.Encode(word) };

    [Fact]
    public async Task Fuzzy_FindsAnAgentByASoundalikeCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Mayr"));
            db.SearchPhoneticKeys.Add(Key(nameof(NOOSE_Website.Data.Entities.Agent), "a1", "Mayr"));
            db.SaveChanges();
        }

        var lead = await Svc(ctx).SearchAsync(Fuzzy("Meier"), Leader());

        Assert.Contains(lead.Groups.SelectMany(g => g.Hit), h => h.TargetId == "a1");
    }

    [Fact]
    public async Task Fuzzy_DoesNotHandAnAgentToSomeoneWhoMayNotSeePersonnelFiles()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Mayr"));
            db.SearchPhoneticKeys.Add(Key(nameof(NOOSE_Website.Data.Entities.Agent), "a1", "Mayr"));
            db.SaveChanges();
        }

        // the index has no gate of its own; the provider's does, and this is the test that proves it applies
        var junior = await Svc(ctx).SearchAsync(Fuzzy("Meier"), Junior());

        Assert.DoesNotContain(junior.Groups.SelectMany(g => g.Hit), h => h.TargetId == "a1");
    }

    [Fact]
    public async Task Fuzzy_FindsALawByASoundalikeTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Laws.Add(new NOOSE_Website.Data.Entities.Common.Law
            {
                Id = "g1", LawBook = "StGB", Paragraph = "§ 242", Title = "Diebstal", Text = "…",
            });
            db.SearchPhoneticKeys.Add(Key(nameof(NOOSE_Website.Data.Entities.Common.Law), "g1", "Diebstal"));
            db.SaveChanges();
        }

        var junior = await Svc(ctx).SearchAsync(Fuzzy("Diebstahl"), Junior());

        Assert.Contains(junior.Groups.SelectMany(g => g.Hit), h => h.TargetId == "g1");
    }

    [Fact]
    public async Task Fuzzy_FindsAnEvidenceItemByASoundalikeName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.EvidenceItems.Add(new NOOSE_Website.Data.Entities.Evidence.EvidenceItem
            {
                Id = "e1", Name = "Schaldämpfer", Category = "Waffenteil",
            });
            db.SearchPhoneticKeys.Add(Key(nameof(NOOSE_Website.Data.Entities.Evidence.EvidenceItem), "e1", "Schaldämpfer"));
            db.SaveChanges();
        }

        var junior = await Svc(ctx).SearchAsync(Fuzzy("Schalldämpfer"), Junior());

        Assert.Contains(junior.Groups.SelectMany(g => g.Hit), h => h.TargetId == "e1");
    }

    [Fact]
    public async Task The_projection_indexes_an_agents_codename_but_never_their_real_name()
    {
        var agent = Seed.Agent("a1", configure: a => { a.Codename = "Falke"; a.RealName = "Johnny Cash"; });

        var row = SearchIndexProjection.For(agent);

        Assert.NotNull(row);
        Assert.Equal(nameof(NOOSE_Website.Data.Entities.Agent), row!.Value.EntityType);
        // the index table carries no visibility gate, so a leadership-only field has no business in it
        Assert.DoesNotContain(row.Value.Stems, s => s.Contains("johnny", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(row.Value.Stems, s => s.Contains("cash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(row.Value.PhoneticKeys, k => k == ColognePhonetic.Encode("Falke"));
    }

    [Fact]
    public void The_projection_leaves_informants_out()
    {
        var informant = new NOOSE_Website.Data.Entities.Informants.Informant
        {
            Id = "i1", CaseNumber = "FIB-VP-2026-0001", RealName = "Mayr", HandlerId = "h1",
        };

        // the only field worth a phonetic pass is the V-person's real name, and this table has no gate
        Assert.Null(SearchIndexProjection.For(informant));
    }
}
