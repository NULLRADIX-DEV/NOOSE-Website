using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Activities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Factions;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for which faction writes refresh which freshness facet.</summary>
public sealed class FactionRecencyStampTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static FactionService FactionSvc(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-F-2026-0001");
        return new FactionService(
            ctx.Factory,
            caseNo,
            Substitute.For<IProfileSuggestionService>(),
            Substitute.For<IPersonService>(),
            Substitute.For<IFactionPhotoStorageService>(),
            Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<IPublicFactionProfileService>());
    }

    private static async Task<Faction> ReloadAsync(SqliteTestContext ctx, string id = "f1")
    {
        using var db = ctx.NewContext();
        return await db.Factions.SingleAsync(f => f.Id == id);
    }

    /// <summary>Seeds one faction whose facets are all unstamped, plus optional stocks.</summary>
    private static void Seeded(SqliteTestContext ctx, Action<AppDbContext>? extra = null)
    {
        using var db = ctx.NewContext();
        db.Factions.Add(Seed.Faction(id: "f1"));
        extra?.Invoke(db);
        db.SaveChanges();
    }

    // ==================== members ====================

    [Fact]
    public async Task MemberAddAsync_StampsMembersOnly()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db => db.People.Add(Seed.Person(id: "p1", name: "Max")));
        var svc = FactionSvc(ctx);

        await svc.MemberAddAsync("f1", new MemberInput { PersonId = "p1" }, Leader());

        var faction = await ReloadAsync(ctx);
        Assert.NotNull(faction.MembersRefreshedAt);
        Assert.Null(faction.StockRefreshedAt);
        Assert.Null(faction.ActivitiesRefreshedAt);
        Assert.Null(faction.DocsRefreshedAt);
    }

    [Fact]
    public async Task MemberChangeAsync_StampsMembers()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db =>
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1" });
        });
        var svc = FactionSvc(ctx);

        await svc.MemberChangeAsync("m1", "Boss", isLead: true, Leader());

        Assert.NotNull((await ReloadAsync(ctx)).MembersRefreshedAt);
    }

    [Fact]
    public async Task MemberRemoveAsync_StampsMembers()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db =>
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1" });
        });
        var svc = FactionSvc(ctx);

        await svc.MemberRemoveAsync("m1", Leader());

        Assert.NotNull((await ReloadAsync(ctx)).MembersRefreshedAt);
    }

    [Fact]
    public async Task MembersBulkApplyAsync_StampsMembers_WhenSomethingChanged()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db => db.People.Add(Seed.Person(id: "p1", name: "Max")));
        var svc = FactionSvc(ctx);

        await svc.MembersBulkApplyAsync("f1", new[] { new MemberInput { PersonId = "p1" } },
            Array.Empty<string>(), Leader());

        Assert.NotNull((await ReloadAsync(ctx)).MembersRefreshedAt);
    }

    [Fact]
    public async Task MembersBulkApplyAsync_StampsNothing_WhenNoMembershipChanged()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx);
        var svc = FactionSvc(ctx);

        await svc.MembersBulkApplyAsync("f1", Array.Empty<MemberInput>(), Array.Empty<string>(), Leader());

        Assert.Null((await ReloadAsync(ctx)).MembersRefreshedAt);
    }

    [Fact]
    public async Task MemberAddAsync_StampsOldFaction_WhenMemberLeaves()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db =>
        {
            db.Factions.Add(Seed.Faction(id: "f2"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
        });
        var svc = FactionSvc(ctx);

        await svc.MemberAddAsync("f1",
            new MemberInput { PersonId = "p1", RemoveFromOtherFactions = true }, Leader());

        Assert.NotNull((await ReloadAsync(ctx, "f1")).MembersRefreshedAt);
        Assert.NotNull((await ReloadAsync(ctx, "f2")).MembersRefreshedAt);
    }

    [Fact]
    public async Task MembersBulkApplyAsync_StampsOldFaction_WhenMemberLeaves()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db =>
        {
            db.Factions.Add(Seed.Faction(id: "f2"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.FactionMembers.Add(new FactionMember { FactionId = "f2", PersonId = "p1" });
        });
        var svc = FactionSvc(ctx);

        await svc.MembersBulkApplyAsync("f1",
            new[] { new MemberInput { PersonId = "p1", RemoveFromOtherFactions = true } },
            Array.Empty<string>(), Leader());

        Assert.NotNull((await ReloadAsync(ctx, "f1")).MembersRefreshedAt);
        Assert.NotNull((await ReloadAsync(ctx, "f2")).MembersRefreshedAt);
    }

    // ==================== stocks vs. master data ====================

    [Fact]
    public async Task RefreshAsync_StampsStock_WhenStockChanged()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx);
        var svc = FactionSvc(ctx);
        var input = new FactionInput
        {
            Name = "Ballas",
            WeaponStock = { new StockInput { Designation = "AK-47", Quantity = "3" } },
        };

        await svc.RefreshAsync("f1", input, Leader());

        var faction = await ReloadAsync(ctx);
        Assert.NotNull(faction.StockRefreshedAt);
        Assert.Null(faction.MembersRefreshedAt);
        Assert.Null(faction.ActivitiesRefreshedAt);
        Assert.Null(faction.DocsRefreshedAt);
    }

    [Fact]
    public async Task RefreshAsync_StampsNothing_WhenOnlyMasterDataChanged()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db =>
        {
            db.FactionWeaponStocks.Add(new FactionWeaponStock { Id = "w1", FactionId = "f1", Designation = "AK-47", Quantity = "3" });
            db.FactionRanks.Add(new FactionRank { Id = "r1", FactionId = "f1", Designation = "Soldat", Order = 0 });
        });
        var svc = FactionSvc(ctx);
        var input = new FactionInput
        {
            Name = "Neuer Name",
            Description = "Beschreibung geändert",
            Ranks = { new RankInput { Id = "r1", Designation = "Boss" } },
            // stocks re-submitted unchanged
            WeaponStock = { new StockInput { Designation = "AK-47", Quantity = "3" } },
        };

        await svc.RefreshAsync("f1", input, Leader());

        var faction = await ReloadAsync(ctx);
        Assert.Equal("Neuer Name", faction.Name);
        Assert.Null(faction.StockRefreshedAt);
        Assert.Null(faction.MembersRefreshedAt);
    }

    [Fact]
    public async Task RefreshAsync_StampsStock_WhenStockRemoved()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db => db.FactionInventories.Add(
            new FactionInventory { Id = "l1", FactionId = "f1", Designation = "Bargeld", Quantity = "5000" }));
        var svc = FactionSvc(ctx);

        await svc.RefreshAsync("f1", new FactionInput { Name = "Ballas" }, Leader());

        Assert.NotNull((await ReloadAsync(ctx)).StockRefreshedAt);
    }

    // ==================== activities ====================

    [Fact]
    public async Task ActivityDeleteAsync_StampsActivities_OnFormerlyLinkedFaction()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db =>
        {
            db.AgentActivities.Add(new AgentActivity
            {
                Id = "a1",
                Title = "Observation",
                ActivityDate = DateTime.UtcNow,
                CreatedById = "lead",
                Links = { new AgentActivityLink { TargetType = nameof(Faction), TargetId = "f1" } },
            });
        });
        var svc = new AgentActivityService(ctx.Factory, Substitute.For<IThreatScoreService>());

        await svc.DeleteAsync("a1", Leader());

        Assert.NotNull((await ReloadAsync(ctx)).ActivitiesRefreshedAt);
    }

    // ==================== docs ====================

    [Fact]
    public async Task DocCreateAsync_StampsDocs_OnLinkedFaction()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db => db.People.Add(Seed.Person(id: "p1", name: "Max")));
        var svc = new PersonDocService(ctx.Factory, Substitute.For<IPersonService>(),
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>());

        await svc.CreateAsync("p1", new PersonDocInput
        {
            Timestamp = DateTime.UtcNow,
            OrgType = nameof(Faction),
            OrgId = "f1",
        }, Leader());

        var faction = await ReloadAsync(ctx);
        Assert.NotNull(faction.DocsRefreshedAt);
        Assert.Null(faction.MembersRefreshedAt);
        Assert.Null(faction.StockRefreshedAt);
        Assert.Null(faction.ActivitiesRefreshedAt);
    }

    [Fact]
    public async Task DocCreateAsync_StampsNothing_WhenNoFactionLinked()
    {
        using var ctx = new SqliteTestContext();
        Seeded(ctx, db => db.People.Add(Seed.Person(id: "p1", name: "Max")));
        var svc = new PersonDocService(ctx.Factory, Substitute.For<IPersonService>(),
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>());

        await svc.CreateAsync("p1", new PersonDocInput
        {
            Timestamp = DateTime.UtcNow,
            // free-text faction only, no record link
            Faction = "Irgendeine Gang",
        }, Leader());

        Assert.Null((await ReloadAsync(ctx)).DocsRefreshedAt);
    }

    [Fact]
    public async Task DocRefreshAsync_StampsDocs_OnOldAndNewFaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.PersonDocs.Add(new Data.Entities.People.PersonDoc
            {
                Id = "d1",
                PersonId = "p1",
                Timestamp = DateTime.UtcNow,
                OrgType = nameof(Faction),
                OrgId = "f1",
            });
            db.SaveChanges();
        }
        var svc = new PersonDocService(ctx.Factory, Substitute.For<IPersonService>(),
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>());

        await svc.RefreshAsync("d1", new PersonDocInput
        {
            Timestamp = DateTime.UtcNow,
            OrgType = nameof(Faction),
            OrgId = "f2",
        }, Leader());

        Assert.NotNull((await ReloadAsync(ctx, "f1")).DocsRefreshedAt);
        Assert.NotNull((await ReloadAsync(ctx, "f2")).DocsRefreshedAt);
    }
}
