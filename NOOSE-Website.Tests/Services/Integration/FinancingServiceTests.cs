using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FinancingService"/> over in-memory SQLite.</summary>
public sealed class FinancingServiceTests
{
    private const string OwnerId = "owner";
    private const string OtherId = "other";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Owner(Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(OwnerId).WithRank(rank).WithCodename("Owner").Build();

    private static ClaimsPrincipal Other()
        => ClaimsPrincipalBuilder.Agent(OtherId).WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private sealed record Harness(FinancingService Svc, KassenService Kasse, FinancingBudgetService Budget);

    private static Harness Build(SqliteTestContext ctx, FinancingBudgetConfig? config = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        var fin = 0;
        var kas = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "FIN", Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-FIN-2026-{++fin:0000}");
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "KAS", Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-KAS-2026-{++kas:0000}");

        var configService = Substitute.For<IFinancingConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(config ?? FinancingBudgetConfig.Default());

        var budget = new FinancingBudgetService(ctx.Factory, configService);
        var kasse = new KassenService(ctx.Factory, caseNo);
        var svc = new FinancingService(ctx.Factory, caseNo, budget, kasse,
            Substitute.For<INotificationService>());
        return new Harness(svc, kasse, budget);
    }

    private static FinancingBudgetConfig TightConfig(decimal amount, int carryPercent = 0) => new()
    {
        Ranks = new Dictionary<string, FinancingRankBudget>
        {
            [FinancingBudgetConfig.RankKey(Rank.SpecialAgent)] = new() { BaseMonthly = amount, CarryOverPercent = carryPercent },
        },
    };

    private static FinancingItem Item(string id, decimal price, int percent = 100, int max = 5,
        Rank min = Rank.Trainee, bool active = true)
        => new()
        {
            Id = id,
            Name = $"Position {id}",
            UnitPrice = price,
            SubsidyPercent = percent,
            MaxQuantity = max,
            MinimumRank = min,
            IsActive = active,
        };

    private static async Task SeedAsync(SqliteTestContext ctx, params FinancingItem[] items)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(OwnerId, Rank.SpecialAgent, configure: a => a.Codename = "Owner"));
        db.Users.Add(Seed.Agent(OtherId, Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.FinancingItems.AddRange(items);
        await db.SaveChangesAsync();
    }

    private static FinancingRequestInput Cart(string justification, params (string ItemId, int Quantity)[] lines)
        => new()
        {
            Justification = justification,
            Lines = lines.Select(l => new FinancingRequestLineInput { ItemId = l.ItemId, Quantity = l.Quantity }).ToList(),
        };

    private static async Task FundAsync(KassenService kasse, decimal amount)
        => await kasse.BookAsync(new KassenBuchungInput
        {
            Account = KassenKonto.Gruengeld,
            Kind = KassenBuchungArt.Einzahlung,
            Amount = amount,
            Timestamp = DateTime.UtcNow.AddHours(-1),
        }, Leader());

    // ---- create ----

    [Fact]
    public async Task Create_SnapshotsCatalogValues_AndAssignsCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 10_000m, percent: 70), Item("b", 2_000m));
        var h = Build(ctx);

        var created = await h.Svc.CreateAsync(Cart("Brauche Ausrüstung", ("a", 1), ("b", 3)), Owner());

        Assert.Equal("FIB-FIN-2026-0001", created.CaseNumber);
        Assert.Equal(FinancingStatus.Requested, created.Status);
        // 10.000 + 6.000 warenwert
        Assert.Equal(16_000m, created.RequestedGross);
        // 70 % von 10.000 = 7.000, plus 100 % von 6.000
        Assert.Equal(13_000m, created.RequestedSubsidy);

        await using var db = ctx.NewContext();
        var lines = await db.FinancingRequestLines.Where(l => l.RequestId == created.Id)
            .OrderBy(l => l.Sorting).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Position a", lines[0].ItemName);
        Assert.Equal(10_000m, lines[0].UnitPrice);
        Assert.Equal(70, lines[0].SubsidyPercent);
        Assert.Null(lines[0].ApprovedQuantity);
    }

    [Fact]
    public async Task Create_SnapshotStaysFrozen_WhenTheCatalogPriceChanges()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 10_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await using (var db = ctx.NewContext())
        {
            var item = await db.FinancingItems.FirstAsync(i => i.Id == "a");
            item.UnitPrice = 99_000m;
            await db.SaveChangesAsync();
        }

        var reloaded = await h.Svc.GetAsync(created.Id, Owner());
        Assert.NotNull(reloaded);
        Assert.Equal(10_000m, reloaded!.Lines[0].UnitPrice);
        Assert.Equal(10_000m, reloaded.RequestedSubsidy);
    }

    [Fact]
    public async Task Create_RejectsQuantityAboveTheItemMaximum()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 100m, max: 2));
        var h = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("Grund", ("a", 3)), Owner()));
    }

    [Fact]
    public async Task Create_RejectsInactiveItem()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 100m, active: false));
        var h = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner()));
    }

    [Fact]
    public async Task Create_RejectsItemAboveTheRequesterRank()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 100m, min: Rank.Director));
        var h = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner()));
    }

    [Fact]
    public async Task Create_RejectsEmptyCartAndMissingJustificationAndDuplicates()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 100m));
        var h = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("Grund"), Owner()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("   ", ("a", 1)), Owner()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("Grund", ("a", 1), ("a", 1)), Owner()));
    }

    [Fact]
    public async Task Create_DeniedForReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 100m));
        var h = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Svc.CreateAsync(Cart("Grund", ("a", 1)), OnlyReader()));
    }

    [Fact]
    public async Task Create_DeniedForATeamLead_EvenWithTheAdminFlag()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 100m));
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("tl-admin", Rank.Director, configure: a =>
            {
                a.IsTeamLead = true;
                a.IsAdmin = true;
            }));
            await db.SaveChangesAsync();
        }
        var h = Build(ctx);
        // admin clears IsOnlyReader, so MayWrite() lets this principal through — the service must not
        var teamLeadAdmin = ClaimsPrincipalBuilder.Agent("tl-admin")
            .WithRank(Rank.Director).AsTeamLead().AsAdmin().Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.CreateAsync(Cart("Grund", ("a", 1)), teamLeadAdmin));

        await using var check = ctx.NewContext();
        Assert.Empty(await check.FinancingRequests.ToListAsync());
    }

    // ---- decide ----

    [Fact]
    public async Task Decide_CutsQuantitiesAndSumsTheApprovedSubsidy()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m), Item("b", 500m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 3), ("b", 2)), Owner());
        var lines = created.Lines.OrderBy(l => l.Sorting).ToList();

        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput
        {
            ApprovedQuantities = new Dictionary<string, int> { [lines[0].Id] = 1, [lines[1].Id] = 0 },
        }, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.NotNull(reloaded);
        Assert.Equal(FinancingStatus.Approved, reloaded!.Status);
        Assert.Equal(1_000m, reloaded.ApprovedSubsidy);
        Assert.Equal(1, reloaded.Lines[0].ApprovedQuantity);
        Assert.Equal(0, reloaded.Lines[1].ApprovedQuantity);
        Assert.Equal("Falcon", reloaded.DeciderName);
        Assert.NotNull(reloaded.DecidedAt);
        // the requested figures survive the cut
        Assert.Equal(4_000m, reloaded.RequestedSubsidy);
    }

    [Fact]
    public async Task Decide_WithoutCuts_ApprovesTheFullRequest()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());

        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(2_000m, reloaded!.ApprovedSubsidy);
    }

    [Fact]
    public async Task Decide_AllLinesCutToZero_Throws()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, approved: true,
            new FinancingDecisionInput { ApprovedQuantities = new() { [created.Lines[0].Id] = 0 } }, Leader()));
    }

    [Fact]
    public async Task Decide_QuantityAboveTheRequestedOne_Throws()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, approved: true,
            new FinancingDecisionInput { ApprovedQuantities = new() { [created.Lines[0].Id] = 5 } }, Leader()));
    }

    [Fact]
    public async Task Decide_OverBudget_NeedsAReason_AndRecordsTheOverrun()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 5_000m));
        var h = Build(ctx, TightConfig(3_000m));
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, approved: true,
            new FinancingDecisionInput(), Leader()));

        await h.Svc.DecideAsync(created.Id, approved: true,
            new FinancingDecisionInput { OverrunReason = "Einsatzkritisch" }, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(2_000m, reloaded!.OverrunAmount);
        Assert.Equal("Einsatzkritisch", reloaded.OverrunReason);
    }

    [Fact]
    public async Task Decide_WithinBudget_RecordsNoOverrun()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx, TightConfig(3_000m));
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Null(reloaded!.OverrunAmount);
        Assert.Null(reloaded.OverrunReason);
    }

    [Fact]
    public async Task Decide_Rejection_ClearsTheReservationAndStrikesTheLines()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());

        await h.Svc.DecideAsync(created.Id, approved: false,
            new FinancingDecisionInput { Note = "Nicht nötig" }, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Rejected, reloaded!.Status);
        Assert.Null(reloaded.ApprovedSubsidy);
        Assert.Null(reloaded.BudgetYear);
        Assert.Equal(0, reloaded.Lines[0].ApprovedQuantity);
        Assert.Equal("Nicht nötig", reloaded.DecisionNote);
    }

    [Fact]
    public async Task Decide_RejectionIsReversible()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: false, new FinancingDecisionInput(), Leader());

        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Approved, reloaded!.Status);
        Assert.Equal(1_000m, reloaded.ApprovedSubsidy);
    }

    [Fact]
    public async Task Decide_OnAnApprovedRequest_Throws()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, approved: true,
            new FinancingDecisionInput(), Leader()));
    }

    [Fact]
    public async Task Decide_DeniedForNonLeadershipAndForReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Owner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), OnlyReader()));
    }

    // ---- withdraw / revoke ----

    [Fact]
    public async Task Withdraw_OnlyTheOwnerAndOnlyWhileOpen()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.WithdrawAsync(created.Id, Other()));

        await h.Svc.WithdrawAsync(created.Id, Owner());
        var reloaded = await h.Svc.GetAsync(created.Id, Owner());
        Assert.Equal(FinancingStatus.Withdrawn, reloaded!.Status);

        // withdrawn is terminal
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.WithdrawAsync(created.Id, Owner()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DecideAsync(created.Id, approved: true,
            new FinancingDecisionInput(), Leader()));
    }

    [Fact]
    public async Task RevokeApproval_FreesTheReservedBudget()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx, TightConfig(3_000m));
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        Assert.Equal(2_000m, (await h.Budget.GetStatusAsync(OwnerId)).Remaining);

        await h.Svc.RevokeApprovalAsync(created.Id, reject: false, "Doch nicht", Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Requested, reloaded!.Status);
        Assert.Null(reloaded.ApprovedSubsidy);
        Assert.Null(reloaded.Lines[0].ApprovedQuantity);
        Assert.Equal(3_000m, (await h.Budget.GetStatusAsync(OwnerId)).Remaining);
    }

    [Fact]
    public async Task RevokeApproval_OnlyFromApproved()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Svc.RevokeApprovalAsync(created.Id, reject: false, null, Leader()));
    }

    // ---- payout ----

    [Fact]
    public async Task Pay_BooksAGruengeldWithdrawalOnTheRecipient()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        await h.Svc.PayAsync(created.Id, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Paid, reloaded!.Status);
        Assert.NotNull(reloaded.KassenBuchungId);
        Assert.Equal("Falcon", reloaded.PaidByName);
        Assert.NotNull(reloaded.PaidAt);

        Assert.Equal(3_000m, await h.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));

        await using var db = ctx.NewContext();
        var booking = await db.KassenBuchungen.FirstAsync(b => b.Id == reloaded.KassenBuchungId);
        Assert.Equal(KassenKonto.Gruengeld, booking.Account);
        Assert.Equal(KassenBuchungArt.Auszahlung, booking.Kind);
        Assert.Equal(2_000m, booking.Amount);
        // the recipient, so the ledger can be filtered by who got the money
        Assert.Equal(OwnerId, booking.BookedById);
        Assert.Contains(created.CaseNumber, booking.Reason);
    }

    [Fact]
    public async Task Pay_MakesTheLedgerRowPointBackAtTheRequest()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await h.Svc.PayAsync(created.Id, Leader());

        var ledger = await h.Kasse.GetLedgerAsync(KassenKonto.Gruengeld);
        var payout = ledger.Single(r => r.Buchung.Kind == KassenBuchungArt.Auszahlung);
        Assert.Equal(created.Id, payout.FinancingRequestId);
        Assert.Equal(created.CaseNumber, payout.FinancingCaseNumber);
        // the deposit that funded the account came from nowhere near a request
        Assert.Null(ledger.Single(r => r.Buchung.Kind == KassenBuchungArt.Einzahlung).FinancingRequestId);
    }

    [Fact]
    public async Task CancelPayment_DropsTheLedgerBackReference()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await h.Svc.PayAsync(created.Id, Leader());

        await h.Svc.CancelPaymentAsync(created.Id, Leader());

        var ledger = await h.Kasse.GetLedgerAsync(KassenKonto.Gruengeld);
        Assert.DoesNotContain(ledger, r => r.FinancingRequestId is not null);
    }

    [Fact]
    public async Task Pay_InsufficientBalance_Throws_AndChangesNothing()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 1_500m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.PayAsync(created.Id, Leader()));

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Approved, reloaded!.Status);
        Assert.Null(reloaded.KassenBuchungId);
        // the rolled-back transaction left no booking behind
        Assert.Equal(1_500m, await h.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
        await using var db = ctx.NewContext();
        Assert.Equal(1, await db.KassenBuchungen.CountAsync());
    }

    [Fact]
    public async Task Pay_Twice_Throws()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await h.Svc.PayAsync(created.Id, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.PayAsync(created.Id, Leader()));
    }

    [Fact]
    public async Task Pay_RequiresAnApprovedRequest()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.PayAsync(created.Id, Leader()));
    }

    [Fact]
    public async Task CancelPayment_CancelsTheBooking_ButKeepsTheBudgetConsumed()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx, TightConfig(5_000m));
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await h.Svc.PayAsync(created.Id, Leader());
        var bookingId = (await h.Svc.GetAsync(created.Id, Leader()))!.KassenBuchungId;

        await h.Svc.CancelPaymentAsync(created.Id, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Approved, reloaded!.Status);
        Assert.Null(reloaded.KassenBuchungId);
        Assert.Null(reloaded.PaidAt);
        // the money is rechnerisch back in the treasury
        Assert.Equal(5_000m, await h.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
        // still approved, so the reservation stands
        Assert.Equal(3_000m, (await h.Budget.GetStatusAsync(OwnerId)).Remaining);

        // hard delete on purpose: a soft-deleted payout could be restored from the treasury trash and
        // would then debit Grüngeld a second time, so it must be gone even ignoring the query filters
        await using var db = ctx.NewContext();
        Assert.False(await db.KassenBuchungen.IgnoreQueryFilters().AnyAsync(b => b.Id == bookingId));
        // the reversal stays readable in the protocol
        Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityType == nameof(KassenBuchung) && a.EntityId == bookingId));
    }

    [Fact]
    public async Task CancelPayment_RequiresAPaidRequest_AndLeavesTheApprovalIntact()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        // a stale page must not be able to flip a still-open request via the Approved transition edge
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CancelPaymentAsync(created.Id, Leader()));

        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CancelPaymentAsync(created.Id, Leader()));

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Approved, reloaded!.Status);
        Assert.Equal(1_000m, reloaded.ApprovedSubsidy);
        Assert.NotNull(reloaded.BudgetYear);
    }

    [Fact]
    public async Task CancelPayment_RejectedRequestStaysRejected()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: false, new FinancingDecisionInput(), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.CancelPaymentAsync(created.Id, Leader()));

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Rejected, reloaded!.Status);
        Assert.Null(reloaded.ApprovedSubsidy);
    }

    [Fact]
    public async Task Pay_RecordsTheStageChangeAndTheAmountInOneAuditRow()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 2)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        await h.Svc.PayAsync(created.Id, Leader());

        await using var db = ctx.NewContext();
        // the payout stamps the request via ExecuteUpdate, which bypasses the audit interceptor
        var audit = Assert.Single(await db.AuditLogs
            .Where(a => a.EntityType == nameof(FinancingRequest) && a.EntityId == created.Id)
            .ToListAsync());
        Assert.Contains("Ausgezahlt", audit.ChangesJson);
        Assert.Contains("Status", audit.ChangesJson);
    }

    [Fact]
    public async Task CancelPayment_ThenPayAgain_Works()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await h.Svc.PayAsync(created.Id, Leader());
        await h.Svc.CancelPaymentAsync(created.Id, Leader());

        await h.Svc.PayAsync(created.Id, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Paid, reloaded!.Status);
        Assert.Equal(4_000m, await h.Kasse.GetBalanceAsync(KassenKonto.Gruengeld));
    }

    // ---- delete / trash ----

    [Fact]
    public async Task Delete_OwnOpenRequest_IsAllowed_ForeignOneIsNot()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Svc.DeleteAsync(created.Id, Other()));
        await h.Svc.DeleteAsync(created.Id, Owner());

        await using var db = ctx.NewContext();
        Assert.Empty(await db.FinancingRequests.ToListAsync());
    }

    [Fact]
    public async Task Delete_PaidRequest_Throws()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        await FundAsync(h.Kasse, 5_000m);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());
        await h.Svc.PayAsync(created.Id, Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Svc.DeleteAsync(created.Id, Leader()));
    }

    [Fact]
    public async Task Trash_ListsOnlyDeleted_AndRestoreClearsTheFlag()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());

        Assert.Empty(await h.Svc.GetTrashAsync());

        // the soft-delete interceptor is not wired in the test context, so mark the row directly
        await using (var db = ctx.NewContext())
        {
            var row = await db.FinancingRequests.FirstAsync(r => r.Id == created.Id);
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var trash = await h.Svc.GetTrashAsync();
        Assert.Single(trash);
        Assert.Equal(created.CaseNumber, trash[0].CaseNumber);

        await h.Svc.RestoreAsync(created.Id, Leader());
        Assert.Empty(await h.Svc.GetTrashAsync());
        Assert.NotNull(await h.Svc.GetAsync(created.Id, Leader()));
    }

    [Fact]
    public async Task Restore_ReopensAnApprovalWhoseBudgetMonthHasPassed()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        // soft-delete it and back-date the reservation to a month that is closed by now
        var (year, month) = FinancingPeriod.Current();
        var (priorYear, priorMonth) = FinancingPeriod.Previous(year, month);
        await using (var db = ctx.NewContext())
        {
            var row = await db.FinancingRequests.FirstAsync(r => r.Id == created.Id);
            row.BudgetYear = priorYear;
            row.BudgetMonth = priorMonth;
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await h.Svc.RestoreAsync(created.Id, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Requested, reloaded!.Status);
        Assert.Null(reloaded.ApprovedSubsidy);
        Assert.Null(reloaded.BudgetYear);
        Assert.Null(reloaded.Lines[0].ApprovedQuantity);
    }

    [Fact]
    public async Task Restore_KeepsAnApprovalChargedToTheRunningMonth()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var created = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.DecideAsync(created.Id, approved: true, new FinancingDecisionInput(), Leader());

        await using (var db = ctx.NewContext())
        {
            var row = await db.FinancingRequests.FirstAsync(r => r.Id == created.Id);
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await h.Svc.RestoreAsync(created.Id, Leader());

        var reloaded = await h.Svc.GetAsync(created.Id, Leader());
        Assert.Equal(FinancingStatus.Approved, reloaded!.Status);
        Assert.Equal(1_000m, reloaded.ApprovedSubsidy);
    }

    [Fact]
    public async Task OpenCount_CountsOnlyRequested()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, Item("a", 1_000m));
        var h = Build(ctx);
        var first = await h.Svc.CreateAsync(Cart("Grund", ("a", 1)), Owner());
        await h.Svc.CreateAsync(Cart("Grund 2", ("a", 1)), Owner());
        Assert.Equal(2, await h.Svc.GetOpenCountAsync());

        await h.Svc.DecideAsync(first.Id, approved: true, new FinancingDecisionInput(), Leader());
        Assert.Equal(1, await h.Svc.GetOpenCountAsync());
    }
}
