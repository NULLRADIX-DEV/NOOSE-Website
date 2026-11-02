using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The NOOSEI quota rules: defaults, validation, the owner-only write gate and the cache.</summary>
public sealed class LlmQuotaConfigServiceTests
{
    private const string SettingKey = "KiKontingente";

    private static ClaimsPrincipal Owner()
        => ClaimsPrincipalBuilder.Agent("owner").WithRank(Rank.Director).AsAiOwner().Build();

    private static LlmQuotaConfigService Build(SqliteTestContext ctx, IMemoryCache? cache = null)
        => new(ctx.Factory, cache ?? new MemoryCache(new MemoryCacheOptions()));

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

        var config = await Build(ctx).GetAsync();

        Assert.Equal(20_000L, config.For(Rank.Trainee).BaseWeekly);
        Assert.Equal(0, config.For(Rank.Trainee).CarryOverPercent);
        Assert.Equal(35_000L, config.For(Rank.SpecialAgent).BaseWeekly);
        Assert.Equal(50_000L, config.For(Rank.SeniorSpecialAgent).BaseWeekly);
        Assert.Equal(25, config.For(Rank.SeniorSpecialAgent).CarryOverPercent);
        Assert.Equal(80_000L, config.For(Rank.SpecialAgentInCharge).BaseWeekly);
        Assert.Equal(120_000L, config.For(Rank.DeputyDirector).BaseWeekly);
        Assert.Equal(200_000L, config.For(Rank.Director).BaseWeekly);
        Assert.Equal(50, config.For(Rank.Director).CarryOverPercent);
    }

    [Fact]
    public async Task DefaultsCoverEveryRank()
    {
        using var ctx = new SqliteTestContext();

        var config = await Build(ctx).GetAsync();

        Assert.All(RankDisplay.All, rank => Assert.True(config.For(rank).BaseWeekly > 0));
    }

    [Fact]
    public async Task UnrankedAgent_GetsNothing()
    {
        using var ctx = new SqliteTestContext();

        var config = await Build(ctx).GetAsync();

        Assert.Equal(0L, config.For(null).BaseWeekly);
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BrokenOrEmptyJson_FallsBackToTheDefaults(string raw)
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, raw);

        var config = await Build(ctx).GetAsync();

        Assert.Equal(35_000L, config.For(Rank.SpecialAgent).BaseWeekly);
    }

    [Fact]
    public async Task StoredConfigViolatingTheInvariants_FallsBackToTheDefaults()
    {
        using var ctx = new SqliteTestContext();
        var broken = new LlmQuotaConfig
        {
            Ranks = new Dictionary<string, LlmRankQuota>
            {
                [LlmQuotaConfig.RankKey(Rank.SpecialAgent)] = new() { BaseWeekly = -5, CarryOverPercent = 900 },
            },
        };
        await StoreAsync(ctx, JsonSerializer.Serialize(broken));

        var config = await Build(ctx).GetAsync();

        Assert.Equal(35_000L, config.For(Rank.SpecialAgent).BaseWeekly);
    }

    [Fact]
    public async Task Save_PersistsAndWritesAConfigAuditRow()
    {
        using var ctx = new SqliteTestContext();
        var config = LlmQuotaConfig.Default();
        config.Ranks[LlmQuotaConfig.RankKey(Rank.Trainee)] = new() { BaseWeekly = 1_234, CarryOverPercent = 10 };

        await Build(ctx).SaveAsync(config, Owner());

        await using var db = ctx.NewContext();
        Assert.NotNull(await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SettingKey));
        var audit = await db.AuditLogs.FirstAsync(a => a.EntityType == LlmQuotaConfigService.AuditType);
        Assert.Equal("global", audit.EntityId);
        Assert.Contains("KI-Kontingente", audit.ChangesJson);
    }

    [Fact]
    public async Task Save_EvictsTheCache()
    {
        using var ctx = new SqliteTestContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = Build(ctx, cache);
        await svc.GetAsync();

        var config = LlmQuotaConfig.Default();
        config.Ranks[LlmQuotaConfig.RankKey(Rank.Trainee)] = new() { BaseWeekly = 4_242, CarryOverPercent = 0 };
        await svc.SaveAsync(config, Owner());

        Assert.Equal(4_242L, (await svc.GetAsync()).For(Rank.Trainee).BaseWeekly);
    }

    [Fact]
    public async Task GetEditable_NeverServesTheCache()
    {
        using var ctx = new SqliteTestContext();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = Build(ctx, cache);
        await svc.GetAsync();

        var stored = LlmQuotaConfig.Default();
        stored.Ranks[LlmQuotaConfig.RankKey(Rank.Trainee)] = new() { BaseWeekly = 777, CarryOverPercent = 0 };
        await StoreAsync(ctx, JsonSerializer.Serialize(stored));

        Assert.Equal(777L, (await svc.GetEditableAsync()).For(Rank.Trainee).BaseWeekly);
    }

    [Fact]
    public async Task Save_RejectsANegativeBaseAndACarryOutsideZeroToHundred()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var negative = LlmQuotaConfig.Default();
        negative.Ranks[LlmQuotaConfig.RankKey(Rank.SpecialAgent)] = new() { BaseWeekly = -1, CarryOverPercent = 0 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(negative, Owner()));

        var tooMuch = LlmQuotaConfig.Default();
        tooMuch.Ranks[LlmQuotaConfig.RankKey(Rank.SpecialAgent)] = new() { BaseWeekly = 1, CarryOverPercent = 101 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SaveAsync(tooMuch, Owner()));
    }

    [Theory]
    [InlineData("spike")]
    [InlineData("burnHours")]
    [InlineData("burstMinutes")]
    [InlineData("similarity")]
    [InlineData("outlierWeeks")]
    public async Task Save_RejectsThresholdsOutsideTheirRanges(string which)
    {
        using var ctx = new SqliteTestContext();
        var config = LlmQuotaConfig.Default();
        switch (which)
        {
            case "spike": config.Anomalies.SpikeFactor = 0.5; break;
            case "burnHours": config.Anomalies.BurnHours = 0; break;
            case "burstMinutes": config.Anomalies.BurstMinutes = 0; break;
            case "similarity": config.Anomalies.BurstSimilarityPercent = 101; break;
            default:
                config.Anomalies.OutlierTrailingWeeks = 3;
                config.Anomalies.OutlierMinWeeks = 5;
                break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).SaveAsync(config, Owner()));
    }

    [Fact]
    public async Task Save_IsRefusedForEveryoneButTheAiOwner()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var leader = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
        var bootstrapAdmin = ClaimsPrincipalBuilder.Agent("adm").WithRank(Rank.Director).AsAdmin().AsBootstrap().Build();
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().AsAiOwner().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(LlmQuotaConfig.Default(), leader));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(LlmQuotaConfig.Default(), bootstrapAdmin));
        // even the owner cannot write while read-only supervision is on
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SaveAsync(LlmQuotaConfig.Default(), supervision));
    }
}
