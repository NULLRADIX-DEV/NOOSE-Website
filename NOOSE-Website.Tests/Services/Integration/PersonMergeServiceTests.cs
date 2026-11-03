using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PersonMergeService"/> against in-memory SQLite.</summary>
public sealed class PersonMergeServiceTests
{
    private const string PersonType = nameof(Person);

    private static PersonMergeService NewService(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IPublicWantedService>());

    // Rank >= SpecialAgentInCharge AND not a read-only team lead -> passes both guards.
    private static ClaimsPrincipal Boss()
        => ClaimsPrincipalBuilder.Agent("boss").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static Person P(string id, string caseNumber, Action<Person>? cfg = null)
        => Seed.Person(id, name: id, configure: p =>
        {
            p.CaseNumber = caseNumber;
            cfg?.Invoke(p);
        });

    // ---- guard tests ----------------------------------------------------

    [Fact]
    public async Task MergeAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MergeAsync("src", "dst", actor));
    }

    [Fact]
    public async Task MergeAsync_Throws_ForReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        // rank passes leadership, but IsTeamLead && !IsAdmin -> OnlyReader -> write-access guard vetoes.
        var actor = ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.MergeAsync("src", "dst", actor));
    }

    [Fact]
    public async Task MergeAsync_Throws_WhenSourceEqualsTarget()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MergeAsync("same", "same", Boss()));
    }

    [Fact]
    public async Task MergeAsync_Throws_WhenSourceIdBlank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MergeAsync("   ", "dst", Boss()));
    }

    [Fact]
    public async Task MergeAsync_Throws_WhenSourceNotFound()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MergeAsync("missing", "dst", Boss()));
    }

    [Fact]
    public async Task MergeAsync_Throws_WhenTargetNotFound()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MergeAsync("src", "missing", Boss()));
    }

    // ---- bulk children + trash source -----------------------------------

    [Fact]
    public async Task MergeAsync_ReassignsBulkChildren_AndTrashesSource()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.PersonDocs.Add(new PersonDoc { PersonId = "src", Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.Observations.Add(new Observation { PersonId = "src", Start = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.PersonPhotos.Add(new PersonPhoto { PersonId = "src", FileNameSaved = "f.jpg", OriginalName = "o.jpg", ContentType = "image/jpeg" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.Equal("dst", read.PersonDocs.Single().PersonId);
        Assert.Equal("dst", read.Observations.Single().PersonId);
        Assert.Equal("dst", read.PersonPhotos.Single().PersonId);
        // source hard-deleted here (soft-delete interceptor absent in tests)
        Assert.False(read.People.Any(p => p.Id == "src"));
        Assert.True(read.People.Any(p => p.Id == "dst"));
    }

    [Fact]
    public async Task MergeAsync_LeavesMergeTrailComment_OnTarget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001", p => p.Name = "Quelle Q"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        var comment = read.Comments.Single();
        Assert.Equal(PersonType, comment.EntityType);
        Assert.Equal("dst", comment.EntityId);
        Assert.Equal("Falcon", comment.AuthorName);
        Assert.Contains("Quelle Q", comment.Text);
    }

    // ---- profile children with dedup ------------------------------------

    [Fact]
    public async Task MergeAsync_DedupsAliases_AndKeepsSourceNameAsAlias()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001", p =>
            {
                p.Name = "Source Name";
                p.Aliases = new() { new PersonAlias { AliasName = "shared" }, new PersonAlias { AliasName = "Unique" } };
            }));
            db.People.Add(P("dst", "FIB-P-2026-0002", p =>
            {
                p.Name = "Target Name";
                p.Aliases = new() { new PersonAlias { AliasName = "Shared" } };
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        var names = read.PersonAliases.Where(a => a.PersonId == "dst").Select(a => a.AliasName).ToList();
        Assert.Equal(3, names.Count);
        Assert.Contains("Unique", names);              // unique alias reassigned
        Assert.Contains("Source Name", names);         // source name kept findable
        Assert.Single(names, n => n == "Shared");      // case-insensitive duplicate collapsed
        // nothing left pointing at the trashed source
        Assert.False(read.PersonAliases.Any(a => a.PersonId == "src"));
    }

    [Fact]
    public async Task MergeAsync_DedupsPhonesVehiclesLocationsWeapons()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001", p =>
            {
                p.PhoneNumbers = new() { new PersonPhone { Number = "111" }, new PersonPhone { Number = "222" } };
                p.Vehicles = new() { new PersonVehicle { Designation = "Sultan", LicensePlate = "ABC" }, new PersonVehicle { Designation = "Kuruma", LicensePlate = "XYZ" } };
                p.Locations = new() { new PersonLocation { Text = "Downtown" }, new PersonLocation { Text = "Sandy" } };
                p.Weapons = new() { new PersonWeapon { Text = "Pistol" }, new PersonWeapon { Text = "Rifle" } };
            }));
            db.People.Add(P("dst", "FIB-P-2026-0002", p =>
            {
                p.PhoneNumbers = new() { new PersonPhone { Number = "111" } };
                p.Vehicles = new() { new PersonVehicle { Designation = "Sultan", LicensePlate = "ABC" } };
                p.Locations = new() { new PersonLocation { Text = "Downtown" } };
                p.Weapons = new() { new PersonWeapon { Text = "Pistol" } };
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.Equal(2, read.PersonPhones.Count(x => x.PersonId == "dst"));
        Assert.Contains(read.PersonPhones.Where(x => x.PersonId == "dst"), x => x.Number == "222");
        Assert.Equal(2, read.PersonVehicles.Count(x => x.PersonId == "dst"));
        Assert.Contains(read.PersonVehicles.Where(x => x.PersonId == "dst"), x => x.Designation == "Kuruma");
        Assert.Equal(2, read.PersonLocations.Count(x => x.PersonId == "dst"));
        Assert.Contains(read.PersonLocations.Where(x => x.PersonId == "dst"), x => x.Text == "Sandy");
        Assert.Equal(2, read.PersonWeapons.Count(x => x.PersonId == "dst"));
        Assert.Contains(read.PersonWeapons.Where(x => x.PersonId == "dst"), x => x.Text == "Rifle");
        // no orphaned profile children remain on the source
        Assert.False(read.PersonPhones.Any(x => x.PersonId == "src"));
    }

    // ---- person-to-person relations -------------------------------------

    [Fact]
    public async Task MergeAsync_ReassignsRelations_AndDropsSelfReferences()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.People.Add(P("third", "FIB-P-2026-0003"));
            // src<->dst collapses to a self-reference once src becomes dst -> dropped
            db.PersonRelations.Add(new PersonRelation { Id = "r-self", PersonAId = "src", PersonBId = "dst" });
            // src<->third is reassigned to dst<->third
            db.PersonRelations.Add(new PersonRelation { Id = "r-keep", PersonAId = "src", PersonBId = "third" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.False(read.PersonRelations.Any(r => r.Id == "r-self"));
        var kept = read.PersonRelations.Single(r => r.Id == "r-keep");
        Assert.Equal("dst", kept.PersonAId);
        Assert.Equal("third", kept.PersonBId);
    }

    // ---- memberships ----------------------------------------------------

    [Fact]
    public async Task MergeAsync_FactionMemberships_ReassignUnique_DropDuplicate()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.Factions.Add(Seed.Faction("f1", configure: f => f.CaseNumber = "FIB-F-2026-0001"));
            db.Factions.Add(Seed.Faction("f2", configure: f => f.CaseNumber = "FIB-F-2026-0002"));
            // both belong to f1 (duplicate) ; only src belongs to f2 (unique)
            db.FactionMembers.Add(new FactionMember { Id = "m-dup", FactionId = "f1", PersonId = "src" });
            db.FactionMembers.Add(new FactionMember { Id = "m-uniq", FactionId = "f2", PersonId = "src" });
            db.FactionMembers.Add(new FactionMember { Id = "m-tgt", FactionId = "f1", PersonId = "dst" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.False(read.FactionMembers.Any(m => m.Id == "m-dup"));           // duplicate dropped
        Assert.Equal("dst", read.FactionMembers.Single(m => m.Id == "m-uniq").PersonId); // unique reassigned
        Assert.Equal("dst", read.FactionMembers.Single(m => m.Id == "m-tgt").PersonId);  // target untouched
        Assert.False(read.FactionMembers.Any(m => m.PersonId == "src"));
    }

    [Fact]
    public async Task MergeAsync_GroupAndPartyMemberships_Reassigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.PersonGroups.Add(new PersonGroup { Id = "g1", Name = "Gruppe", CaseNumber = "FIB-G-2026-0001" });
            db.Parties.Add(new Party { Id = "pt1", Name = "Partei", CaseNumber = "FIB-PT-2026-0001" });
            db.PersonGroupMembers.Add(new PersonGroupMember { Id = "gm", PersonGroupId = "g1", PersonId = "src" });
            db.PartyMembers.Add(new PartyMember { Id = "pm", PartyId = "pt1", PersonId = "src" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.Equal("dst", read.PersonGroupMembers.Single(m => m.Id == "gm").PersonId);
        Assert.Equal("dst", read.PartyMembers.Single(m => m.Id == "pm").PersonId);
    }

    // ---- polymorphic references -----------------------------------------

    [Fact]
    public async Task MergeAsync_ReassignsPolymorphicReferences_ToTarget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.Comments.Add(new Comment { Id = "c1", EntityType = PersonType, EntityId = "src", Text = "note" });
            db.Sources.Add(new Source { Id = "s-ent", EntityType = PersonType, EntityId = "src", Title = "quelle" });
            db.Sources.Add(new Source { Id = "s-tgt", EntityType = "Case", EntityId = "case-1", TargetType = PersonType, TargetId = "src", Title = "ref" });
            db.Followups.Add(new Followup { Id = "w1", EntityType = PersonType, EntityId = "src", DueAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.ClassificationHistory.Add(new ClassificationHistory { Id = "ch1", EntityType = PersonType, EntityId = "src", Timestamp = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.Equal("dst", read.Comments.Single(c => c.Id == "c1").EntityId);
        Assert.Equal("dst", read.Sources.Single(s => s.Id == "s-ent").EntityId);
        var refSource = read.Sources.Single(s => s.Id == "s-tgt");
        Assert.Equal("dst", refSource.TargetId);
        Assert.Equal("case-1", refSource.EntityId); // owning side untouched
        Assert.Equal("dst", read.Followups.Single(w => w.Id == "w1").EntityId);
        Assert.Equal("dst", read.ClassificationHistory.Single(h => h.Id == "ch1").EntityId);
    }

    [Fact]
    public async Task MergeAsync_Tags_DedupByUniqueIndex()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.Tags.Add(new Tag { Id = "t1", Name = "Alpha" });
            db.Tags.Add(new Tag { Id = "t2", Name = "Beta" });
            db.TagMappings.Add(new TagMapping { Id = "map-tgt", TagId = "t1", EntityType = PersonType, EntityId = "dst" });
            db.TagMappings.Add(new TagMapping { Id = "map-dup", TagId = "t1", EntityType = PersonType, EntityId = "src" });
            db.TagMappings.Add(new TagMapping { Id = "map-uniq", TagId = "t2", EntityType = PersonType, EntityId = "src" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.False(read.TagMappings.Any(m => m.Id == "map-dup"));                     // would violate the unique index -> dropped
        Assert.Equal("dst", read.TagMappings.Single(m => m.Id == "map-uniq").EntityId); // new tag reassigned
        var tagIds = read.TagMappings.Where(m => m.EntityId == "dst").Select(m => m.TagId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "t1", "t2" }, tagIds);
    }

    [Fact]
    public async Task MergeAsync_CustomFieldValues_TargetValueWins()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.CustomFieldValues.Add(new CustomFieldValue { Id = "cf-tgt", CustomFieldDefinitionId = "d1", EntityType = PersonType, EntityId = "dst", Value = "keep" });
            db.CustomFieldValues.Add(new CustomFieldValue { Id = "cf-dup", CustomFieldDefinitionId = "d1", EntityType = PersonType, EntityId = "src", Value = "drop" });
            db.CustomFieldValues.Add(new CustomFieldValue { Id = "cf-uniq", CustomFieldDefinitionId = "d2", EntityType = PersonType, EntityId = "src", Value = "moved" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.False(read.CustomFieldValues.Any(v => v.Id == "cf-dup"));                     // target value wins
        Assert.Equal("keep", read.CustomFieldValues.Single(v => v.Id == "cf-tgt").Value);
        Assert.Equal("dst", read.CustomFieldValues.Single(v => v.Id == "cf-uniq").EntityId); // non-conflicting reassigned
    }

    [Fact]
    public async Task MergeAsync_Watchlist_DedupPerAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("w1"));
            db.Users.Add(Seed.Agent("w2", configure: a => a.DiscordId = "discord-w2"));
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            db.Watchlists.Add(new WatchlistEntry { Id = "wl-tgt", AgentId = "w1", EntityType = PersonType, EntityId = "dst", CreatedAt = new DateTime(2026, 1, 1) });
            db.Watchlists.Add(new WatchlistEntry { Id = "wl-dup", AgentId = "w1", EntityType = PersonType, EntityId = "src", CreatedAt = new DateTime(2026, 1, 2) });
            db.Watchlists.Add(new WatchlistEntry { Id = "wl-uniq", AgentId = "w2", EntityType = PersonType, EntityId = "src", CreatedAt = new DateTime(2026, 1, 3) });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.False(read.Watchlists.IgnoreQueryFilters().Any(w => w.Id == "wl-dup"));                  // w1 already follows target
        Assert.Equal("dst", read.Watchlists.IgnoreQueryFilters().Single(w => w.Id == "wl-uniq").EntityId); // w2 reassigned
    }

    [Fact]
    public async Task MergeAsync_Links_ReassignBothSides_AndDropSelfLinks()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002"));
            // src -> dst collapses into a self-link once src becomes dst -> dropped
            db.Links.Add(new Link { Id = "l-self", SourceType = PersonType, SourceId = "src", TargetType = PersonType, TargetId = "dst" });
            // src -> case is reassigned on the source side
            db.Links.Add(new Link { Id = "l-keep", SourceType = PersonType, SourceId = "src", TargetType = "Case", TargetId = "case-1" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.False(read.Links.Any(l => l.Id == "l-self"));
        var kept = read.Links.Single(l => l.Id == "l-keep");
        Assert.Equal("dst", kept.SourceId);
        Assert.Equal("case-1", kept.TargetId);
    }

    [Fact]
    public async Task MergeAsync_Requests_RetargetedWithRefreshedDesignation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001"));
            db.People.Add(P("dst", "FIB-P-2026-0002", p => p.Name = "Ziel Person"));
            db.Requests.Add(new Request { Id = "req1", TargetType = PersonType, TargetId = "src", TargetDesignation = "alt" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        var req = read.Requests.Single(r => r.Id == "req1");
        Assert.Equal("dst", req.TargetId);
        Assert.Equal("Ziel Person (FIB-P-2026-0002)", req.TargetDesignation);
    }

    // ---- profile fill + classification carry-over -----------------------

    [Fact]
    public async Task MergeAsync_FillsMissingDescription_AndCarriesClassifiedFlag()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001", p =>
            {
                p.Description = "Wichtige Notiz";
                p.IsClassified = true;
            }));
            db.People.Add(P("dst", "FIB-P-2026-0002", p =>
            {
                p.Description = null;      // empty -> gets filled from source
                p.IsClassified = false;    // OR'd with source -> true
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        var target = read.People.Single(p => p.Id == "dst");
        Assert.Equal("Wichtige Notiz", target.Description);
        Assert.True(target.IsClassified);
    }

    [Fact]
    public async Task MergeAsync_KeepsExistingTargetDescription()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(P("src", "FIB-P-2026-0001", p => p.Description = "Quelle Text"));
            db.People.Add(P("dst", "FIB-P-2026-0002", p => p.Description = "Ziel Text"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.MergeAsync("src", "dst", Boss());

        using var read = ctx.NewContext();
        Assert.Equal("Ziel Text", read.People.Single(p => p.Id == "dst").Description);
    }
}
