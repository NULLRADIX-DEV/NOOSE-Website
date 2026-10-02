using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for ThreatScoreConfigService against in-memory SQLite.</summary>
public sealed class ThreatScoreConfigServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private ThreatScoreConfigService NewService() => new(_ctx.Factory, _cache);

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ThreatScoreConfiguration Valid(double halfLifeDays)
    {
        var c = ThreatScoreConfiguration.Default();
        c.HalfLifeDays = halfLifeDays;
        return c;
    }

    private void SeedConfigRow(ThreatScoreConfiguration config)
    {
        using var db = _ctx.NewContext();
        db.ThreatScoreConfigs.Add(new ThreatScoreConfig
        {
            Id = ThreatScoreConfig.GlobalId,
            Json = JsonSerializer.Serialize(config, ThreatScoreService.JsonOptions),
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _ctx.Dispose();
        _cache.Dispose();
    }

    // ---------------------------------------------------------------- GetAsync

    [Fact]
    public async Task GetAsync_ReturnsCodeDefault_WhenNoRowExists()
    {
        var svc = NewService();

        var config = await svc.GetAsync();

        Assert.Equal(90.0, config.HalfLifeDays);
        Assert.Equal(55.0, config.CapS1);
        Assert.Equal(50, config.TriageThreshold);
    }

    [Fact]
    public async Task GetAsync_ReturnsStoredConfig_WhenRowExists()
    {
        SeedConfigRow(Valid(halfLifeDays: 120.0));
        var svc = NewService();

        var config = await svc.GetAsync();

        Assert.Equal(120.0, config.HalfLifeDays);
    }

    [Fact]
    public async Task GetAsync_ServesCachedResult_IgnoringLaterDbChanges()
    {
        var svc = NewService();
        var first = await svc.GetAsync(); // populates cache with defaults
        Assert.Equal(90.0, first.HalfLifeDays);

        // insert a row behind the cache
        SeedConfigRow(Valid(halfLifeDays: 200.0));

        var second = await svc.GetAsync();
        Assert.Equal(90.0, second.HalfLifeDays); // still the cached default
    }

    [Fact]
    public async Task GetAsync_ReturnsDefault_WhenJsonMalformed()
    {
        using (var db = _ctx.NewContext())
        {
            db.ThreatScoreConfigs.Add(new ThreatScoreConfig
            {
                Id = ThreatScoreConfig.GlobalId,
                Json = "{ this is not valid json",
            });
            db.SaveChanges();
        }
        var svc = NewService();

        var config = await svc.GetAsync();

        Assert.Equal(90.0, config.HalfLifeDays); // fell back to default
    }

    // ---------------------------------------------------------------- GetEditableAsync

    [Fact]
    public async Task GetEditableAsync_ReturnsCodeDefault_WhenNoRowExists()
    {
        var svc = NewService();

        var config = await svc.GetEditableAsync();

        Assert.Equal(90.0, config.HalfLifeDays);
        Assert.Equal(40.0, config.CapP1);
    }

    [Fact]
    public async Task GetEditableAsync_ReturnsStoredConfig_WhenRowExists()
    {
        SeedConfigRow(Valid(halfLifeDays: 133.0));
        var svc = NewService();

        var config = await svc.GetEditableAsync();

        Assert.Equal(133.0, config.HalfLifeDays);
    }

    [Fact]
    public async Task GetEditableAsync_AlwaysFresh_ReflectsLaterDbChanges()
    {
        var svc = NewService();
        var first = await svc.GetEditableAsync(); // no row -> default, and NOT cached
        Assert.Equal(90.0, first.HalfLifeDays);

        SeedConfigRow(Valid(halfLifeDays: 150.0));

        var second = await svc.GetEditableAsync();
        Assert.Equal(150.0, second.HalfLifeDays); // fresh read reflects the new row
    }

    // ---------------------------------------------------------------- SaveAsync

    [Fact]
    public async Task SaveAsync_InsertsRow_WhenNoneExists()
    {
        var svc = NewService();

        await svc.SaveAsync(Valid(halfLifeDays: 111.0), Leader());

        using var db = _ctx.NewContext();
        var row = await db.ThreatScoreConfigs.SingleAsync(k => k.Id == ThreatScoreConfig.GlobalId);
        Assert.False(string.IsNullOrWhiteSpace(row.Json));
        var stored = JsonSerializer.Deserialize<ThreatScoreConfiguration>(row.Json!, ThreatScoreService.JsonOptions);
        Assert.NotNull(stored);
        Assert.Equal(111.0, stored!.HalfLifeDays);
    }

    [Fact]
    public async Task SaveAsync_UpdatesRow_WhenAlreadyExists()
    {
        SeedConfigRow(Valid(halfLifeDays: 90.0));
        var svc = NewService();

        await svc.SaveAsync(Valid(halfLifeDays: 222.0), Leader());

        using var db = _ctx.NewContext();
        var rows = await db.ThreatScoreConfigs.ToListAsync();
        Assert.Single(rows); // updated in place, no duplicate row
        var stored = JsonSerializer.Deserialize<ThreatScoreConfiguration>(rows[0].Json!, ThreatScoreService.JsonOptions);
        Assert.Equal(222.0, stored!.HalfLifeDays);
    }

    [Fact]
    public async Task SaveAsync_ClearsCache_SoNextGetSeesNewValue()
    {
        var svc = NewService();
        var before = await svc.GetAsync(); // seeds cache with defaults
        Assert.Equal(90.0, before.HalfLifeDays);

        await svc.SaveAsync(Valid(halfLifeDays: 175.0), Leader());

        var after = await svc.GetAsync();
        Assert.Equal(175.0, after.HalfLifeDays); // cache was invalidated
    }

    [Fact]
    public async Task SaveAsync_ThrowsUnauthorized_WhenActorNotLeadership()
    {
        var svc = NewService();
        var junior = ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.JuniorAgent).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SaveAsync(Valid(halfLifeDays: 99.0), junior));

        using var db = _ctx.NewContext();
        Assert.False(await db.ThreatScoreConfigs.AnyAsync()); // nothing persisted
    }

    [Fact]
    public async Task SaveAsync_ThrowsUnauthorized_ForDemoVisitor()
    {
        var svc = NewService();
        // rank Director like DemoIdentity: leadership by rank, but read-only
        var demo = ClaimsPrincipalBuilder.Agent("demo-agent").WithRank(Rank.Director).AsDemo().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SaveAsync(Valid(halfLifeDays: 99.0), demo));

        using var db = _ctx.NewContext();
        Assert.False(await db.ThreatScoreConfigs.AnyAsync()); // nothing persisted
    }

    [Fact]
    public async Task SaveAsync_ThrowsInvalidOperation_WhenConfigInvalid()
    {
        var svc = NewService();
        var invalid = ThreatScoreConfiguration.Default();
        invalid.HalfLifeDays = 0; // violates the Positive() invariant

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SaveAsync(invalid, Leader()));

        using var db = _ctx.NewContext();
        Assert.False(await db.ThreatScoreConfigs.AnyAsync()); // validation failed before persist
    }

    // ---------------------------------------------------------------- Validate (static)

    [Fact]
    public void Validate_DoesNotThrow_ForDefaultConfig()
    {
        ThreatScoreConfigService.Validate(ThreatScoreConfiguration.Default());
    }

    [Fact]
    public void Validate_Throws_WhenHalfLifeDaysNotPositive()
    {
        var c = ThreatScoreConfiguration.Default();
        c.HalfLifeDays = 0;

        Assert.Throws<InvalidOperationException>(() => ThreatScoreConfigService.Validate(c));
    }

    [Fact]
    public void Validate_Throws_WhenFactionCapsDoNotSumTo100()
    {
        var c = ThreatScoreConfiguration.Default();
        c.CapS1 = 60.0; // S1..S4 now sum to 105, not 100

        Assert.Throws<InvalidOperationException>(() => ThreatScoreConfigService.Validate(c));
    }

    [Fact]
    public void Validate_Throws_WhenPersonCapsDoNotSumTo100()
    {
        var c = ThreatScoreConfiguration.Default();
        c.CapP5 = 20.0; // P1..P5 now sum to 112, not 100

        Assert.Throws<InvalidOperationException>(() => ThreatScoreConfigService.Validate(c));
    }

    [Fact]
    public void Validate_Throws_WhenSeverityTiersNotMonotone()
    {
        var c = ThreatScoreConfiguration.Default();
        c.KindWeightMedium = 5.0; // medium (5) > heavy (3) breaks heavy >= medium

        Assert.Throws<InvalidOperationException>(() => ThreatScoreConfigService.Validate(c));
    }

    [Fact]
    public void Validate_Throws_WhenTriageThresholdOutOfRange()
    {
        var c = ThreatScoreConfiguration.Default();
        c.TriageThreshold = 150; // must be 0..100

        Assert.Throws<InvalidOperationException>(() => ThreatScoreConfigService.Validate(c));
    }

    [Fact]
    public void Validate_Throws_WhenS2SubCapsDoNotSumToCapS2()
    {
        var c = ThreatScoreConfiguration.Default();
        c.CapWeapons = 10.0; // S2 sub-caps no longer sum to CapS2 (22)

        Assert.Throws<InvalidOperationException>(() => ThreatScoreConfigService.Validate(c));
    }
}
