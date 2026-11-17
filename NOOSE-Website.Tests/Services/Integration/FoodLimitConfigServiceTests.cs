using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FoodLimitConfigService"/>: defaults, validation, leadership gate.</summary>
public sealed class FoodLimitConfigServiceTests
{
    private const string SettingKey = "VerpflegungsLimits";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("s").WithRank(Rank.SpecialAgentInCharge).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private static FoodLimitConfigService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static async Task StoreAsync(SqliteTestContext ctx, string? raw)
    {
        await using var db = ctx.NewContext();
        db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = raw });
        await db.SaveChangesAsync();
    }

    private static FoodLimitConfig Custom(int food, int drink) => new()
    {
        Ranks = new Dictionary<string, FoodRankLimit>
        {
            [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = food, DrinkPerWeek = drink },
        },
    };

    [Fact]
    public async Task NoRow_YieldsTheDefaults_ForEveryRank()
    {
        using var ctx = new SqliteTestContext();

        var config = await Build(ctx).GetAsync();

        foreach (var rank in RankDisplay.All)
        {
            Assert.Equal(10, config.For(rank).FoodPerWeek);
            Assert.Equal(10, config.For(rank).DrinkPerWeek);
        }
        Assert.Empty(config.Divisions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ kaputt")]
    [InlineData("null")]
    public async Task EmptyOrInvalidJson_FallsBackToTheDefaults(string raw)
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, raw);

        var config = await Build(ctx).GetEditableAsync();

        Assert.Equal(10, config.For(Rank.SpecialAgent).FoodPerWeek);
    }

    [Fact]
    public async Task StoredNegativeLimit_FallsBackToTheDefaults()
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, JsonSerializer.Serialize(Custom(-3, 2)));

        var config = await Build(ctx).GetAsync();

        Assert.Equal(10, config.For(Rank.SpecialAgent).FoodPerWeek);
    }

    [Fact]
    public async Task StoredUnknownDivision_FallsBackToTheDefaults()
    {
        using var ctx = new SqliteTestContext();
        var config = Custom(4, 4);
        config.Divisions["99"] = new() { [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = 1 } };
        await StoreAsync(ctx, JsonSerializer.Serialize(config));

        Assert.Equal(10, (await Build(ctx).GetAsync()).For(Rank.SpecialAgent).FoodPerWeek);
    }

    [Fact]
    public async Task Save_RoundTrips_AndRefreshesTheCache()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        // warm the cache with the defaults first
        Assert.Equal(10, (await svc.GetAsync()).For(Rank.SpecialAgent).FoodPerWeek);

        var custom = Custom(5, 7);
        custom.Divisions[FoodLimitConfig.DivisionKey(Division.Hrt)] = new()
        {
            [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = 12, DrinkPerWeek = 14 },
        };
        await svc.SaveAsync(custom, Leader());

        var loaded = await svc.GetAsync();
        Assert.Equal((5, 7), (loaded.For(Rank.SpecialAgent).FoodPerWeek, loaded.For(Rank.SpecialAgent).DrinkPerWeek));
        Assert.Equal(12, loaded.For(Rank.SpecialAgent, Division.Hrt).FoodPerWeek);
        // a missing key means no allowance
        Assert.Equal(0, loaded.For(Rank.Director).FoodPerWeek);

        await using var db = ctx.NewContext();
        Assert.Equal(1, await db.AuditLogs.CountAsync(l => l.EntityType == FoodLimitConfigService.AuditType));
    }

    [Fact]
    public async Task Save_Twice_UpdatesTheSingleRow()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SaveAsync(Custom(1, 1), Leader());
        await svc.SaveAsync(Custom(2, 2), Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(1, await db.SystemSettings.CountAsync(s => s.Key == SettingKey));
        Assert.Equal(2, (await svc.GetEditableAsync()).For(Rank.SpecialAgent).FoodPerWeek);
    }

    [Fact]
    public async Task Save_RejectsNegativeLimits()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(Custom(-1, 0), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(Custom(0, -1), Leader()));
        var division = Custom(1, 1);
        division.Divisions[FoodLimitConfig.DivisionKey(Division.Ctd)] = new()
        {
            [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = -2 },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(division, Leader()));

        await using var db = ctx.NewContext();
        Assert.False(await db.SystemSettings.AnyAsync(s => s.Key == SettingKey));
    }

    [Fact]
    public async Task Save_RequiresLeadershipWithWriteAccess()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(Custom(1, 1), Senior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(Custom(1, 1), OnlyReader()));
    }
}
