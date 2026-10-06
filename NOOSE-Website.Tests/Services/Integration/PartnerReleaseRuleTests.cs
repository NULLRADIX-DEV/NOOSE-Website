using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Rule releases: every matching record of a type, including ones created later, without a share row.</summary>
public sealed class PartnerReleaseRuleTests : IDisposable
{
    private const PartnerAgency Parlament = PartnerAgency.Parlament;
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private void Rule(string entityType, PartnerRuleScope scope, bool includesChildren = false, PartnerAgency agency = Parlament)
    {
        using var db = _ctx.NewContext();
        db.PartnerReleaseRules.Add(new PartnerReleaseRule
        {
            Agency = agency, EntityType = entityType, Scope = scope, IncludesChildren = includesChildren,
        });
        db.SaveChanges();
    }

    private void Add(params object[] entities)
    {
        using var db = _ctx.NewContext();
        db.AddRange(entities);
        db.SaveChanges();
    }

    private static FactionMember Member(string factionId, string personId) => new() { FactionId = factionId, PersonId = personId };

    private async Task<bool> VisibleAsync(string type, string id, PartnerAgency agency = Parlament)
    {
        await using var db = _ctx.NewContext();
        return await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, type, id, agency, "partner");
    }

    private async Task<List<string>> VisibleFactionIdsAsync()
    {
        await using var db = _ctx.NewContext();
        return await db.Factions.OnlyPartnerVisible(db, Parlament, "partner").Select(f => f.Id).OrderBy(i => i).ToListAsync();
    }

    private async Task<List<string>> VisiblePersonIdsAsync()
    {
        await using var db = _ctx.NewContext();
        return await db.People.OnlyPartnerVisible(db, Parlament, "partner").Select(p => p.Id).OrderBy(i => i).ToListAsync();
    }

    [Fact]
    public async Task BadFactionRule_ReleasesBadFactionsOnly()
    {
        Rule(nameof(Faction), PartnerRuleScope.BadFactions);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true),
            Seed.Faction("state", configure: f => f.IsStateFaction = true),
            Seed.Faction("neutral"));

        Assert.Equal(new[] { "bad" }, await VisibleFactionIdsAsync());
        Assert.True(await VisibleAsync(nameof(Faction), "bad"));
        Assert.False(await VisibleAsync(nameof(Faction), "neutral"));
    }

    [Fact]
    public async Task BadFactionRule_CoversAFactionCreatedAfterTheRule()
    {
        Rule(nameof(Faction), PartnerRuleScope.BadFactions);
        Add(Seed.Faction("later", configure: f => f.IsBadFaction = true));

        Assert.True(await VisibleAsync(nameof(Faction), "later"));
    }

    [Fact]
    public async Task BadFactionRule_NeverReleasesAClassifiedFaction()
    {
        Rule(nameof(Faction), PartnerRuleScope.BadFactions);
        Add(Seed.Faction("vs", configure: f => { f.IsBadFaction = true; f.IsClassified = true; }));

        Assert.False(await VisibleAsync(nameof(Faction), "vs"));
        Assert.Empty(await VisibleFactionIdsAsync());
    }

    [Fact]
    public async Task MemberRule_ReleasesCurrentMembersOfAnActiveUnclassifiedBadFaction()
    {
        Rule(nameof(Person), PartnerRuleScope.BadFactionMembers);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true),
            Seed.Faction("neutral"),
            Seed.Faction("bad-vs", configure: f => { f.IsBadFaction = true; f.IsClassified = true; }),
            Seed.Faction("bad-archived", configure: f => { f.IsBadFaction = true; f.IsArchived = true; }),
            Seed.Person("in-bad"), Seed.Person("in-neutral"), Seed.Person("in-vs"), Seed.Person("in-archived"),
            Seed.Person("left"), Seed.Person("nobody"));
        Add(Member("bad", "in-bad"), Member("neutral", "in-neutral"), Member("bad-vs", "in-vs"),
            Member("bad-archived", "in-archived"),
            new FactionMember { FactionId = "bad", PersonId = "left", IsDeleted = true });

        Assert.Equal(new[] { "in-bad" }, await VisiblePersonIdsAsync());
        Assert.True(await VisibleAsync(nameof(Person), "in-bad"));
        Assert.False(await VisibleAsync(nameof(Person), "left"));
    }

    [Fact]
    public async Task MemberRule_NeverReleasesAClassifiedPerson()
    {
        Rule(nameof(Person), PartnerRuleScope.BadFactionMembers);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true),
            Seed.Person("vs", configure: p => p.IsClassified = true));
        Add(Member("bad", "vs"));

        Assert.False(await VisibleAsync(nameof(Person), "vs"));
    }

    [Fact]
    public async Task AllRule_ReleasesEveryUnclassifiedRecordOfTheType()
    {
        Rule(nameof(PersonGroup), PartnerRuleScope.All);
        Rule(nameof(Party), PartnerRuleScope.All);
        Rule(nameof(Law), PartnerRuleScope.All);
        Add(new PersonGroup { Id = "g1", Name = "G", CaseNumber = "NOOSE-G-2026-9001" },
            new PersonGroup { Id = "g-vs", Name = "G2", CaseNumber = "NOOSE-G-2026-9002", IsClassified = true },
            new Party { Id = "pa1", Name = "P", CaseNumber = "NOOSE-PA-2026-9001" },
            new Law { Id = "l1", LawBook = "StGB", Paragraph = "§ 1", Title = "Mord" });

        Assert.True(await VisibleAsync(nameof(PersonGroup), "g1"));
        Assert.False(await VisibleAsync(nameof(PersonGroup), "g-vs"));
        Assert.True(await VisibleAsync(nameof(Party), "pa1"));
        Assert.True(await VisibleAsync(nameof(Law), "l1"));
    }

    [Fact]
    public async Task Rule_OfAnotherAgency_ReleasesNothing()
    {
        Rule(nameof(Faction), PartnerRuleScope.BadFactions, agency: PartnerAgency.DoJ);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true));

        Assert.False(await VisibleAsync(nameof(Faction), "bad"));
        Assert.True(await VisibleAsync(nameof(Faction), "bad", PartnerAgency.DoJ));
    }

    [Fact]
    public async Task Rule_WithChildren_CountsAsWholeRecord_WithoutChildrenOnlyAsShell()
    {
        Rule(nameof(Faction), PartnerRuleScope.BadFactions, includesChildren: true);
        Rule(nameof(Person), PartnerRuleScope.All, includesChildren: false);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true), Seed.Person("p1"));

        await using var db = _ctx.NewContext();
        Assert.True(await PartnerVisibility.ParentIncludesChildrenAsync(db, nameof(Faction), "bad", Parlament, "partner"));
        Assert.False(await PartnerVisibility.ParentIncludesChildrenAsync(db, nameof(Person), "p1", Parlament, "partner"));
    }

    [Fact]
    public async Task ReleasedParentIds_IncludesRuleMatches()
    {
        Rule(nameof(Person), PartnerRuleScope.BadFactionMembers);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true), Seed.Person("in"), Seed.Person("out"));
        Add(Member("bad", "in"));

        await using var db = _ctx.NewContext();
        var released = await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(Person), new[] { "in", "out" }, Parlament, "partner");

        Assert.Equal(new[] { "in" }, released);
    }

    [Fact]
    public async Task PointCheck_MatchesListPredicate()
    {
        Rule(nameof(Person), PartnerRuleScope.BadFactionMembers);
        Add(Seed.Faction("bad", configure: f => f.IsBadFaction = true),
            Seed.Person("a"), Seed.Person("b"), Seed.Person("c", configure: p => p.IsClassified = true));
        Add(Member("bad", "a"), Member("bad", "c"));
        using (var db = _ctx.NewContext())
        {
            db.PartnerShares.Add(new PartnerShare { EntityType = nameof(Person), EntityId = "b", Agency = Parlament });
            db.SaveChanges();
        }

        var listed = await VisiblePersonIdsAsync();
        foreach (var id in new[] { "a", "b", "c" })
        {
            Assert.Equal(listed.Contains(id), await VisibleAsync(nameof(Person), id));
        }
        Assert.Equal(new[] { "a", "b" }, listed);
    }
}
