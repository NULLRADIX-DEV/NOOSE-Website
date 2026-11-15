using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="INetworkStatisticsService" />
public class NetworkStatisticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache) : INetworkStatisticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Record types the cross-tab covers, in a fixed order so the matrix axes stay stable.</summary>
    private static readonly (string Type, string Label)[] LinkableTypes =
    [
        ("Person", "Person"),
        ("Faction", "Fraktion"),
        ("PersonGroup", "Personengruppe"),
        ("Party", "Partei"),
        ("DrugRoute", "Drogenroute"),
        ("Operation", "Operation"),
        ("Case", "Vorgang"),
        ("Taskforce", "Taskforce"),
    ];

    public async Task<ChartMatrix> GetTypeCrossTabAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
        => await cache.GetOrCreateAsync($"stats:network:crosstab:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var tracked = LinkableTypes.Select(t => t.Type).ToList();
            // Link is low-cardinality next to the log tables, and grouping on two stored columns translates
            var pairs = await db.Links
                .Where(l => tracked.Contains(l.SourceType) && tracked.Contains(l.TargetType))
                .GroupBy(l => new { l.SourceType, l.TargetType })
                .Select(g => new { g.Key.SourceType, g.Key.TargetType, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var cells = LinkableTypes.Select(_ => new int[LinkableTypes.Length]).ToList();
            foreach (var pair in pairs)
            {
                var row = Array.FindIndex(LinkableTypes, t => t.Type == pair.SourceType);
                var column = Array.FindIndex(LinkableTypes, t => t.Type == pair.TargetType);
                if (row < 0 || column < 0)
                {
                    continue;
                }
                // links are undirected in meaning, so fold both directions into one triangle-ish view
                cells[row][column] += pair.Count;
                if (row != column)
                {
                    cells[column][row] += pair.Count;
                }
            }

            var max = cells.SelectMany(c => c).DefaultIfEmpty(0).Max();
            return new ChartMatrix(
                LinkableTypes.Select(t => t.Label).ToList(),
                LinkableTypes.Select(t => t.Label).ToList(),
                cells.Select(c => (IReadOnlyList<int>)c).ToList(),
                max);
        }) ?? ChartMatrix.Empty;

    public async Task<ChartGrid> GetLinkKindTrendAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
        => await cache.GetOrCreateAsync($"stats:network:kinds:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var start = scope.StartUtc(now);
            var buckets = StatisticsBuckets.Starts(scope, now);

            var rows = await db.Links
                .Where(l => l.CreatedAt >= start)
                .Select(l => new { l.CreatedAt, l.Kind })
                .ToListAsync(cancellationToken);

            var series = LinkKindDisplay.All
                .Select(kind => new ChartSeriesData(
                    LinkKindDisplay.Name(kind),
                    StatisticsBuckets.Count(rows.Where(r => r.Kind == kind).Select(r => r.CreatedAt).ToList(), buckets)))
                .ToList();

            return new ChartGrid(buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(), series);
        }) ?? ChartGrid.Empty;

    public async Task<ChartGrid> GetRelationTypesAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var counts = (await db.PersonRelations
                .GroupBy(r => r.Type)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);

        return new ChartGrid(
            RelationTypeDisplay.All.Select(RelationTypeDisplay.Name).ToList(),
            [new ChartSeriesData("Beziehungen", RelationTypeDisplay.All
                .Select(t => (double)counts.GetValueOrDefault(t)).ToList())]);
    }

    public async Task<IReadOnlyList<ChartTile>> GetFactionTilesAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var factions = await db.Factions.OnlyActive()
            .Where(f => scope.IncludeClassified || !f.IsClassified)
            .Select(f => new
            {
                f.Id,
                f.Name,
                f.ThreatScore,
                Members = f.Members!.Count(),
                f.EstimatedMemberCount,
            })
            .ToListAsync(cancellationToken);

        return factions
            // the actual member count is the truth; the estimate only fills in where nothing is captured
            .Select(f => new ChartTile(f.Name,
                f.Members > 0 ? f.Members : f.EstimatedMemberCount ?? 0,
                HazardLevelLogic.From(f.ThreatScore),
                $"/fraktionen/{f.Id}"))
            .Where(t => t.Weight > 0)
            .OrderByDescending(t => t.Weight)
            .ToList();
    }

    public async Task<IReadOnlyList<ChartFlow>> GetClassificationFlowsAsync(StatisticsScope scope,
        CancellationToken cancellationToken = default)
        => await cache.GetOrCreateAsync($"stats:network:flows:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var start = scope.StartUtc(DateTime.UtcNow);

            // append-only history; ordering per record turns it into transitions
            var rows = await db.ClassificationHistory
                .Where(h => h.Timestamp >= start)
                .Select(h => new { h.EntityType, h.EntityId, h.Value, h.Timestamp })
                .ToListAsync(cancellationToken);

            var flows = new Dictionary<(Classification From, Classification To), int>();
            foreach (var record in rows.GroupBy(r => new { r.EntityType, r.EntityId }))
            {
                var ordered = record.OrderBy(r => r.Timestamp).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var from = ordered[i - 1].Value;
                    var to = ordered[i].Value;
                    if (from == to)
                    {
                        continue;
                    }
                    var key = (from, to);
                    flows[key] = flows.GetValueOrDefault(key) + 1;
                }
            }

            // the arrow keeps the two ends apart even when a stage appears on both sides
            return (IReadOnlyList<ChartFlow>)flows
                .OrderByDescending(f => f.Value)
                .Select(f => new ChartFlow(
                    ClassificationDisplay.Name(f.Key.From),
                    $"{ClassificationDisplay.Name(f.Key.To)} ›",
                    f.Value))
                .ToList();
        }) ?? [];
}
