using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="IActivityStatisticsService" />
public class ActivityStatisticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache) : IActivityStatisticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Record types shown as rows/series; the audit log covers more, but these are the actual files.</summary>
    private static readonly (string Type, string Label)[] TrackedTypes =
    [
        ("Person", "Personen"),
        ("Faction", "Fraktionen"),
        ("PersonGroup", "Personengruppen"),
        ("Party", "Parteien"),
        ("DrugRoute", "Drogenrouten"),
        ("Operation", "Operationen"),
        ("Case", "Vorgänge"),
        ("Taskforce", "Taskforces"),
        ("Job", "Aufgaben"),
    ];

    public async Task<ChartMatrix> GetWeekdayHourAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        return await cache.GetOrCreateAsync($"stats:activity:weekhour:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var start = scope.StartUtc(DateTime.UtcNow);

            // grouped in SQL down to the hour, so at most days x 24 rows come back regardless of log size
            var slots = await db.AuditLogs
                .Where(a => a.Timestamp >= start)
                .GroupBy(a => new { a.Timestamp.Year, a.Timestamp.Month, a.Timestamp.Day, a.Timestamp.Hour })
                .Select(g => new
                {
                    g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour,
                    Count = g.Count(),
                })
                .ToListAsync(cancellationToken);

            // weekday and the displayed hour are local; the app pins TZ=Europe/Berlin
            var cells = Enumerable.Range(0, 7).Select(_ => new int[24]).ToList();
            foreach (var slot in slots)
            {
                var utc = new DateTime(slot.Year, slot.Month, slot.Day, slot.Hour, 0, 0, DateTimeKind.Utc);
                var local = utc.ToLocalTime();
                var row = ((int)local.DayOfWeek + 6) % 7;
                cells[row][local.Hour] += slot.Count;
            }

            var max = cells.SelectMany(c => c).DefaultIfEmpty(0).Max();
            return new ChartMatrix(
                ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"],
                Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToList(),
                cells.Select(c => (IReadOnlyList<int>)c).ToList(),
                max);
        }) ?? ChartMatrix.Empty;
    }

    public async Task<IReadOnlyList<ChartDay>> GetDailyDensityAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        return await cache.GetOrCreateAsync($"stats:activity:daily:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var start = scope.StartUtc(DateTime.UtcNow);

            var days = await db.AuditLogs
                .Where(a => a.Timestamp >= start)
                .GroupBy(a => new { a.Timestamp.Year, a.Timestamp.Month, a.Timestamp.Day })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
                .ToListAsync(cancellationToken);

            // fold onto local days so the calendar cannot drift a day against the feed
            var perDay = new Dictionary<DateOnly, int>();
            foreach (var day in days)
            {
                var local = new DateTime(day.Year, day.Month, day.Day, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();
                var key = DateOnly.FromDateTime(local);
                perDay[key] = perDay.GetValueOrDefault(key) + day.Count;
            }

            return (IReadOnlyList<ChartDay>)perDay
                .OrderBy(p => p.Key)
                .Select(p => new ChartDay(p.Key, p.Value))
                .ToList();
        }) ?? [];
    }

    public async Task<ChartGrid> GetByRecordTypeAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        return await cache.GetOrCreateAsync($"stats:activity:bytype:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            var matrix = await LoadTypeByBucketAsync(scope, cancellationToken);

            var buckets = StatisticsBuckets.Starts(scope, DateTime.UtcNow);
            var series = matrix.Rows
                .Select((label, i) => new ChartSeriesData(label,
                    matrix.Cells[i].Select(c => (double)c).ToList()))
                .Where(s => s.Values.Any(v => v > 0))
                .ToList();

            // an all-cold type would only add a dead legend entry
            return new ChartGrid(buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(), series);
        }) ?? ChartGrid.Empty;
    }

    public async Task<ChartMatrix> GetCaptureGapsAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        return await cache.GetOrCreateAsync($"stats:activity:gaps:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await LoadTypeByBucketAsync(scope, cancellationToken);
        }) ?? ChartMatrix.Empty;
    }

    /// <summary>Record type by bucket; the shared source of the by-type series and the gap heatmap.</summary>
    private async Task<ChartMatrix> LoadTypeByBucketAsync(StatisticsScope scope, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var start = scope.StartUtc(now);
        var tracked = TrackedTypes.Select(t => t.Type).ToList();

        var rows = await db.AuditLogs
            .Where(a => a.Timestamp >= start && tracked.Contains(a.EntityType))
            .GroupBy(a => new { a.EntityType, a.Timestamp.Year, a.Timestamp.Month, a.Timestamp.Day })
            .Select(g => new { g.Key.EntityType, g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var buckets = StatisticsBuckets.Starts(scope, now);
        var cells = TrackedTypes.Select(_ => new int[buckets.Count]).ToList();
        foreach (var row in rows)
        {
            var typeIndex = Array.FindIndex(TrackedTypes, t => t.Type == row.EntityType);
            if (typeIndex < 0)
            {
                continue;
            }
            var stamp = new DateTime(row.Year, row.Month, row.Day, 0, 0, 0, DateTimeKind.Utc);
            var bucket = StatisticsBuckets.IndexOf(buckets, stamp);
            if (bucket >= 0)
            {
                cells[typeIndex][bucket] += row.Count;
            }
        }

        var max = cells.SelectMany(c => c).DefaultIfEmpty(0).Max();
        return new ChartMatrix(
            TrackedTypes.Select(t => t.Label).ToList(),
            buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(),
            cells.Select(c => (IReadOnlyList<int>)c).ToList(),
            max);
    }
}
