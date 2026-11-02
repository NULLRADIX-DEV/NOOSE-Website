using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="ValueListLabelService"/>: DB upsert/delete plus the static store refresh behind the display classes.</summary>
[Collection("EnumLabels")]
public sealed class ValueListLabelServiceTests : IDisposable
{
    public void Dispose()
    {
        // never leak overrides into other tests
        EnumLabelText.ReplaceAll([]);
    }

    private static ValueListLabelService Build(SqliteTestContext ctx) => new(ctx.Factory);

    private static ClaimsPrincipal Leader
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build();

    private static ClaimsPrincipal ReadOnlySupervision
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).AsTeamLead().Build();

    [Fact]
    public async Task SetAsync_CreatesAndUpdatesOverride_AndRefreshesStore()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SetAsync("Rank", "Director", "Chefin", Leader);
        Assert.Equal("Chefin", EnumLabelText.Get("Rank", "Director"));
        Assert.Equal("Chefin", RankDisplay.Name(Rank.Director));
        Assert.Equal("Director", RankDisplay.DefaultName(Rank.Director));

        // second set updates the same row instead of duplicating it
        await svc.SetAsync("Rank", "Director", "Direktorin", Leader);
        Assert.Equal("Direktorin", EnumLabelText.Get("Rank", "Director"));
        using (var db = ctx.NewContext())
        {
            Assert.Single(db.EnumLabelOverrides);
        }
    }

    [Fact]
    public async Task ResetAsync_RemovesOverride_AndRefreshesStore()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.SetAsync("Rank", "Director", "Chefin", Leader);
        await svc.ResetAsync("Rank", "Director", Leader);

        Assert.Null(EnumLabelText.Get("Rank", "Director"));
        Assert.Equal("Director", RankDisplay.Name(Rank.Director));
        using (var db = ctx.NewContext())
        {
            Assert.Empty(db.EnumLabelOverrides);
        }
    }

    [Fact]
    public async Task SetAsync_DeniesReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).SetAsync("Rank", "Director", "Chefin", ReadOnlySupervision));
    }

    [Fact]
    public async Task SetAsync_RejectsEmptyLabel()
    {
        using var ctx = new SqliteTestContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).SetAsync("Rank", "Director", "   ", Leader));
    }
}
