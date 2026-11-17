using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FoodLimitService"/>: which limit applies and what counts against it.</summary>
public sealed class FoodLimitServiceTests
{
    private static int _caseSeq;

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static FoodLimitService Build(SqliteTestContext ctx, FoodLimitConfig config)
    {
        var configService = Substitute.For<IFoodLimitConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(config);
        return new FoodLimitService(ctx.Factory, configService);
    }

    private static FoodLimitConfig Config() => new()
    {
        Ranks = new Dictionary<string, FoodRankLimit>
        {
            [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = 10, DrinkPerWeek = 8 },
        },
        Divisions = new Dictionary<string, Dictionary<string, FoodRankLimit>>
        {
            [FoodLimitConfig.DivisionKey(Division.Ctd)] = new()
            {
                [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = 3, DrinkPerWeek = 4 },
            },
        },
    };

    private static async Task SeedAgentsAsync(SqliteTestContext ctx, params Agent[] agents)
    {
        await using var db = ctx.NewContext();
        db.Users.AddRange(agents);
        await db.SaveChangesAsync();
    }

    private static FoodRequest Request(string agentId, FoodRequestStatus status, int food, int drink, DateTime? week,
        bool deleted = false)
        => new()
        {
            AgentId = agentId,
            CaseNumber = $"FIB-VER-2026-{Interlocked.Increment(ref _caseSeq):0000}",
            Status = status,
            FoodApproved = food,
            DrinkApproved = drink,
            LimitWeekStart = week,
            IsDeleted = deleted,
        };

    [Fact]
    public async Task BaseTable_AppliesWithoutOverrideOrDivisionTable()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid));
        var svc = Build(ctx, Config());

        var status = await svc.GetStatusAsync("a");

        Assert.Equal((10, 8), (status.FoodLimit, status.DrinkLimit));
        Assert.False(status.FoodIsOverride);
        Assert.False(status.DrinkIsOverride);
        Assert.Equal(FoodWeek.Current(), status.WeekStart);
    }

    [Fact]
    public async Task DivisionTable_BeatsTheBaseTable()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Ctd));
        var svc = Build(ctx, Config());

        var status = await svc.GetStatusAsync("a");

        Assert.Equal((3, 4), (status.FoodLimit, status.DrinkLimit));
    }

    [Fact]
    public async Task Override_BeatsEveryTable_PerKind()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent, configure: a =>
        {
            a.Division = Division.Ctd;
            a.FoodLimitOverride = 20;
        }));
        var svc = Build(ctx, Config());

        var status = await svc.GetStatusAsync("a");

        Assert.Equal(20, status.FoodLimit);
        Assert.True(status.FoodIsOverride);
        // drink has no override, so the division table still applies
        Assert.Equal(4, status.DrinkLimit);
        Assert.False(status.DrinkIsOverride);
    }

    [Fact]
    public async Task UnconfiguredRank_GetsNoAllowance()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.Trainee));
        var svc = Build(ctx, Config());

        var status = await svc.GetStatusAsync("a");

        Assert.Equal((0, 0), (status.FoodLimit, status.DrinkLimit));
    }

    [Fact]
    public async Task Consumption_CountsApprovedAndIssuedOfTheRunningWeekOnly()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent), Seed.Agent("b", Rank.SpecialAgent));
        var week = FoodWeek.Current();
        await using (var db = ctx.NewContext())
        {
            db.FoodRequests.AddRange(
                Request("a", FoodRequestStatus.Approved, 2, 1, week),
                Request("a", FoodRequestStatus.Issued, 1, 3, week),
                // none of these count
                Request("a", FoodRequestStatus.Approved, 5, 5, week.AddDays(-7)),
                Request("a", FoodRequestStatus.Issued, 5, 5, week.AddDays(7)),
                Request("a", FoodRequestStatus.Rejected, 4, 4, week),
                Request("a", FoodRequestStatus.Withdrawn, 4, 4, week),
                Request("a", FoodRequestStatus.Requested, 4, 4, week),
                Request("a", FoodRequestStatus.Approved, 4, 4, week, deleted: true),
                Request("b", FoodRequestStatus.Approved, 6, 6, week));
            await db.SaveChangesAsync();
        }
        var svc = Build(ctx, Config());

        var status = await svc.GetStatusAsync("a");

        Assert.Equal((3, 4), (status.FoodConsumed, status.DrinkConsumed));
        Assert.Equal((7, 4), (status.FoodRemaining, status.DrinkRemaining));
    }

    [Fact]
    public async Task GetAll_ListsActiveInHouseAgents_WithTheirOwnConsumption()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx,
            Seed.Agent("b", Rank.SpecialAgent, configure: a => a.Codename = "Bravo"),
            Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Codename = "Alpha"),
            Seed.Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true),
            Seed.Agent("gone", Rank.SpecialAgent, AgentStatus.Terminated));
        await using (var db = ctx.NewContext())
        {
            db.FoodRequests.Add(Request("b", FoodRequestStatus.Approved, 2, 0, FoodWeek.Current()));
            await db.SaveChangesAsync();
        }
        var svc = Build(ctx, Config());

        var all = await svc.GetAllStatusAsync();

        Assert.Equal(new[] { "Alpha", "Bravo" }, all.Select(s => s.Codename));
        Assert.Equal(0, all[0].FoodConsumed);
        Assert.Equal(2, all[1].FoodConsumed);
    }

    [Fact]
    public async Task SetOverride_StoresAndClears_AndWritesAnAuditRow()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent));
        var svc = Build(ctx, Config());

        await svc.SetOverrideAsync("a", 15, null, Leader());
        var status = await svc.GetStatusAsync("a");
        Assert.Equal((15, 8), (status.FoodLimit, status.DrinkLimit));

        await svc.SetOverrideAsync("a", null, null, Leader());
        Assert.Equal(10, (await svc.GetStatusAsync("a")).FoodLimit);

        await using var db = ctx.NewContext();
        Assert.Equal(2, await db.AuditLogs.CountAsync(l => l.EntityId == "a"));
    }

    [Fact]
    public async Task SetOverride_RejectsNegativeValues()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent));
        var svc = Build(ctx, Config());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetOverrideAsync("a", -1, null, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetOverrideAsync("a", null, -1, Leader()));
    }

    [Fact]
    public async Task SetOverride_RequiresLeadership()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("a", Rank.SpecialAgent));
        var svc = Build(ctx, Config());
        var senior = ClaimsPrincipalBuilder.Agent("s").WithRank(Rank.SpecialAgentInCharge).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SetOverrideAsync("a", 5, 5, senior));
    }
}
