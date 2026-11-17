using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Evidence;
using NOOSE_Website.Models.Food;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FoodCatalogService"/>: positions tied to evidence-room items.</summary>
public sealed class FoodCatalogServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("s").WithRank(Rank.SpecialAgentInCharge).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private sealed record Harness(FoodCatalogService Svc, EvidenceService Evidence);

    private static Harness Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var seq = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ASS", Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-ASS-2026-{++seq:0000}");
        var evidence = new EvidenceService(ctx.Factory, caseNo, Substitute.For<IEvidenceImageStorageService>(),
            new ProfileSuggestionService(ctx.Factory));
        return new Harness(new FoodCatalogService(ctx.Factory, evidence), evidence);
    }

    private static async Task<string> DepositAsync(EvidenceService evidence, string name, int quantity)
    {
        await evidence.CreateEntryAsync(new EvidenceEntryInput
        {
            Type = EvidenceEntryType.Deposit,
            OwnerType = EvidenceService.NooseOwner,
            HandlerAgentId = "lead",
            Lines = { new EvidenceLineInput { ItemName = name, Quantity = quantity } },
        }, Leader());
        return (await evidence.GetItemByNameAsync(name))!.Id;
    }

    private static FoodItemInput Input(string evidenceItemId, FoodKind kind = FoodKind.Food, bool active = true,
        int max = 3, int sorting = 0)
        => new() { EvidenceItemId = evidenceItemId, Kind = kind, IsActive = active, MaxQuantity = max, Sorting = sorting, Description = "  lecker  " };

    [Fact]
    public async Task Create_StoresThePosition()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 4);

        var created = await h.Svc.CreateAsync(Input(burger, max: 4), Leader());

        var loaded = await h.Svc.GetAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(burger, loaded.EvidenceItemId);
        Assert.Equal("Burger", loaded.EvidenceItem?.Name);
        Assert.Equal(4, loaded.MaxQuantity);
        Assert.Equal("lecker", loaded.Description);
    }

    [Fact]
    public async Task Create_RequiresAnExistingEvidenceItem()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Input("gibt-es-nicht"), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Input(""), Leader()));
    }

    [Fact]
    public async Task Create_RejectsAMaximumBelowOne()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Input(burger, max: 0), Leader()));
    }

    [Fact]
    public async Task Create_AllowsOneLivePositionPerEvidenceItem()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 1);
        await h.Svc.CreateAsync(Input(burger), Leader());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Input(burger, FoodKind.Drink), Leader()));
        Assert.Contains("Burger", ex.Message);
    }

    [Fact]
    public async Task Create_ASoftDeletedPosition_DoesNotBlockItsItem()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 1);
        var first = await h.Svc.CreateAsync(Input(burger), Leader());
        // the soft-delete interceptor is not wired in tests
        await using (var db = ctx.NewContext())
        {
            var row = await db.FoodItems.FirstAsync(i => i.Id == first.Id);
            row.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        var second = await h.Svc.CreateAsync(Input(burger), Leader());

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Update_MayKeepItsOwnItem_ButNotTakeAnother()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 1);
        var cola = await DepositAsync(h.Evidence, "eCola", 1);
        var a = await h.Svc.CreateAsync(Input(burger), Leader());
        await h.Svc.CreateAsync(Input(cola, FoodKind.Drink), Leader());

        await h.Svc.UpdateAsync(a.Id, Input(burger, max: 5), Leader());
        Assert.Equal(5, (await h.Svc.GetAsync(a.Id))!.MaxQuantity);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.UpdateAsync(a.Id, Input(cola), Leader()));
    }

    [Fact]
    public async Task Writes_RequireLeadershipWithWriteAccess()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 1);
        var created = await h.Svc.CreateAsync(Input(burger), Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.CreateAsync(Input(burger), Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.CreateAsync(Input(burger), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.UpdateAsync(created.Id, Input(burger), Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.DeleteAsync(created.Id, Senior()));
    }

    [Fact]
    public async Task GetActive_ExcludesInactive_AndFillsOnHand()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 4);
        var cola = await DepositAsync(h.Evidence, "eCola", 7);
        var water = await DepositAsync(h.Evidence, "Wasser", 2);
        await h.Svc.CreateAsync(Input(burger, sorting: 1), Leader());
        await h.Svc.CreateAsync(Input(cola, FoodKind.Drink, sorting: 0), Leader());
        await h.Svc.CreateAsync(Input(water, FoodKind.Drink, active: false), Leader());

        var active = await h.Svc.GetActiveAsync();
        var all = await h.Svc.GetAllAsync();

        Assert.Equal(new[] { "eCola", "Burger" }, active.Select(d => d.Name));
        Assert.Equal(new[] { 7, 4 }, active.Select(d => d.OnHand));
        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.Single(d => d.Name == "Wasser").OnHand);
    }

    [Fact]
    public async Task GetActive_SkipsPositionsWhoseEvidenceItemIsGone()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var burger = await DepositAsync(h.Evidence, "Burger", 4);
        var created = await h.Svc.CreateAsync(Input(burger), Leader());
        await using (var db = ctx.NewContext())
        {
            var item = await db.EvidenceItems.FirstAsync(i => i.Id == burger);
            item.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await h.Svc.GetActiveAsync());
        // leadership still sees and can edit the orphaned position
        Assert.Equal("(gelöschtes Asservat)", Assert.Single(await h.Svc.GetAllAsync()).Name);
        var loaded = await h.Svc.GetAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.EvidenceItem);
    }

    [Fact]
    public async Task Delete_OfAMissingPosition_IsANoOp()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);

        await h.Svc.DeleteAsync("fehlt", Leader());
    }
}
