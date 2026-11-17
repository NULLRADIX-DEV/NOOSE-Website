using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="IFoodStatisticsService" />
public class FoodStatisticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    IFoodLimitService limitService) : IFoodStatisticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private const int TopN = 8;

    // food requests carry no VS axis, so only the window matters — key on the range only
    public async Task<FoodStatistics> GetAsync(StatisticsScope scope, CancellationToken cancellationToken = default)
    {
        var windowed = await cache.GetOrCreateAsync($"stats:verpflegung:v1:{StatisticsRangeDisplay.Token(scope.Range)}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var start = scope.StartUtc(now);
            var buckets = StatisticsBuckets.Starts(scope, now);

            var filed = await db.FoodRequests.AsNoTracking()
                .Where(r => r.CreatedAt >= start)
                .Select(r => r.Status)
                .ToListAsync(cancellationToken);

            var issued = await db.FoodRequests.AsNoTracking()
                .Where(r => r.Status == FoodRequestStatus.Issued && r.IssuedAt != null && r.IssuedAt >= start)
                .Select(r => new { IssuedAt = r.IssuedAt!.Value, Food = r.FoodApproved ?? 0, Drink = r.DrinkApproved ?? 0 })
                .ToListAsync(cancellationToken);

            var volume = new ChartGrid(
                buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(),
                [
                    new ChartSeriesData(FoodKindDisplay.Name(FoodKind.Food), SumInto(issued.Select(i => (i.IssuedAt, i.Food)), buckets)),
                    new ChartSeriesData(FoodKindDisplay.Name(FoodKind.Drink), SumInto(issued.Select(i => (i.IssuedAt, i.Drink)), buckets)),
                ]);

            // lines carry no soft-delete of their own, so gate them on their (filtered) issued request
            var topItems = await db.FoodRequestLines.AsNoTracking()
                .Where(l => (l.ApprovedQuantity ?? l.Quantity) > 0
                    && db.FoodRequests.Any(r => r.Id == l.RequestId && r.Status == FoodRequestStatus.Issued
                        && r.IssuedAt != null && r.IssuedAt >= start))
                .GroupBy(l => l.ItemName)
                .Select(g => new { Name = g.Key, Pieces = g.Sum(l => l.ApprovedQuantity ?? l.Quantity) })
                .OrderByDescending(x => x.Pieces)
                .ThenBy(x => x.Name)
                .Take(TopN)
                .ToListAsync(cancellationToken);

            var byStatus = FoodRequestStatusDisplay.All
                .Select(s => new DistributionSegment(FoodRequestStatusDisplay.Name(s), filed.Count(f => f == s)))
                .Where(s => s.Count > 0)
                .ToList();

            return new FoodStatistics(
                issued.Sum(i => i.Food),
                issued.Sum(i => i.Drink),
                filed.Count,
                filed.Count(s => s == FoodRequestStatus.Requested),
                filed.Count(s => s is FoodRequestStatus.Approved or FoodRequestStatus.Issued or FoodRequestStatus.Rejected),
                filed.Count(s => s == FoodRequestStatus.Rejected),
                volume,
                byStatus,
                topItems.Select(x => new DistributionSegment(x.Name, x.Pieces)).ToList(),
                ChartGrid.Empty,
                ChartGrid.Empty);
        }) ?? FoodStatistics.Empty;

        // the running week is read live, so it can never lag the limit tiles
        var limits = await limitService.GetAllStatusAsync(cancellationToken);
        return windowed with
        {
            FoodUtilisationByRank = Utilisation(limits, FoodKind.Food),
            DrinkUtilisationByRank = Utilisation(limits, FoodKind.Drink),
        };
    }

    /// <summary>Weekly limit versus consumption of the running week, summed per rank.</summary>
    public static ChartGrid Utilisation(IReadOnlyList<FoodLimitStatus> limits, FoodKind kind)
    {
        // rankless agents have no allowance
        var rows = RankDisplay.All
            .Select(rank =>
            {
                var members = limits.Where(l => l.Rank == rank).ToList();
                return (Rank: RankDisplay.Name(rank), Limit: members.Sum(m => m.Limit(kind)), Consumed: members.Sum(m => m.Consumed(kind)));
            })
            .Where(r => r.Limit > 0 || r.Consumed > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return ChartGrid.Empty;
        }
        return new ChartGrid(
            rows.Select(r => r.Rank).ToList(),
            [
                new ChartSeriesData("Wochenlimit", rows.Select(r => (double)r.Limit).ToList()),
                new ChartSeriesData("Verbraucht", rows.Select(r => (double)r.Consumed).ToList()),
            ]);
    }

    /// <summary>Sums pieces into their buckets; anything before the first bucket is dropped.</summary>
    private static IReadOnlyList<double> SumInto(IEnumerable<(DateTime Timestamp, int Pieces)> items, List<DateTime> buckets)
    {
        var sums = new double[buckets.Count];
        foreach (var (timestamp, pieces) in items)
        {
            var index = StatisticsBuckets.IndexOf(buckets, timestamp);
            if (index >= 0)
            {
                sums[index] += pieces;
            }
        }
        return sums;
    }
}
