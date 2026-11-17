using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Statistics;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FoodStatisticsService"/> over in-memory SQLite.</summary>
public sealed class FoodStatisticsServiceTests
{
    private static int _caseSeq;

    private static FoodStatisticsService Build(SqliteTestContext ctx)
    {
        var configService = Substitute.For<IFoodLimitConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(new FoodLimitConfig
        {
            Ranks = new Dictionary<string, FoodRankLimit>
            {
                [FoodLimitConfig.RankKey(Rank.SpecialAgent)] = new() { FoodPerWeek = 5, DrinkPerWeek = 4 },
            },
        });
        return new FoodStatisticsService(ctx.Factory, new MemoryCache(new MemoryCacheOptions()),
            new FoodLimitService(ctx.Factory, configService));
    }

    private static FoodRequest Request(string agentId, FoodRequestStatus status, DateTime createdAt,
        params (string Name, FoodKind Kind, int Quantity, int? Approved)[] lines)
    {
        var issued = status == FoodRequestStatus.Issued;
        var consumes = FoodRequestStatusDisplay.ConsumesLimit(status);
        return new FoodRequest
        {
            AgentId = agentId,
            CaseNumber = $"FIB-VER-2026-{Interlocked.Increment(ref _caseSeq):0000}",
            Status = status,
            CreatedAt = createdAt,
            FoodApproved = consumes ? lines.Where(l => l.Kind == FoodKind.Food).Sum(l => l.Approved ?? l.Quantity) : null,
            DrinkApproved = consumes ? lines.Where(l => l.Kind == FoodKind.Drink).Sum(l => l.Approved ?? l.Quantity) : null,
            LimitWeekStart = consumes ? FoodWeek.Current() : null,
            IssuedAt = issued ? createdAt : null,
            Lines = lines.Select((l, i) => new FoodRequestLine
            {
                ItemName = l.Name,
                Kind = l.Kind,
                Quantity = l.Quantity,
                ApprovedQuantity = l.Approved,
                Sorting = i,
            }).ToList(),
        };
    }

    [Fact]
    public async Task Get_SumsIssuedPieces_CountsStages_AndRanksPositions()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a", Rank.SpecialAgent));
            db.Users.Add(Seed.Agent("b", Rank.SpecialAgent));
            db.FoodRequests.AddRange(
                Request("a", FoodRequestStatus.Issued, now.AddMinutes(-5),
                    ("Burger", FoodKind.Food, 3, 2), ("eCola", FoodKind.Drink, 1, 1), ("Wasser", FoodKind.Drink, 2, 0)),
                Request("b", FoodRequestStatus.Issued, now.AddMinutes(-4), ("eCola", FoodKind.Drink, 2, 2)),
                // not handed out
                Request("b", FoodRequestStatus.Approved, now.AddMinutes(-3), ("Burger", FoodKind.Food, 1, 1)),
                Request("a", FoodRequestStatus.Requested, now.AddMinutes(-2), ("Burger", FoodKind.Food, 4, null)),
                Request("a", FoodRequestStatus.Rejected, now.AddMinutes(-2), ("Burger", FoodKind.Food, 4, 0)),
                Request("b", FoodRequestStatus.Withdrawn, now.AddMinutes(-1), ("Burger", FoodKind.Food, 1, null)),
                // outside every window
                Request("a", FoodRequestStatus.Issued, now.AddYears(-3), ("Burger", FoodKind.Food, 5, 5)));
            await db.SaveChangesAsync();
        }

        var stats = await Build(ctx).GetAsync(new StatisticsScope(true, StatisticsRange.Months12));

        Assert.Equal((2, 3), (stats.IssuedFood, stats.IssuedDrink));
        Assert.Equal(6, stats.RequestCount);
        Assert.Equal(1, stats.OpenCount);
        Assert.Equal(4, stats.DecidedCount);
        Assert.Equal(0.25, stats.RejectionRate);
        Assert.Equal(new[] { ("eCola", 3), ("Burger", 2) }, stats.TopItems.Select(t => (t.Designation, t.Count)));
        Assert.Equal(2, stats.ByStatus.Single(s => s.Designation == "Ausgegeben").Count);

        Assert.Equal(12, stats.IssuedOverTime.Labels.Count);
        Assert.Equal(2d, stats.IssuedOverTime.Series[0].Values[^1]);
        Assert.Equal(3d, stats.IssuedOverTime.Series[1].Values[^1]);

        // two agents' limits
        Assert.Equal(new[] { RankDisplay.Name(Rank.SpecialAgent) }, stats.FoodUtilisationByRank.Labels);
        Assert.Equal(10d, stats.FoodUtilisationByRank.Series[0].Values[0]);
        Assert.Equal(8d, stats.DrinkUtilisationByRank.Series[0].Values[0]);
    }

    [Fact]
    public void Utilisation_SumsPerRank_AndSkipsEmptyRanks()
    {
        var week = FoodWeek.Current();
        var limits = new List<FoodLimitStatus>
        {
            new("a", "A", Rank.SpecialAgent, week, 5, 4, 2, 1, false, false),
            new("b", "B", Rank.SpecialAgent, week, 6, 4, 7, 0, true, false),
            new("c", "C", Rank.Director, week, 0, 2, 0, 2, false, false),
            new("d", "D", null, week, 9, 9, 9, 9, false, false),
        };

        var food = FoodStatisticsService.Utilisation(limits, FoodKind.Food);
        var drink = FoodStatisticsService.Utilisation(limits, FoodKind.Drink);

        Assert.Equal(new[] { RankDisplay.Name(Rank.SpecialAgent) }, food.Labels);
        Assert.Equal(new[] { 11d }, food.Series[0].Values);
        Assert.Equal(new[] { 9d }, food.Series[1].Values);
        Assert.Equal(2, drink.Labels.Count);
        Assert.Equal(new[] { 8d, 2d }, drink.Series[0].Values);
        Assert.True(FoodStatisticsService.Utilisation([], FoodKind.Food).IsEmpty);
    }
}
