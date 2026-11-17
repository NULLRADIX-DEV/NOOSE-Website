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

/// <summary>Integration tests for <see cref="FoodService"/> over in-memory SQLite, with the real evidence room behind the issue.</summary>
public sealed class FoodServiceTests
{
    private const string OwnerId = "owner";
    private const string OtherId = "other";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Owner()
        => ClaimsPrincipalBuilder.Agent(OwnerId).WithRank(Rank.SpecialAgent).WithCodename("Owner").Build();

    private static ClaimsPrincipal Other()
        => ClaimsPrincipalBuilder.Agent(OtherId).WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Senior").Build();

    private sealed record Harness(FoodService Svc, EvidenceService Evidence, FoodLimitService Limits);

    private static Harness Build(SqliteTestContext ctx, FoodLimitConfig? config = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var ver = 0;
        var ass = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "VER", Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-VER-2026-{++ver:0000}");
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ASS", Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-ASS-2026-{++ass:0000}");

        var configService = Substitute.For<IFoodLimitConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(config ?? FoodLimitConfig.Default());

        var evidence = new EvidenceService(ctx.Factory, caseNo, Substitute.For<IEvidenceImageStorageService>(),
            new ProfileSuggestionService(ctx.Factory));
        var limits = new FoodLimitService(ctx.Factory, configService);
        var svc = new FoodService(ctx.Factory, caseNo, limits, evidence, Substitute.For<INotificationService>());
        return new Harness(svc, evidence, limits);
    }

    private static FoodLimitConfig Tight(int food, int drink) => new()
    {
        Ranks = new Dictionary<string, FoodRankLimit>
        {
            [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = food, DrinkPerWeek = drink },
        },
    };

    /// <summary>Agents, two stocked evidence items and their catalog positions ("burger" food, "cola" drink).</summary>
    private static async Task<(string Burger, string Cola)> SeedAsync(SqliteTestContext ctx, Harness h,
        int burgerStock = 10, int colaStock = 10, bool burgerActive = true, Action<NOOSE_Website.Data.Entities.Agent>? owner = null)
    {
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(OwnerId, Rank.SpecialAgent, configure: a =>
            {
                a.Codename = "Owner";
                owner?.Invoke(a);
            }));
            db.Users.Add(Seed.Agent(OtherId, Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("lead", Rank.Director, configure: a => a.Codename = "Falcon"));
            db.Users.Add(Seed.Agent("senior", Rank.SeniorSpecialAgent));
            await db.SaveChangesAsync();
        }

        var burger = await DepositAsync(h.Evidence, "Burger", burgerStock);
        var cola = await DepositAsync(h.Evidence, "eCola", colaStock);

        await using (var db = ctx.NewContext())
        {
            db.FoodItems.Add(new FoodItem { Id = "burger", EvidenceItemId = burger, Kind = FoodKind.Food, MaxQuantity = 5, IsActive = burgerActive });
            db.FoodItems.Add(new FoodItem { Id = "cola", EvidenceItemId = cola, Kind = FoodKind.Drink, MaxQuantity = 5, Sorting = 1 });
            await db.SaveChangesAsync();
        }
        return (burger, cola);
    }

    private static async Task<string> DepositAsync(EvidenceService evidence, string name, int quantity)
    {
        await evidence.CreateEntryAsync(new EvidenceEntryInput
        {
            Type = EvidenceEntryType.Deposit,
            OwnerType = EvidenceService.NooseOwner,
            HandlerAgentId = "lead",
            Timestamp = DateTime.UtcNow.AddDays(-1),
            Lines = { new EvidenceLineInput { ItemName = name, Quantity = quantity } },
        }, Leader());
        return (await evidence.GetItemByNameAsync(name))!.Id;
    }

    private static FoodRequestInput Basket(params (string ItemId, int Quantity)[] lines)
        => new()
        {
            Justification = "Lange Schicht",
            Lines = lines.Select(l => new FoodRequestLineInput { ItemId = l.ItemId, Quantity = l.Quantity }).ToList(),
        };

    private static async Task<List<FoodRequestLine>> LinesAsync(SqliteTestContext ctx, string requestId)
    {
        await using var db = ctx.NewContext();
        return await db.FoodRequestLines.Where(l => l.RequestId == requestId).OrderBy(l => l.Sorting).ToListAsync();
    }

    private static async Task<FoodRequest> ReloadAsync(SqliteTestContext ctx, string requestId)
    {
        await using var db = ctx.NewContext();
        return await db.FoodRequests.IgnoreQueryFilters().Include(r => r.Lines).FirstAsync(r => r.Id == requestId);
    }

    /// <summary>Flags a request deleted directly; the soft-delete interceptor is not wired in tests.</summary>
    private static async Task SoftDeleteAsync(SqliteTestContext ctx, string requestId, DateTime? limitWeek = null)
    {
        await using var db = ctx.NewContext();
        var row = await db.FoodRequests.FirstAsync(r => r.Id == requestId);
        row.IsDeleted = true;
        row.DeletedAt = DateTime.UtcNow;
        if (limitWeek is { } week)
        {
            row.LimitWeekStart = week;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<List<EvidenceEntry>> WithdrawalsAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        return await db.EvidenceEntries.IgnoreQueryFilters().Include(e => e.Lines)
            .Where(e => e.Type == EvidenceEntryType.Withdrawal).ToListAsync();
    }

    /// <summary>Files 3 burgers and 2 colas and approves them uncut.</summary>
    private static async Task<FoodRequest> ApprovedAsync(Harness h, int burgers = 3, int colas = 2)
    {
        var created = await h.Svc.CreateAsync(Basket(("burger", burgers), ("cola", colas)), Owner());
        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput(), Leader());
        return created;
    }

    // ---- create ----

    [Fact]
    public async Task Create_SnapshotsNameKindAndEvidenceItem_AndAssignsCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, cola) = await SeedAsync(ctx, h);

        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());

        Assert.Equal("FIB-VER-2026-0001", created.CaseNumber);
        Assert.Equal(FoodRequestStatus.Requested, created.Status);
        Assert.Equal(3, created.FoodRequested);
        Assert.Equal(2, created.DrinkRequested);
        Assert.Equal("Lange Schicht", created.Justification);

        var lines = await LinesAsync(ctx, created.Id);
        Assert.Equal(2, lines.Count);
        Assert.Equal(("burger", burger, "Burger", FoodKind.Food, 3), (lines[0].ItemId, lines[0].EvidenceItemId, lines[0].ItemName, lines[0].Kind, lines[0].Quantity));
        Assert.Equal(("cola", cola, "eCola", FoodKind.Drink, 2), (lines[1].ItemId, lines[1].EvidenceItemId, lines[1].ItemName, lines[1].Kind, lines[1].Quantity));
        Assert.All(lines, l => Assert.Null(l.ApprovedQuantity));
    }

    [Fact]
    public async Task Create_SnapshotStaysFrozen_WhenTheCatalogChanges()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());

        await using (var db = ctx.NewContext())
        {
            var item = await db.FoodItems.FirstAsync(i => i.Id == "burger");
            item.Kind = FoodKind.Drink;
            var evidenceItem = await db.EvidenceItems.FirstAsync(i => i.Id == item.EvidenceItemId);
            evidenceItem.Name = "Umbenannt";
            await db.SaveChangesAsync();
        }

        var line = Assert.Single(await LinesAsync(ctx, created.Id));
        Assert.Equal("Burger", line.ItemName);
        Assert.Equal(FoodKind.Food, line.Kind);
    }

    [Fact]
    public async Task Create_RefusesQuantityAboveTheMaximum()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Basket(("burger", 6)), Owner()));
        Assert.Contains("höchstens 5", ex.Message);
    }

    [Fact]
    public async Task Create_RefusesZeroQuantity()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Basket(("burger", 0)), Owner()));
    }

    [Fact]
    public async Task Create_RefusesInactivePosition()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h, burgerActive: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Basket(("burger", 1)), Owner()));
        await using var db = ctx.NewContext();
        Assert.Empty(db.FoodRequests);
    }

    [Fact]
    public async Task Create_RefusesTeamLead()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h, owner: a => a.IsTeamLead = true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Basket(("burger", 1)), Owner()));
        Assert.Contains("Team-Leitung", ex.Message);
    }

    [Fact]
    public async Task Create_RefusesReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var teamLead = ClaimsPrincipalBuilder.Agent(OwnerId).WithRank(Rank.SpecialAgent).AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.CreateAsync(Basket(("burger", 1)), teamLead));
    }

    [Fact]
    public async Task Create_RefusesDuplicateLines()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Basket(("burger", 1), ("burger", 2)), Owner()));
    }

    [Fact]
    public async Task Create_RefusesAnEmptyBasket()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CreateAsync(Basket(), Owner()));
    }

    // ---- decide ----

    [Fact]
    public async Task Decide_CutsPerLine_AndStampsTheRunningWeek()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());
        var lines = await LinesAsync(ctx, created.Id);

        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput
        {
            ApprovedQuantities = { [lines[0].Id] = 1 },
            Note = "Nur einer",
        }, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Approved, request.Status);
        Assert.Equal(1, request.FoodApproved);
        // a line left out keeps its requested quantity
        Assert.Equal(2, request.DrinkApproved);
        Assert.Equal(FoodWeek.Current(), request.LimitWeekStart);
        Assert.Equal("Falcon", request.DeciderName);
        Assert.Equal("Nur einer", request.DecisionNote);
        Assert.NotNull(request.DecidedAt);
        Assert.Null(request.OverrunReason);
        var decided = request.Lines.OrderBy(l => l.Sorting).ToList();
        Assert.Equal(1, decided[0].ApprovedQuantity);
        Assert.Equal(2, decided[1].ApprovedQuantity);
    }

    [Fact]
    public async Task Decide_RefusesACutAboveTheRequestedQuantity()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 2)), Owner());
        var line = Assert.Single(await LinesAsync(ctx, created.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, true,
            new FoodDecisionInput { ApprovedQuantities = { [line.Id] = 3 } }, Leader()));
    }

    [Fact]
    public async Task Decide_AllZero_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());
        var lines = await LinesAsync(ctx, created.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, true,
            new FoodDecisionInput { ApprovedQuantities = { [lines[0].Id] = 0, [lines[1].Id] = 0 } }, Leader()));
        Assert.Equal(FoodRequestStatus.Requested, (await ReloadAsync(ctx, created.Id)).Status);
    }

    [Fact]
    public async Task Decide_FoodOverrun_RequiresAReason_AndStoresIt()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx, Tight(food: 2, drink: 10));
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput(), Leader()));
        Assert.Contains("Essen um 1", ex.Message);
        Assert.Equal(FoodRequestStatus.Requested, (await ReloadAsync(ctx, created.Id)).Status);

        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput { OverrunReason = "  Großeinsatz  " }, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Approved, request.Status);
        Assert.Equal(1, request.OverrunFood);
        Assert.Null(request.OverrunDrink);
        Assert.Equal("Großeinsatz", request.OverrunReason);
    }

    [Fact]
    public async Task Decide_DrinkOverrun_RequiresAReason_AndStoresIt()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx, Tight(food: 10, drink: 1));
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 1), ("cola", 3)), Owner());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput { OverrunReason = "   " }, Leader()));
        Assert.Contains("Trinken um 2", ex.Message);

        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput { OverrunReason = "Hitzewelle" }, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Null(request.OverrunFood);
        Assert.Equal(2, request.OverrunDrink);
        Assert.Equal("Hitzewelle", request.OverrunReason);
    }

    [Fact]
    public async Task Decide_OverrunCountsEarlierApprovalsOfTheWeek()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx, Tight(food: 4, drink: 10));
        await SeedAsync(ctx, h);
        await ApprovedAsync(h, burgers: 3, colas: 1);
        var second = await h.Svc.CreateAsync(Basket(("burger", 2)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.DecideAsync(second.Id, true, new FoodDecisionInput(), Leader()));
    }

    [Fact]
    public async Task Decide_CutBelowTheLimit_NeedsNoReason()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx, Tight(food: 2, drink: 10));
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3)), Owner());
        var line = Assert.Single(await LinesAsync(ctx, created.Id));

        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput { ApprovedQuantities = { [line.Id] = 2 } }, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(2, request.FoodApproved);
        Assert.Null(request.OverrunFood);
    }

    [Fact]
    public async Task Decide_Reject_StrikesEveryLine()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());

        await h.Svc.DecideAsync(created.Id, false, new FoodDecisionInput { Note = "Kein Bedarf" }, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Rejected, request.Status);
        Assert.All(request.Lines, l => Assert.Equal(0, l.ApprovedQuantity));
        Assert.Null(request.FoodApproved);
        Assert.Null(request.DrinkApproved);
        Assert.Null(request.LimitWeekStart);
        Assert.Equal("Kein Bedarf", request.DecisionNote);
    }

    [Fact]
    public async Task Decide_RequiresLeadership()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput(), Senior()));
    }

    [Fact]
    public async Task Decide_AlreadyApproved_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput(), Leader()));
    }

    // ---- issue ----

    [Fact]
    public async Task Issue_BooksExactlyOneWithdrawalFromTheFibStock()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, cola) = await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());
        var lines = await LinesAsync(ctx, created.Id);
        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput { ApprovedQuantities = { [lines[0].Id] = 2 } }, Leader());

        await h.Svc.IssueAsync(created.Id, Leader());

        var entry = Assert.Single(await WithdrawalsAsync(ctx));
        Assert.Equal(EvidenceService.NooseOwner, entry.OwnerType);
        Assert.Null(entry.OwnerId);
        Assert.False(entry.IsDeleted);
        Assert.Contains(created.CaseNumber, entry.Notes);
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(2, entry.Lines.Single(l => l.ItemId == burger).Quantity);
        Assert.Equal(2, entry.Lines.Single(l => l.ItemId == cola).Quantity);

        Assert.Equal(8, await h.Evidence.GetOnHandAsync(burger));
        Assert.Equal(8, await h.Evidence.GetOnHandAsync(cola));

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Issued, request.Status);
        Assert.Equal(entry.Id, request.EvidenceEntryId);
        Assert.NotNull(request.IssuedAt);
        Assert.Equal("Falcon", request.IssuedByName);
    }

    [Fact]
    public async Task Issue_LeavesStruckLinesOutOfTheBooking()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, cola) = await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 3), ("cola", 2)), Owner());
        var lines = await LinesAsync(ctx, created.Id);
        await h.Svc.DecideAsync(created.Id, true, new FoodDecisionInput { ApprovedQuantities = { [lines[1].Id] = 0 } }, Leader());

        await h.Svc.IssueAsync(created.Id, Leader());

        var entry = Assert.Single(await WithdrawalsAsync(ctx));
        var line = Assert.Single(entry.Lines);
        Assert.Equal((burger, 3), (line.ItemId, line.Quantity));
        Assert.Equal(10, await h.Evidence.GetOnHandAsync(cola));
    }

    [Fact]
    public async Task Issue_WithInsufficientStock_ThrowsAndLeavesNoEntry()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, _) = await SeedAsync(ctx, h, burgerStock: 1);
        var created = await ApprovedAsync(h, burgers: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.IssueAsync(created.Id, Leader()));
        Assert.Contains("Nicht genug", ex.Message);

        Assert.Empty(await WithdrawalsAsync(ctx));
        Assert.Equal(1, await h.Evidence.GetOnHandAsync(burger));
        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Approved, request.Status);
        Assert.Null(request.EvidenceEntryId);
        Assert.Null(request.IssuedAt);
    }

    [Fact]
    public async Task Issue_BelowSeniorSpecialAgent_IsUnauthorized()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.IssueAsync(created.Id, Other()));
        Assert.Empty(await WithdrawalsAsync(ctx));
    }

    [Fact]
    public async Task Issue_BySeniorSpecialAgent_IsAllowed()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);

        await h.Svc.IssueAsync(created.Id, Senior());

        Assert.Equal(FoodRequestStatus.Issued, (await ReloadAsync(ctx, created.Id)).Status);
    }

    [Fact]
    public async Task Issue_Twice_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.IssueAsync(created.Id, Leader()));
        Assert.Single(await WithdrawalsAsync(ctx));
    }

    [Fact]
    public async Task Issue_OfAnUndecidedRequest_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.IssueAsync(created.Id, Leader()));
        Assert.Empty(await WithdrawalsAsync(ctx));
    }

    [Fact]
    public async Task IssuedWithdrawal_CannotBeEditedInTheEvidenceRoom()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, _) = await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());
        var entry = Assert.Single(await WithdrawalsAsync(ctx));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Evidence.UpdateEntryAsync(entry.Id, new EvidenceEntryInput
        {
            Type = EvidenceEntryType.Withdrawal,
            OwnerType = EvidenceService.NooseOwner,
            HandlerAgentId = "lead",
            Lines = { new EvidenceLineInput { ItemId = burger, Quantity = 1 } },
        }, Leader()));
        Assert.Contains(created.CaseNumber, ex.Message);

        var unchanged = Assert.Single(await WithdrawalsAsync(ctx));
        Assert.Equal(3, unchanged.Lines.Single(l => l.ItemId == burger).Quantity);
    }

    [Fact]
    public async Task IssuedWithdrawal_CannotBeDeletedInTheEvidenceRoom()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, _) = await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());
        var entry = Assert.Single(await WithdrawalsAsync(ctx));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Evidence.DeleteEntryAsync(entry.Id, Leader()));

        Assert.False(Assert.Single(await WithdrawalsAsync(ctx)).IsDeleted);
        Assert.Equal(7, await h.Evidence.GetOnHandAsync(burger));
    }

    // ---- cancel issue ----

    [Fact]
    public async Task CancelIssue_RemovesTheWithdrawal_RestoresStock_AndResetsToApproved()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, cola) = await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());
        var entryId = (await ReloadAsync(ctx, created.Id)).EvidenceEntryId!;

        await h.Svc.CancelIssueAsync(created.Id, Leader());

        // hard delete: nothing left in the evidence trash to restore
        Assert.Empty(await WithdrawalsAsync(ctx));
        await using (var db = ctx.NewContext())
        {
            Assert.False(await db.EvidenceEntryLines.AnyAsync(l => l.EntryId == entryId));
        }
        Assert.Equal(10, await h.Evidence.GetOnHandAsync(burger));
        Assert.Equal(10, await h.Evidence.GetOnHandAsync(cola));

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Approved, request.Status);
        Assert.Null(request.EvidenceEntryId);
        Assert.Null(request.IssuedAt);
        Assert.Null(request.IssuedByName);
        // the approval keeps charging the week
        Assert.Equal(3, request.FoodApproved);
    }

    [Fact]
    public async Task CancelIssue_ThenIssueAgain_BooksAFreshWithdrawal()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        var (burger, _) = await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());
        await h.Svc.CancelIssueAsync(created.Id, Leader());

        await h.Svc.IssueAsync(created.Id, Leader());

        Assert.Single(await WithdrawalsAsync(ctx));
        Assert.Equal(7, await h.Evidence.GetOnHandAsync(burger));
    }

    [Fact]
    public async Task CancelIssue_OfAnApprovedRequest_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CancelIssueAsync(created.Id, Leader()));
    }

    [Fact]
    public async Task CancelIssue_RequiresLeadership()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.CancelIssueAsync(created.Id, Senior()));
        Assert.Single(await WithdrawalsAsync(ctx));
    }

    // ---- revoke, withdraw, delete, restore ----

    [Fact]
    public async Task RevokeApproval_BackToRequested_FreesTheLimit()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        Assert.Equal(3, (await h.Limits.GetStatusAsync(OwnerId)).FoodConsumed);

        await h.Svc.RevokeApprovalAsync(created.Id, false, null, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Requested, request.Status);
        Assert.All(request.Lines, l => Assert.Null(l.ApprovedQuantity));
        Assert.Null(request.LimitWeekStart);
        Assert.Equal(0, (await h.Limits.GetStatusAsync(OwnerId)).FoodConsumed);
    }

    [Fact]
    public async Task Withdraw_OnlyByTheOwner()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.WithdrawAsync(created.Id, Other()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.WithdrawAsync(created.Id, Leader()));
        Assert.Equal(FoodRequestStatus.Requested, (await ReloadAsync(ctx, created.Id)).Status);

        await h.Svc.WithdrawAsync(created.Id, Owner());
        Assert.Equal(FoodRequestStatus.Withdrawn, (await ReloadAsync(ctx, created.Id)).Status);
    }

    [Fact]
    public async Task Withdraw_AfterApproval_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.WithdrawAsync(created.Id, Owner()));
    }

    [Fact]
    public async Task Delete_IsRefused_WhenIssued()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DeleteAsync(created.Id, Leader()));
        Assert.False((await ReloadAsync(ctx, created.Id)).IsDeleted);
    }

    [Fact]
    public async Task Delete_ByOwner_OnlyWhileOpen()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var open = await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());
        var approved = await ApprovedAsync(h);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.DeleteAsync(approved.Id, Owner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.DeleteAsync(open.Id, Other()));
        await h.Svc.DeleteAsync(open.Id, Owner());

        await using var db = ctx.NewContext();
        Assert.False(await db.FoodRequests.AnyAsync(r => r.Id == open.Id));
        Assert.True(await db.FoodRequests.AnyAsync(r => r.Id == approved.Id));
    }

    [Fact]
    public async Task Restore_OfAnApprovalFromAPastWeek_FallsBackToRequested()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await SoftDeleteAsync(ctx, created.Id, FoodWeek.Current().AddDays(-7));

        await h.Svc.RestoreAsync(created.Id, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.False(request.IsDeleted);
        Assert.Equal(FoodRequestStatus.Requested, request.Status);
        Assert.Null(request.LimitWeekStart);
        Assert.Null(request.FoodApproved);
        Assert.Null(request.DeciderName);
        Assert.All(request.Lines, l => Assert.Null(l.ApprovedQuantity));
    }

    [Fact]
    public async Task Restore_OfAnApprovalFromThisWeek_StaysApproved()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await SoftDeleteAsync(ctx, created.Id);
        Assert.Equal(0, (await h.Limits.GetStatusAsync(OwnerId)).FoodConsumed);

        await h.Svc.RestoreAsync(created.Id, Leader());

        var request = await ReloadAsync(ctx, created.Id);
        Assert.Equal(FoodRequestStatus.Approved, request.Status);
        Assert.Equal(FoodWeek.Current(), request.LimitWeekStart);
        Assert.Equal(3, (await h.Limits.GetStatusAsync(OwnerId)).FoodConsumed);
    }

    // ---- visibility ----

    [Fact]
    public async Task Get_AnotherAgentsRequest_IsInvisible_ButReadersOfEverythingSeeIt()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());

        Assert.Null(await h.Svc.GetAsync(created.Id, Other()));
        Assert.NotNull(await h.Svc.GetAsync(created.Id, Owner()));
        Assert.NotNull(await h.Svc.GetAsync(created.Id, Leader()));
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.SpecialAgent).AsTeamLead().Build();
        Assert.NotNull(await h.Svc.GetAsync(created.Id, supervision));
    }

    [Fact]
    public async Task Lists_FollowTheVisibilityRule()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        await h.Svc.CreateAsync(Basket(("burger", 1)), Owner());

        Assert.Empty(await h.Svc.GetVisibleAsync(null, Other()));
        Assert.Empty(await h.Svc.GetForAgentAsync(OwnerId, 10, Other()));
        Assert.Single(await h.Svc.GetVisibleAsync(null, Owner()));
        var all = Assert.Single(await h.Svc.GetVisibleAsync(FoodRequestStatus.Requested, Leader()));
        Assert.Equal("Owner", all.AgentCodename);
        Assert.Single(await h.Svc.GetForAgentAsync(OwnerId, 10, Leader()));
        Assert.Empty(await h.Svc.GetVisibleAsync(FoodRequestStatus.Approved, Leader()));
    }

    [Fact]
    public async Task GetByEvidenceEntry_FindsTheIssuedRequest_ForVisibleCallersOnly()
    {
        using var ctx = new SqliteTestContext();
        var h = Build(ctx);
        await SeedAsync(ctx, h);
        var created = await ApprovedAsync(h);
        await h.Svc.IssueAsync(created.Id, Leader());
        var entry = Assert.Single(await WithdrawalsAsync(ctx));

        Assert.Equal(created.Id, (await h.Svc.GetByEvidenceEntryAsync(entry.Id, Leader()))?.Id);
        Assert.Null(await h.Svc.GetByEvidenceEntryAsync(entry.Id, Other()));
    }
}
