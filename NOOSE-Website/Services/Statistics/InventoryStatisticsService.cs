using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="IInventoryStatisticsService" />
public class InventoryStatisticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IRecencyService recency,
    IMemoryCache cache) : IInventoryStatisticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<ChartGrid> GetGrowthAsync(StatisticsScope scope, CancellationToken cancellationToken = default)
        => await cache.GetOrCreateAsync($"stats:inventory:growth:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var start = scope.StartUtc(now);
            var buckets = StatisticsBuckets.Starts(scope, now);

            // one bounded query per type; only the creation instant is fetched
            var series = new List<ChartSeriesData>
            {
                new("Personen", StatisticsBuckets.Count(await db.People
                    .Where(p => (scope.IncludeClassified || !p.IsClassified) && p.CreatedAt >= start)
                    .Select(p => p.CreatedAt).ToListAsync(cancellationToken), buckets)),
                new("Fraktionen", StatisticsBuckets.Count(await db.Factions
                    .Where(f => (scope.IncludeClassified || !f.IsClassified) && f.CreatedAt >= start)
                    .Select(f => f.CreatedAt).ToListAsync(cancellationToken), buckets)),
                new("Personengruppen", StatisticsBuckets.Count(await db.PersonGroups
                    .Where(g => (scope.IncludeClassified || !g.IsClassified) && g.CreatedAt >= start)
                    .Select(g => g.CreatedAt).ToListAsync(cancellationToken), buckets)),
                new("Parteien", StatisticsBuckets.Count(await db.Parties
                    .Where(p => (scope.IncludeClassified || !p.IsClassified) && p.CreatedAt >= start)
                    .Select(p => p.CreatedAt).ToListAsync(cancellationToken), buckets)),
                new("Drogenrouten", StatisticsBuckets.Count(await db.DrugRoutes
                    .Where(r => (scope.IncludeClassified || !r.IsClassified) && r.CreatedAt >= start)
                    .Select(r => r.CreatedAt).ToListAsync(cancellationToken), buckets)),
                new("Operationen", StatisticsBuckets.Count(await db.Operations
                    .Where(o => (scope.IncludeClassified || !o.IsClassified) && o.CreatedAt >= start)
                    .Select(o => o.CreatedAt).ToListAsync(cancellationToken), buckets)),
                new("Vorgänge", StatisticsBuckets.Count(await db.Cases
                    .Where(c => (scope.IncludeClassified || !c.IsClassified) && c.CreatedAt >= start)
                    .Select(c => c.CreatedAt).ToListAsync(cancellationToken), buckets)),
            };

            return new ChartGrid(buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(), series);
        }) ?? ChartGrid.Empty;

    public async Task<ChartGrid> GetClassificationAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var counts = (await db.People.OnlyActive()
                .Where(p => scope.IncludeClassified || !p.IsClassified)
                .GroupBy(p => p.Classification)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);

        // projected over the display order so an empty stage keeps its slot and its colour
        return new ChartGrid(
            ClassificationDisplay.All.Select(ClassificationDisplay.Name).ToList(),
            [new ChartSeriesData("Personen", ClassificationDisplay.All
                .Select(c => (double)counts.GetValueOrDefault(c)).ToList())]);
    }

    public async Task<ChartGrid> GetHazardComparisonAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var personScores = await db.People.OnlyActive()
            .Where(p => scope.IncludeClassified || !p.IsClassified)
            .Select(p => p.ThreatScore)
            .ToListAsync(cancellationToken);
        var factionScores = await db.Factions.OnlyActive()
            .Where(f => scope.IncludeClassified || !f.IsClassified)
            .Select(f => f.ThreatScore)
            .ToListAsync(cancellationToken);

        return new ChartGrid(
            HazardLevelLogic.All.Select(HazardLevelLogic.Name).ToList(),
            [
                new ChartSeriesData("Personen", ByHazard(personScores)),
                new ChartSeriesData("Fraktionen", ByHazard(factionScores)),
            ]);
    }

    public async Task<IReadOnlyList<DistributionSegment>> GetLifeStatusAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        // the effective status depends on the respawn window, which only C# can evaluate
        var raw = await db.People.OnlyActive()
            .Where(p => scope.IncludeClassified || !p.IsClassified)
            .Select(p => new { p.LifeStatus, p.DeadUntil })
            .ToListAsync(cancellationToken);
        var counts = raw
            .GroupBy(x => LifeStatusLogic.Effective(x.LifeStatus, x.DeadUntil, now))
            .ToDictionary(g => g.Key, g => g.Count());

        return LifeStatusDisplay.All
            .Select(s => new DistributionSegment(LifeStatusDisplay.Name(s), counts.GetValueOrDefault(s)))
            .ToList();
    }

    public async Task<ChartGrid> GetMeasureOutcomeTrendAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
        => await cache.GetOrCreateAsync($"stats:inventory:outcomes:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var start = scope.StartUtc(now);
            var buckets = StatisticsBuckets.Starts(scope, now);

            var rows = await db.PersonDocs
                .Where(d => (scope.IncludeClassified || !d.Person!.IsClassified) && d.Timestamp >= start)
                .Select(d => new { d.Timestamp, d.Outcome })
                .ToListAsync(cancellationToken);

            var series = MeasureOutcomeDisplay.All
                .Select(outcome => new ChartSeriesData(
                    MeasureOutcomeDisplay.Name(outcome),
                    StatisticsBuckets.Count(rows.Where(r => r.Outcome == outcome).Select(r => r.Timestamp).ToList(), buckets)))
                .ToList();

            return new ChartGrid(buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(), series);
        }) ?? ChartGrid.Empty;

    public async Task<ChartGrid> GetCaseFunnelAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var counts = (await db.Cases
                .Where(c => scope.IncludeClassified || !c.IsClassified)
                .GroupBy(c => c.Status)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);

        return new ChartGrid(
            CaseStatusDisplay.All.Select(CaseStatusDisplay.Name).ToList(),
            [new ChartSeriesData("Vorgänge", CaseStatusDisplay.All
                .Select(s => (double)counts.GetValueOrDefault(s)).ToList())]);
    }

    public async Task<IReadOnlyList<ChartRatio>> GetRecencyAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await recency.GetAllSettingsAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // reference date is modified-at falling back to created-at, the same rule the recency light uses
        var sources = new (string Type, string Label, string Href, Func<Task<List<DateTime>>> Load)[]
        {
            ("Person", "Personen", "/personen", () => db.People.OnlyActive()
                .Where(p => scope.IncludeClassified || !p.IsClassified)
                .Select(p => p.ModifiedAt ?? p.CreatedAt).ToListAsync(cancellationToken)),
            // factions age by their four facet stamps (members/stocks/activities/docs), oldest wins
            ("Faction", "Fraktionen", "/fraktionen", async () => (await db.Factions.OnlyActive()
                    .Where(f => scope.IncludeClassified || !f.IsClassified)
                    .Select(f => new
                    {
                        f.CreatedAt, f.MembersRefreshedAt, f.StockRefreshedAt,
                        f.ActivitiesRefreshedAt, f.DocsRefreshedAt,
                    })
                    .ToListAsync(cancellationToken))
                .Select(f => FactionRecency.Reference(f.CreatedAt, f.MembersRefreshedAt, f.StockRefreshedAt,
                    f.ActivitiesRefreshedAt, f.DocsRefreshedAt))
                .ToList()),
            ("PersonGroup", "Personengruppen", "/personengruppen", () => db.PersonGroups
                .Where(g => scope.IncludeClassified || !g.IsClassified)
                .Select(g => g.ModifiedAt ?? g.CreatedAt).ToListAsync(cancellationToken)),
            ("Party", "Parteien", "/parteien", () => db.Parties
                .Where(p => scope.IncludeClassified || !p.IsClassified)
                .Select(p => p.ModifiedAt ?? p.CreatedAt).ToListAsync(cancellationToken)),
            ("DrugRoute", "Drogenrouten", "/drogenrouten", () => db.DrugRoutes.OnlyActive()
                .Where(r => scope.IncludeClassified || !r.IsClassified)
                .Select(r => r.ModifiedAt ?? r.CreatedAt).ToListAsync(cancellationToken)),
            ("Operation", "Operationen", "/operationen", () => db.Operations
                .Where(o => scope.IncludeClassified || !o.IsClassified)
                .Select(o => o.ModifiedAt ?? o.CreatedAt).ToListAsync(cancellationToken)),
            ("Case", "Vorgänge", "/vorgaenge", () => db.Cases
                .Where(c => scope.IncludeClassified || !c.IsClassified)
                .Select(c => c.ModifiedAt ?? c.CreatedAt).ToListAsync(cancellationToken)),
        };

        var rows = new List<ChartRatio>(sources.Length);
        foreach (var source in sources)
        {
            var dates = await source.Load();
            if (dates.Count == 0)
            {
                continue;
            }
            var setting = settings.GetValueOrDefault(source.Type, new RecencySettings(30, 90, false));
            var fresh = setting.AgingDisabled
                ? dates.Count
                : dates.Count(d => (now - d).TotalDays <= setting.WarningDays);
            rows.Add(new ChartRatio(source.Label, fresh, dates.Count, source.Href));
        }
        return rows;
    }

    private static IReadOnlyList<double> ByHazard(IReadOnlyList<int?> scores)
    {
        var counts = scores.GroupBy(HazardLevelLogic.From).ToDictionary(g => g.Key, g => g.Count());
        return HazardLevelLogic.All.Select(l => (double)counts.GetValueOrDefault(l)).ToList();
    }
}

