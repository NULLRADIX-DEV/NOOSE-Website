using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="IFinancingStatisticsService" />
public class FinancingStatisticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    IFinancingConfigService configService) : IFinancingStatisticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private const int TopN = 8;

    // funding carries no VS axis, so only the window matters — key on the range only
    public async Task<FinancingStatistics> GetAsync(StatisticsScope scope, CancellationToken cancellationToken = default)
    {
        var windowed = await cache.GetOrCreateAsync($"stats:finanzierung:v1:{StatisticsRangeDisplay.Token(scope.Range)}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var start = scope.StartUtc(now);
            var buckets = StatisticsBuckets.Starts(scope, now);

            var rows = await db.FinancingRequests.AsNoTracking()
                .Where(r => r.CreatedAt >= start)
                .Select(r => new { r.Status, r.ApprovedSubsidy, r.DecidedAt, r.PaidAt })
                .ToListAsync(cancellationToken);

            var decided = rows
                .Where(r => r.DecidedAt is not null && r.ApprovedSubsidy is not null
                    && (r.Status == FinancingStatus.Approved || r.Status == FinancingStatus.Paid))
                .Select(r => (r.DecidedAt!.Value, r.ApprovedSubsidy!.Value))
                .ToList();
            var paid = rows
                .Where(r => r.PaidAt is not null && r.ApprovedSubsidy is not null && r.Status == FinancingStatus.Paid)
                .Select(r => (r.PaidAt!.Value, r.ApprovedSubsidy!.Value))
                .ToList();

            var volume = new ChartGrid(
                buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(),
                [
                    new ChartSeriesData("Genehmigt", SumInto(decided, buckets)),
                    new ChartSeriesData("Ausgezahlt", SumInto(paid, buckets)),
                ]);

            // lines carry no soft-delete of their own, so gate them on their (filtered) request
            var topItems = await db.FinancingRequestLines.AsNoTracking()
                .Where(l => l.CreatedAt >= start && db.FinancingRequests.Any(r => r.Id == l.RequestId))
                .GroupBy(l => l.ItemName)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(TopN)
                .ToListAsync(cancellationToken);

            return new FinancingStatistics(
                decided.Sum(d => d.Item2),
                paid.Sum(p => p.Item2),
                rows.Count,
                rows.Count(r => r.Status == FinancingStatus.Requested),
                rows.Count(r => r.Status == FinancingStatus.Rejected),
                volume,
                ChartGrid.Empty,
                topItems.Select(x => new DistributionSegment(x.Name, x.Count)).ToList());
        }) ?? FinancingStatistics.Empty;

        // current-month utilisation is read live, so it can never lag the budgets page
        var utilisation = await UtilisationAsync(cancellationToken);
        return windowed with { UtilisationByRank = utilisation };
    }

    public async Task<FinancingReport> GetMonthAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1);

        var rows = await db.FinancingRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < to)
            .Select(r => new { r.Status, r.ApprovedSubsidy })
            .ToListAsync(cancellationToken);

        // charged to this budget month, regardless of when the decision itself happened
        var charged = await db.FinancingRequests.AsNoTracking()
            .Where(r => r.BudgetYear == year && r.BudgetMonth == month
                && (r.Status == FinancingStatus.Approved || r.Status == FinancingStatus.Paid))
            .Select(r => new { r.Status, r.ApprovedSubsidy, r.AgentId })
            .ToListAsync(cancellationToken);

        var topItems = await db.FinancingRequestLines.AsNoTracking()
            .Where(l => l.CreatedAt >= from && l.CreatedAt < to
                && db.FinancingRequests.Any(r => r.Id == l.RequestId))
            .GroupBy(l => l.ItemName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(TopN)
            .ToListAsync(cancellationToken);

        var byRank = await RankUtilisationAsync(db, year, month, cancellationToken);

        return new FinancingReport(
            charged.Sum(r => r.ApprovedSubsidy ?? 0m),
            charged.Where(r => r.Status == FinancingStatus.Paid).Sum(r => r.ApprovedSubsidy ?? 0m),
            rows.Count,
            rows.Count(r => r.Status is FinancingStatus.Approved or FinancingStatus.Paid),
            rows.Count(r => r.Status == FinancingStatus.Rejected),
            topItems.Select(x => new DistributionSegment(x.Name, x.Count)).ToList(),
            byRank);
    }

    /// <summary>Base budget versus consumption of the running month, grouped by rank.</summary>
    private async Task<ChartGrid> UtilisationAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (year, month) = FinancingPeriod.Current();
        var rows = await RankUtilisationAsync(db, year, month, cancellationToken);
        if (rows.Count == 0)
        {
            return ChartGrid.Empty;
        }
        return new ChartGrid(
            rows.Select(r => r.Rank).ToList(),
            [
                new ChartSeriesData("Grundbudget", rows.Select(r => (double)r.Budget).ToList()),
                new ChartSeriesData("Verbraucht", rows.Select(r => (double)r.Consumed).ToList()),
            ]);
    }

    private async Task<List<FinancingRankUtilisation>> RankUtilisationAsync(AppDbContext db, int year, int month,
        CancellationToken cancellationToken)
    {
        var config = await configService.GetAsync(cancellationToken);
        // team leads never count towards a rank's budget or consumption; rankless agents have no budget
        var agents = await db.Users.AsNoTracking().OnlySelectable()
            .Where(a => a.Rank != null)
            .Select(a => new { a.Id, a.Rank, a.Division, a.IsTRU, a.PartnerAgency, a.FinancingBudgetOverride })
            .ToListAsync(cancellationToken);
        if (agents.Count == 0)
        {
            return new();
        }

        var consumedByAgent = await db.FinancingRequests.AsNoTracking()
            .Where(r => r.BudgetYear == year && r.BudgetMonth == month && r.AgentId != null
                && (r.Status == FinancingStatus.Approved || r.Status == FinancingStatus.Paid))
            .GroupBy(r => r.AgentId)
            .Select(g => new { AgentId = g.Key!, Sum = g.Sum(r => r.ApprovedSubsidy ?? 0m) })
            .ToListAsync(cancellationToken);
        var consumed = consumedByAgent.ToDictionary(x => x.AgentId, x => x.Sum);

        return RankDisplay.All
            .Select(rank =>
            {
                var members = agents.Where(a => a.Rank == rank).ToList();
                var budget = members.Sum(a => a.FinancingBudgetOverride
                    ?? config.For(rank, DivisionRules.Of(a.Rank, a.Division, a.IsTRU, a.PartnerAgency == null).Home).BaseMonthly);
                var used = members.Sum(a => consumed.GetValueOrDefault(a.Id, 0m));
                return new FinancingRankUtilisation(RankDisplay.Name(rank), budget, used);
            })
            .Where(r => r.Budget > 0 || r.Consumed > 0)
            .ToList();
    }

    /// <summary>Sums amounts into their buckets; anything before the first bucket is dropped.</summary>
    private static IReadOnlyList<double> SumInto(IReadOnlyList<(DateTime Timestamp, decimal Amount)> items, List<DateTime> buckets)
    {
        var sums = new double[buckets.Count];
        foreach (var (timestamp, amount) in items)
        {
            var index = StatisticsBuckets.IndexOf(buckets, timestamp);
            if (index >= 0)
            {
                sums[index] += (double)amount;
            }
        }
        return sums;
    }
}
