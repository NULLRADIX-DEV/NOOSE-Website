using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="LinkSuggestionService"/> against in-memory SQLite.</summary>
public sealed class LinkSuggestionServiceTests
{
    private static LinkSuggestionService NewService(SqliteTestContext ctx)
        => new(ctx.Factory);

    // Director => leadership => sees classified candidates.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.Director).Build();

    // Trainee: not leadership => classified candidates hidden.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.Trainee).Build();

    // ---- entity-type gate --------------------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_ReturnsEmpty_ForNonPersonEntityType()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Faction", "f1", Leader());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ReturnsEmpty_WhenNoSignalsMatch()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        Assert.Empty(result);
    }

    // ---- signal 1: same phone number ---------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsPersonWithSamePhone_IgnoringFormatting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.PersonPhones.Add(new PersonPhone { PersonId = "p1", Number = "0176 / 555-1234" });
            db.PersonPhones.Add(new PersonPhone { PersonId = "p2", Number = "01765551234" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("Anna Schmidt", s.Designation);
        Assert.Equal("/personen/p2", s.Href);
        Assert.Equal(1, s.Strength);
        Assert.Contains("gleiche Telefonnummer", s.Reason);
    }

    // ---- signal 2: same faction --------------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsFellowFactionMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("gleiche Fraktion: Ballas", s.Reason);
        Assert.Equal(1, s.Strength);
    }

    // ---- signal 3: same person group ---------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsFellowGroupMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.PersonGroups.Add(new PersonGroup { Id = "g1", Name = "Kartell", CaseNumber = "FIB-G-2026-0001" });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p1" });
            db.PersonGroupMembers.Add(new PersonGroupMember { PersonGroupId = "g1", PersonId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("gleiche Gruppe: Kartell", s.Reason);
    }

    // ---- signal 4: shared tag ----------------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsPersonSharingTag()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.Tags.Add(new Tag { Id = "t1", Name = "Waffenhandel" });
            db.TagMappings.Add(new TagMapping { TagId = "t1", EntityType = "Person", EntityId = "p1" });
            db.TagMappings.Add(new TagMapping { TagId = "t1", EntityType = "Person", EntityId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("gemeinsamer Tag: Waffenhandel", s.Reason);
    }

    // ---- signal 5: shared link (common neighbour in the link graph) --------

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsPersonSharingLinkNeighbour()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.Factions.Add(Seed.Faction("fX", "Vagos"));
            // both persons link to the same faction node -> common neighbour.
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "fX" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p2", TargetType = "Faction", TargetId = "fX" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("gemeinsame Verknüpfung", s.Reason);
    }

    // ---- signal 6: same surname / alias ------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsPersonWithSameSurname()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mustermann"));
            db.People.Add(Seed.Person("p2", "Anna Mustermann"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("gleicher Nachname: Mustermann", s.Reason);
    }

    [Fact]
    public async Task GetSuggestionsAsync_SuggestsPersonWithSharedAlias()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.PersonAliases.Add(new PersonAlias { PersonId = "p1", AliasName = "Ghost" });
            db.PersonAliases.Add(new PersonAlias { PersonId = "p2", AliasName = "Ghost" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
        Assert.Equal("gemeinsamer Alias: Ghost", s.Reason);
    }

    // ---- exclusions --------------------------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_ExcludesAlreadyLinkedPerson()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            // would be a candidate via shared faction ...
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p2" });
            // ... but is already directly linked person-to-person.
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ExcludesExistingPersonRelation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p2" });
            db.PersonRelations.Add(new PersonRelation { PersonAId = "p1", PersonBId = "p2", Type = RelationType.Family });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        Assert.Empty(result);
    }

    // ---- classified visibility ---------------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_HidesClassifiedCandidate_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt", p => p.IsClassified = true));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", NonLeader());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSuggestionsAsync_ShowsClassifiedCandidate_ToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt", p => p.IsClassified = true));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p2" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        var s = Assert.Single(result);
        Assert.Equal("p2", s.TargetId);
    }

    // ---- ranking & cap -----------------------------------------------------

    [Fact]
    public async Task GetSuggestionsAsync_RanksByStrength_MostSignalsFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mueller"));
            db.People.Add(Seed.Person("p2", "Anna Schmidt")); // faction + phone => strength 2
            db.People.Add(Seed.Person("p3", "Ben Krause"));    // faction only => strength 1
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p2" });
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p3" });
            db.PersonPhones.Add(new PersonPhone { PersonId = "p1", Number = "111" });
            db.PersonPhones.Add(new PersonPhone { PersonId = "p2", Number = "111" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        Assert.Equal(2, result.Count);
        Assert.Equal("p2", result[0].TargetId);
        Assert.Equal(2, result[0].Strength);
        Assert.Equal("p3", result[1].TargetId);
        Assert.Equal(1, result[1].Strength);
    }

    [Fact]
    public async Task GetSuggestionsAsync_CapsResultsAtTwelve()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Ziel Person"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = "p1" });
            // 13 fellow members, each with a distinct surname (one shared-faction signal each).
            for (var i = 1; i <= 13; i++)
            {
                var id = $"cand{i}";
                db.People.Add(Seed.Person(id, $"Kandidat Nachname{i:00}"));
                db.FactionMembers.Add(new FactionMember { FactionId = "f1", PersonId = id });
            }
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetSuggestionsAsync("Person", "p1", Leader());

        Assert.Equal(12, result.Count);
    }
}
