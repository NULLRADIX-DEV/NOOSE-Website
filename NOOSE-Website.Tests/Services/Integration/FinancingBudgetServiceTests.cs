using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FinancingBudgetService"/>: rank base, individual override, carry-over chain.</summary>
public sealed class FinancingBudgetServiceTests
{
    private const string AgentId = "agent-b";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static FinancingBudgetService Build(SqliteTestContext ctx, FinancingBudgetConfig? config = null)
    {
        var configService = Substitute.For<IFinancingConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(config ?? FinancingBudgetConfig.Default());
        return new FinancingBudgetService(ctx.Factory, configService);
    }

    private static FinancingBudgetConfig Config(Rank rank, decimal amount, int carryPercent) => new()
    {
        Ranks = new Dictionary<string, FinancingRankBudget>
        {
            [FinancingBudgetConfig.RankKey(rank)] = new() { BaseMonthly = amount, CarryOverPercent = carryPercent },
        },
    };

    private static async Task SeedAgentAsync(SqliteTestContext ctx, Rank? rank = Rank.SpecialAgent, decimal? over = null)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(AgentId, rank ?? Rank.SpecialAgent, configure: a =>
        {
            a.Rank = rank;
            a.FinancingBudgetOverride = over;
        }));
        await db.SaveChangesAsync();
    }

    /// <summary>Adds an approved request charged to the given budget month.</summary>
    private static async Task ChargeAsync(SqliteTestContext ctx, int year, int month, decimal subsidy,
        FinancingStatus status = FinancingStatus.Approved, bool deleted = false)
    {
        await using var db = ctx.NewContext();
        db.FinancingRequests.Add(new FinancingRequest
        {
            CaseNumber = $"FIB-FIN-{year}-{Guid.NewGuid().ToString()[..4]}",
            AgentId = AgentId,
            Status = status,
            Justification = "Test",
            RequestedGross = subsidy,
            RequestedSubsidy = subsidy,
            ApprovedSubsidy = subsidy,
            BudgetYear = year,
            BudgetMonth = month,
            IsDeleted = deleted,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        });
        await db.SaveChangesAsync();
    }

    private static async Task ClosePeriodAsync(SqliteTestContext ctx, int year, int month, decimal carryOut)
    {
        await using var db = ctx.NewContext();
        db.FinancingBudgetPeriods.Add(new FinancingBudgetPeriod
        {
            AgentId = AgentId,
            Year = year,
            Month = month,
            BaseBudget = 1_000m,
            CarryIn = 0m,
            Consumed = 0m,
            CarryOut = carryOut,
            CarryPercent = 50,
            ClosedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task BaseBudget_ComesFromTheRank()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SpecialAgent);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 42_000m, 25));

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(42_000m, status.BaseBudget);
        Assert.Equal(25, status.CarryPercent);
        Assert.False(status.IsOverride);
        Assert.Equal(42_000m, status.Remaining);
    }

    [Fact]
    public async Task UnrankedAgent_GetsNothing()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, rank: null);
        var svc = Build(ctx);

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(0m, status.BaseBudget);
        Assert.Equal(0m, status.Remaining);
    }

    [Fact]
    public async Task Override_BeatsTheRankDefault()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SpecialAgent, over: 5_000m);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 42_000m, 0));

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(5_000m, status.BaseBudget);
        Assert.True(status.IsOverride);
    }

    [Fact]
    public async Task Consumed_CountsApprovedAndPaid_ButNotRejectedWithdrawnOrDeleted()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 10_000m, 0));
        var (year, month) = FinancingPeriod.Current();

        await ChargeAsync(ctx, year, month, 1_000m, FinancingStatus.Approved);
        await ChargeAsync(ctx, year, month, 2_000m, FinancingStatus.Paid);
        await ChargeAsync(ctx, year, month, 4_000m, FinancingStatus.Rejected);
        await ChargeAsync(ctx, year, month, 8_000m, FinancingStatus.Withdrawn);
        await ChargeAsync(ctx, year, month, 500m, FinancingStatus.Approved, deleted: true);

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(3_000m, status.Consumed);
        Assert.Equal(7_000m, status.Remaining);
    }

    [Fact]
    public async Task Consumed_IgnoresOtherBudgetMonths()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 10_000m, 0));
        var (year, month) = FinancingPeriod.Current();
        var (priorYear, priorMonth) = FinancingPeriod.Previous(year, month);

        await ChargeAsync(ctx, year, month, 1_000m);
        await ChargeAsync(ctx, priorYear, priorMonth, 9_000m);

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(1_000m, status.Consumed);
    }

    [Fact]
    public async Task Remaining_MayGoNegative_AfterADeliberateOverrun()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 1_000m, 0));
        var (year, month) = FinancingPeriod.Current();
        await ChargeAsync(ctx, year, month, 2_500m);

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(-1_500m, status.Remaining);
    }

    [Fact]
    public async Task CarryIn_ComesFromTheDirectPredecessorPeriod()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 1_000m, 50));
        var (year, month) = FinancingPeriod.Current();
        var (priorYear, priorMonth) = FinancingPeriod.Previous(year, month);
        await ClosePeriodAsync(ctx, priorYear, priorMonth, carryOut: 400m);

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(400m, status.CarryIn);
        Assert.Equal(1_400m, status.Available);
    }

    [Fact]
    public async Task CarryIn_IgnoresAPeriodTwoMonthsBack()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 1_000m, 0));
        var (year, month) = FinancingPeriod.Current();
        var (priorYear, priorMonth) = FinancingPeriod.Previous(year, month);
        var (olderYear, olderMonth) = FinancingPeriod.Previous(priorYear, priorMonth);
        await ClosePeriodAsync(ctx, olderYear, olderMonth, carryOut: 900m);

        var status = await svc.GetStatusAsync(AgentId);

        // the gap month is closed with a zero-percent carry, so nothing reaches the running month
        Assert.Equal(0m, status.CarryIn);
    }

    [Fact]
    public async Task NoHistory_MeansNoPhantomCarryOver()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 1_000m, 50));

        var status = await svc.GetStatusAsync(AgentId);

        Assert.Equal(0m, status.CarryIn);
        await using var db = ctx.NewContext();
        Assert.Empty(await db.FinancingBudgetPeriods.ToListAsync());
    }

    [Fact]
    public async Task ElapsedMonth_IsClosedOnceAndCarriesItsRestForward()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx, Config(Rank.SpecialAgent, 1_000m, 50));
        var (year, month) = FinancingPeriod.Current();
        var (priorYear, priorMonth) = FinancingPeriod.Previous(year, month);
        // 400 consumed last month → rest 600 → 50 % = 300 carried in
        await ChargeAsync(ctx, priorYear, priorMonth, 400m);

        var status = await svc.GetStatusAsync(AgentId);
        Assert.Equal(300m, status.CarryIn);
        Assert.Equal(1_300m, status.Available);

        await using (var db = ctx.NewContext())
        {
            var period = Assert.Single(await db.FinancingBudgetPeriods.ToListAsync());
            Assert.Equal(priorYear, period.Year);
            Assert.Equal(priorMonth, period.Month);
            Assert.Equal(400m, period.Consumed);
            Assert.Equal(300m, period.CarryOut);
            Assert.Equal(Rank.SpecialAgent, period.RankAtClose);
        }

        // idempotent: a second read must not write another row nor change the numbers
        var again = await svc.GetStatusAsync(AgentId);
        Assert.Equal(300m, again.CarryIn);
        await using (var db = ctx.NewContext())
        {
            Assert.Single(await db.FinancingBudgetPeriods.ToListAsync());
        }
    }

    [Fact]
    public async Task ClosedPeriod_StaysFrozen_WhenTheRuleChangesLater()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var (year, month) = FinancingPeriod.Current();
        var (priorYear, priorMonth) = FinancingPeriod.Previous(year, month);
        await ChargeAsync(ctx, priorYear, priorMonth, 400m);

        // close under the original rule
        await Build(ctx, Config(Rank.SpecialAgent, 1_000m, 50)).GetStatusAsync(AgentId);

        // the rule doubles afterwards; the stored carry-over must not move
        var status = await Build(ctx, Config(Rank.SpecialAgent, 2_000m, 100)).GetStatusAsync(AgentId);

        Assert.Equal(300m, status.CarryIn);
        Assert.Equal(2_000m, status.BaseBudget);
    }

    [Fact]
    public async Task SetOverride_IsAuditedAgainstThePersonnelRecord()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx);

        await svc.SetOverrideAsync(AgentId, 7_500m, Leader());

        await using (var db = ctx.NewContext())
        {
            Assert.Equal(7_500m, (await db.Users.FirstAsync(a => a.Id == AgentId)).FinancingBudgetOverride);
            var audit = Assert.Single(await db.AuditLogs.Where(a => a.EntityId == AgentId).ToListAsync());
            Assert.Equal("Agent", audit.EntityType);
            Assert.Contains("Finanzierungsbudget", audit.ChangesJson);
        }

        await svc.SetOverrideAsync(AgentId, null, Leader());
        await using (var db = ctx.NewContext())
        {
            Assert.Null((await db.Users.FirstAsync(a => a.Id == AgentId)).FinancingBudgetOverride);
        }
    }

    [Fact]
    public async Task SetOverride_RejectsNegativeAmountsAndNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetOverrideAsync(AgentId, -1m, Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SetOverrideAsync(AgentId, 100m, Junior()));
    }

    [Fact]
    public async Task GetAllStatus_ListsActiveInternalAgents_AndNeverTeamLeads()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("active-1", Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("pending-1", Rank.SpecialAgent, AgentStatus.Pending));
            db.Users.Add(Seed.Agent("partner-1", Rank.SpecialAgent,
                configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            // read-only supervision is RP-wide invisible and must not show up in any roster
            db.Users.Add(Seed.Agent("teamlead-1", Rank.SpecialAgent, configure: a => a.IsTeamLead = true));
            // not even with the admin flag on top
            db.Users.Add(Seed.Agent("teamlead-admin", Rank.Director, configure: a =>
            {
                a.IsTeamLead = true;
                a.IsAdmin = true;
            }));
            await db.SaveChangesAsync();
        }
        var svc = Build(ctx);

        var rows = await svc.GetAllStatusAsync();

        Assert.Single(rows);
        Assert.Equal("active-1", rows[0].AgentId);
    }
}
