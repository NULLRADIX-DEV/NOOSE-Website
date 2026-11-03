using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services.Statistics;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The rank pyramid and the promotion trend read the whole ladder, whatever its length.</summary>
public sealed class WorkforceStatisticsServiceTests
{
    private static WorkforceStatisticsService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build();

    private static StatisticsScope Scope() => new(true, StatisticsRange.Months12);

    [Fact]
    public async Task GetRankPyramidAsync_ListsAllEightRanksTopDown_WithTheirHeadCount()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("d", Rank.Director),
                Seed.Agent("ad", Rank.AssistantDirector),
                Seed.Agent("sc1", Rank.SectionChief), Seed.Agent("sc2", Rank.SectionChief),
                Seed.Agent("t", Rank.Trainee));
            db.SaveChanges();
        }

        var grid = await Build(ctx).GetRankPyramidAsync(Leader(), Scope());

        Assert.Equal(RankDisplay.All.Reverse().Select(r => RankDisplay.Name(r)).ToList(), grid.Labels);
        Assert.Equal(8, grid.Labels.Count);
        Assert.Equal("Director", grid.Labels[0]);
        Assert.Equal("Trainee", grid.Labels[^1]);
        // Director, Deputy Director, Assistant Director, Section Chief, Special Agent in Charge, ..., Trainee
        Assert.Equal([1d, 0d, 1d, 2d, 0d, 0d, 0d, 1d], Assert.Single(grid.Series).Values);
    }

    [Fact]
    public async Task GetPromotionTrendAsync_CountsAPromotionIntoTheNewLevelsAndNeverAFirstAssignment()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        using (var db = ctx.NewContext())
        {
            db.AgentRankHistories.AddRange(
                new AgentRankHistory { AgentId = "a", Alt = Rank.SpecialAgentInCharge, New = Rank.SectionChief, Timestamp = now.AddDays(-1) },
                new AgentRankHistory { AgentId = "b", Alt = Rank.SectionChief, New = Rank.AssistantDirector, Timestamp = now.AddDays(-1) },
                new AgentRankHistory { AgentId = "c", Alt = Rank.DeputyDirector, New = Rank.AssistantDirector, Timestamp = now.AddDays(-1) },
                new AgentRankHistory { AgentId = "d", Alt = null, New = Rank.Director, Timestamp = now.AddDays(-1) });
            db.SaveChanges();
        }

        var grid = await Build(ctx).GetPromotionTrendAsync(Leader(), Scope());

        Assert.Equal(2d, grid.Series.Single(s => s.Name == "Beförderungen").Values.Sum());
        Assert.Equal(1d, grid.Series.Single(s => s.Name == "Rückstufungen").Values.Sum());
    }
}
