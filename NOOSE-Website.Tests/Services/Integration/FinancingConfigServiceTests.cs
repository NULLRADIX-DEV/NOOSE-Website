using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FinancingConfigService"/>: defaults, validation, cache behaviour.</summary>
public sealed class FinancingConfigServiceTests
{
    private const string SettingKey = "FinanzierungsBudgets";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private static FinancingConfigService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static async Task StoreAsync(SqliteTestContext ctx, string? raw)
    {
        await using var db = ctx.NewContext();
        db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = raw });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoRow_YieldsTheCodeDefaults()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var config = await svc.GetAsync();

        Assert.Equal(25_000m, config.For(Rank.Trainee).BaseMonthly);
        Assert.Equal(0, config.For(Rank.Trainee).CarryOverPercent);
        Assert.Equal(250_000m, config.For(Rank.Director).BaseMonthly);
        Assert.Equal(50, config.For(Rank.Director).CarryOverPercent);
    }

    [Fact]
    public async Task DefaultsCoverEveryRank()
    {
        using var ctx = new SqliteTestContext();
        var config = await Build(ctx).GetAsync();

        foreach (var rank in RankDisplay.All)
        {
            Assert.True(config.For(rank).BaseMonthly > 0, $"{rank} hat kein Budget.");
        }
    }

    /// <summary>The numbers the data migration writes for the two new levels: the same ones, or code and database diverge.</summary>
    [Fact]
    public async Task DefaultsPutTheNewLevelsBetweenTheOldOnesAndRiseWithTheLadder()
    {
        using var ctx = new SqliteTestContext();
        var config = await Build(ctx).GetAsync();

        Assert.Equal(150_000m, config.For(Rank.SpecialAgentInCharge).BaseMonthly);
        Assert.Equal(165_000m, config.For(Rank.SectionChief).BaseMonthly);
        Assert.Equal(180_000m, config.For(Rank.AssistantDirector).BaseMonthly);
        Assert.Equal(200_000m, config.For(Rank.DeputyDirector).BaseMonthly);
        Assert.Equal(50, config.For(Rank.SectionChief).CarryOverPercent);
        Assert.Equal(50, config.For(Rank.AssistantDirector).CarryOverPercent);
        Assert.Equal(RankDisplay.All.Count, FinancingBudgetConfig.Default().Ranks.Count);
        var budgets = RankDisplay.All.Select(r => config.For(r).BaseMonthly).ToList();
        Assert.Equal(budgets.OrderBy(b => b).ToList(), budgets);
    }

    [Fact]
    public void TheDefaultConfigPassesItsOwnValidation()
        => Assert.Null(Record.Exception(() => FinancingConfigService.Validate(FinancingBudgetConfig.Default())));

    [Fact]
    public async Task UnknownRankKey_FallsBackToNothing()
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, JsonSerializer.Serialize(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.Director)] = new() { BaseMonthly = 1m, CarryOverPercent = 0 },
            },
        }));

        var config = await Build(ctx).GetAsync();

        Assert.Equal(1m, config.For(Rank.Director).BaseMonthly);
        Assert.Equal(0m, config.For(Rank.Trainee).BaseMonthly);
    }

    [Fact]
    public void DivisionTable_BeatsTheBaseTable_AndFallsBackPerRank()
    {
        var config = new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.SpecialAgent)] = new() { BaseMonthly = 50_000m, CarryOverPercent = 0 },
                [FinancingBudgetConfig.RankKey(Rank.SeniorSpecialAgent)] = new() { BaseMonthly = 75_000m, CarryOverPercent = 25 },
            },
            Divisions = new Dictionary<string, Dictionary<string, FinancingRankBudget>>
            {
                [FinancingBudgetConfig.DivisionKey(Division.Hrt)] = new()
                {
                    [FinancingBudgetConfig.RankKey(Rank.SpecialAgent)] = new() { BaseMonthly = 80_000m, CarryOverPercent = 10 },
                },
            },
        };

        Assert.Equal(80_000m, config.For(Rank.SpecialAgent, Division.Hrt).BaseMonthly);
        Assert.Equal(10, config.For(Rank.SpecialAgent, Division.Hrt).CarryOverPercent);
        Assert.Equal(75_000m, config.For(Rank.SeniorSpecialAgent, Division.Hrt).BaseMonthly);
        Assert.Equal(50_000m, config.For(Rank.SpecialAgent, Division.Cid).BaseMonthly);
        Assert.Equal(50_000m, config.For(Rank.SpecialAgent, null).BaseMonthly);
        Assert.Equal(0m, config.For(null, Division.Hrt).BaseMonthly);
    }

    [Fact]
    public void Validate_ChecksTheDivisionTablesToo()
    {
        var negative = new FinancingBudgetConfig
        {
            Divisions = new Dictionary<string, Dictionary<string, FinancingRankBudget>>
            {
                [FinancingBudgetConfig.DivisionKey(Division.Ctd)] = new()
                {
                    [FinancingBudgetConfig.RankKey(Rank.Trainee)] = new() { BaseMonthly = -1m },
                },
            },
        };
        var unknown = new FinancingBudgetConfig
        {
            Divisions = new Dictionary<string, Dictionary<string, FinancingRankBudget>> { ["99"] = new() },
        };

        Assert.Throws<InvalidOperationException>(() => FinancingConfigService.Validate(negative));
        Assert.Throws<InvalidOperationException>(() => FinancingConfigService.Validate(unknown));
    }

    [Fact]
    public async Task Save_RoundTripsTheDivisionTables()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var config = FinancingBudgetConfig.Default();
        config.Divisions[FinancingBudgetConfig.DivisionKey(Division.Hrt)] = new()
        {
            [FinancingBudgetConfig.RankKey(Rank.SpecialAgent)] = new() { BaseMonthly = 90_000m, CarryOverPercent = 30 },
        };

        await svc.SaveAsync(config, Leader());
        var stored = await svc.GetEditableAsync();

        Assert.Equal(90_000m, stored.For(Rank.SpecialAgent, Division.Hrt).BaseMonthly);
        Assert.Equal(50_000m, stored.For(Rank.SpecialAgent, Division.Cid).BaseMonthly);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BrokenOrEmptyJson_FallsBackToTheDefaults(string raw)
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, raw);

        var config = await Build(ctx).GetAsync();

        Assert.Equal(25_000m, config.For(Rank.Trainee).BaseMonthly);
    }

    [Fact]
    public async Task StoredConfigViolatingTheInvariants_FallsBackToTheDefaults()
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, JsonSerializer.Serialize(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.Director)] = new() { BaseMonthly = -5m, CarryOverPercent = 0 },
            },
        }));

        var config = await Build(ctx).GetAsync();

        Assert.Equal(250_000m, config.For(Rank.Director).BaseMonthly);
    }

    [Fact]
    public async Task Save_PersistsAndWritesAConfigAuditRow()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SaveAsync(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.SpecialAgent)] = new() { BaseMonthly = 12_345m, CarryOverPercent = 10 },
            },
        }, Leader());

        Assert.Equal(12_345m, (await svc.GetAsync()).For(Rank.SpecialAgent).BaseMonthly);

        await using var db = ctx.NewContext();
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal("FinancingBudgetConfig", audit.EntityType);
        Assert.Equal("global", audit.EntityId);
        Assert.Contains("Finanzierungs-Budgets", audit.ChangesJson);
    }

    [Fact]
    public async Task Save_EvictsTheCache()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        // warm the cache with the defaults
        Assert.Equal(25_000m, (await svc.GetAsync()).For(Rank.Trainee).BaseMonthly);

        await svc.SaveAsync(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.Trainee)] = new() { BaseMonthly = 1m, CarryOverPercent = 0 },
            },
        }, Leader());

        Assert.Equal(1m, (await svc.GetAsync()).For(Rank.Trainee).BaseMonthly);
    }

    [Fact]
    public async Task GetEditable_NeverServesTheCache()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.GetAsync();

        await StoreAsync(ctx, JsonSerializer.Serialize(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.Trainee)] = new() { BaseMonthly = 777m, CarryOverPercent = 0 },
            },
        }));

        Assert.Equal(777m, (await svc.GetEditableAsync()).For(Rank.Trainee).BaseMonthly);
    }

    [Fact]
    public async Task Save_RejectsInvalidValues()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.Director)] = new() { BaseMonthly = -1m, CarryOverPercent = 0 },
            },
        }, Leader()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(new FinancingBudgetConfig
        {
            Ranks = new Dictionary<string, FinancingRankBudget>
            {
                [FinancingBudgetConfig.RankKey(Rank.Director)] = new() { BaseMonthly = 1m, CarryOverPercent = 101 },
            },
        }, Leader()));
    }

    [Fact]
    public async Task Save_DeniedForNonLeadershipAndReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var config = FinancingBudgetConfig.Default();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(config, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(config, OnlyReader()));
    }
}
