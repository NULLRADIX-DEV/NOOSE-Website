using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="CareerRequirementsService"/>: defaults, permissions, validation, cache.</summary>
public sealed class CareerRequirementsServiceTests
{
    private const string SettingKey = "KarriereAnforderungen";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Hrb()
        => ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.Trainee).AsHrb().Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private static CareerRequirementsService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static CareerRequirementsConfig Config(params CareerRequirement[] items)
        => new() { Items = items.ToList() };

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

        Assert.Equal(7, config.Items.Count);
        Assert.StartsWith("Mindestens 3 Monate durchgängige Zugehörigkeit", config.Items[0].Text);
        Assert.Equal(2, config.Items[0].Alternatives.Count);
        Assert.Contains("Military Police", config.Items[0].Alternatives[0]);
        Assert.Equal("Einschlägige Rechtskenntnisse", config.Items[^1].Text);
        Assert.All(config.Items.Skip(1), item => Assert.Empty(item.Alternatives));
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

        Assert.Equal(7, config.Items.Count);
    }

    [Fact]
    public async Task StoredConfigViolatingTheBounds_FallsBackToTheDefaults()
    {
        using var ctx = new SqliteTestContext();
        await StoreAsync(ctx, JsonSerializer.Serialize(Config(
            new CareerRequirement { Text = new string('x', CareerRequirementsConfig.MaxTextLength + 1) })));

        var config = await Build(ctx).GetAsync();

        Assert.Equal(7, config.Items.Count);
    }

    [Fact]
    public async Task Save_RoundTripsOrderAndAlternatives()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SaveAsync(Config(
            new CareerRequirement { Text = "Erste", Alternatives = ["A1", "A2"] },
            new CareerRequirement { Text = "Zweite" }), Leader());

        var config = await svc.GetEditableAsync();

        Assert.Collection(config.Items,
            first =>
            {
                Assert.Equal("Erste", first.Text);
                Assert.Equal(["A1", "A2"], first.Alternatives);
            },
            second =>
            {
                Assert.Equal("Zweite", second.Text);
                Assert.Empty(second.Alternatives);
            });
    }

    [Fact]
    public async Task Save_TrimsAndDropsBlankRows()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SaveAsync(Config(
            new CareerRequirement { Text = "  Behalten  ", Alternatives = ["  Auch  ", "   ", ""] },
            new CareerRequirement { Text = "   " },
            new CareerRequirement { Text = string.Empty }), Hrb());

        var config = await svc.GetEditableAsync();

        var item = Assert.Single(config.Items);
        Assert.Equal("Behalten", item.Text);
        Assert.Equal(["Auch"], item.Alternatives);
    }

    [Fact]
    public async Task Save_EmptyList_StaysEmptyInsteadOfFallingBackToTheDefaults()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SaveAsync(new CareerRequirementsConfig(), Leader());

        Assert.Empty((await svc.GetEditableAsync()).Items);
        Assert.Empty((await svc.GetAsync()).Items);
    }

    [Fact]
    public async Task Save_WritesAConfigAuditRow()
    {
        using var ctx = new SqliteTestContext();

        await Build(ctx).SaveAsync(Config(
            new CareerRequirement { Text = "Erste", Alternatives = ["A1"] }), Leader());

        await using var db = ctx.NewContext();
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal(CareerRequirementsService.AuditType, audit.EntityType);
        Assert.Equal("global", audit.EntityId);
        Assert.Contains("Karriere-Anforderungen", audit.ChangesJson);
    }

    [Fact]
    public async Task Save_EvictsTheCache()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        // warm the cache with the defaults
        Assert.Equal(7, (await svc.GetAsync()).Items.Count);

        await svc.SaveAsync(Config(new CareerRequirement { Text = "Nur eine" }), Leader());

        var item = Assert.Single((await svc.GetAsync()).Items);
        Assert.Equal("Nur eine", item.Text);
    }

    [Fact]
    public async Task Save_HrbIsAllowed()
    {
        using var ctx = new SqliteTestContext();

        await Build(ctx).SaveAsync(Config(new CareerRequirement { Text = "HRB darf" }), Hrb());

        var item = Assert.Single((await Build(ctx).GetEditableAsync()).Items);
        Assert.Equal("HRB darf", item.Text);
    }

    [Fact]
    public async Task Save_PlainAgentIsRejected()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).SaveAsync(Config(new CareerRequirement { Text = "Nein" }), Junior()));
    }

    [Fact]
    public async Task Save_OnlyReaderIsRejected()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).SaveAsync(Config(new CareerRequirement { Text = "Nein" }), OnlyReader()));
    }

    [Fact]
    public async Task Save_TooManyItemsIsRejected()
    {
        using var ctx = new SqliteTestContext();
        var items = Enumerable.Range(0, CareerRequirementsConfig.MaxItems + 1)
            .Select(i => new CareerRequirement { Text = $"Anforderung {i}" })
            .ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).SaveAsync(Config(items), Leader()));
    }

    [Fact]
    public async Task Save_TooManyAlternativesIsRejected()
    {
        using var ctx = new SqliteTestContext();
        var item = new CareerRequirement
        {
            Text = "Erste",
            Alternatives = Enumerable.Range(0, CareerRequirementsConfig.MaxAlternatives + 1)
                .Select(i => $"Alternative {i}")
                .ToList(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).SaveAsync(Config(item), Leader()));
    }

    [Fact]
    public async Task Save_TooLongTextIsRejected()
    {
        using var ctx = new SqliteTestContext();
        var item = new CareerRequirement { Text = new string('x', CareerRequirementsConfig.MaxTextLength + 1) };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).SaveAsync(Config(item), Leader()));
    }

    [Fact]
    public async Task Save_TooLongAlternativeIsRejected()
    {
        using var ctx = new SqliteTestContext();
        var item = new CareerRequirement
        {
            Text = "Erste",
            Alternatives = [new string('x', CareerRequirementsConfig.MaxTextLength + 1)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).SaveAsync(Config(item), Leader()));
    }
}
