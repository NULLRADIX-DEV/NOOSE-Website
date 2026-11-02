using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="IWorkforceStatisticsService" />
public class WorkforceStatisticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache) : IWorkforceStatisticsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Highest rank first, so the pyramid reads top-down.</summary>
    private static readonly Rank[] RanksTopDown = RankDisplay.All.Reverse().ToArray();

    /// <summary>Recruiting stages in funnel order; the terminal outcomes come last.</summary>
    /// <remarks>Kept here rather than on the display class, because the order is this chart's intent.</remarks>
    private static readonly BewerbungStatus[] FunnelStages =
    [
        BewerbungStatus.Eingereicht, BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImTest,
        BewerbungStatus.ImVorstellungsgespraech, BewerbungStatus.Angenommen,
        BewerbungStatus.Abgelehnt, BewerbungStatus.Geschlossen,
    ];

    public async Task<ChartGrid> GetRankPyramidAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var counts = (await Roster(db)
                .Where(a => a.Rank != null)
                .GroupBy(a => a.Rank!.Value)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);

        return new ChartGrid(
            RanksTopDown.Select(r => RankDisplay.Name(r)).ToList(),
            [new ChartSeriesData("Agenten", RanksTopDown
                .Select(r => (double)counts.GetValueOrDefault(r)).ToList())]);
    }

    public async Task<ChartGrid> GetPromotionTrendAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var start = scope.StartUtc(now);
        var buckets = StatisticsBuckets.Starts(scope, now);

        var rows = await db.AgentRankHistories
            .Where(h => h.Timestamp >= start)
            .Select(h => new { h.Timestamp, h.Alt, h.New })
            .ToListAsync(cancellationToken);

        // a first assignment has no previous rank, so it counts as neither up nor down
        var promotions = rows.Where(r => r.Alt != null && r.New > r.Alt).Select(r => r.Timestamp).ToList();
        var demotions = rows.Where(r => r.Alt != null && r.New < r.Alt).Select(r => r.Timestamp).ToList();

        return new ChartGrid(
            buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(),
            [
                new ChartSeriesData("Beförderungen", StatisticsBuckets.Count(promotions, buckets)),
                new ChartSeriesData("Rückstufungen", StatisticsBuckets.Count(demotions, buckets)),
            ]);
    }

    public async Task<ChartMatrix> GetWorkloadAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);
        return await cache.GetOrCreateAsync($"stats:workforce:workload:v1:{scope.CacheToken}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var start = scope.StartUtc(now);

            // codename only - the real name must never reach a chart
            var roster = await Roster(db)
                .Select(a => new { a.Id, a.Codename })
                .ToListAsync(cancellationToken);
            if (roster.Count == 0)
            {
                return ChartMatrix.Empty;
            }
            var ids = roster.Select(a => a.Id).ToList();

            var rows = await db.AuditLogs
                .Where(l => l.Timestamp >= start && l.AgentId != null && ids.Contains(l.AgentId))
                .GroupBy(l => new { l.AgentId, l.Timestamp.Year, l.Timestamp.Month, l.Timestamp.Day })
                .Select(g => new { g.Key.AgentId, g.Key.Year, g.Key.Month, g.Key.Day, Count = g.Count() })
                .ToListAsync(cancellationToken);

            var buckets = StatisticsBuckets.Starts(scope, now);
            var order = roster
                .OrderBy(a => a.Codename)
                .Select((a, i) => (a.Id, a.Codename, Index: i))
                .ToList();
            var byId = order.ToDictionary(a => a.Id, a => a.Index);
            var cells = order.Select(_ => new int[buckets.Count]).ToList();

            foreach (var row in rows)
            {
                if (row.AgentId is null || !byId.TryGetValue(row.AgentId, out var rowIndex))
                {
                    continue;
                }
                var stamp = new DateTime(row.Year, row.Month, row.Day, 0, 0, 0, DateTimeKind.Utc);
                var bucket = StatisticsBuckets.IndexOf(buckets, stamp);
                if (bucket >= 0)
                {
                    cells[rowIndex][bucket] += row.Count;
                }
            }

            var max = cells.SelectMany(c => c).DefaultIfEmpty(0).Max();
            return new ChartMatrix(
                order.Select(a => a.Codename ?? "—").ToList(),
                buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(),
                cells.Select(c => (IReadOnlyList<int>)c).ToList(),
                max);
        }) ?? ChartMatrix.Empty;
    }

    public async Task<ChartMatrix> GetMissedMeetingsAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var start = scope.StartUtc(now);

        // only closed meetings carry a settled attendance state
        var meetings = await db.Meetings
            .Where(m => m.AttendanceClosedAt != null && m.Start >= start)
            .Select(m => new { m.Id, m.Start })
            .ToListAsync(cancellationToken);
        if (meetings.Count == 0)
        {
            return ChartMatrix.Empty;
        }
        var meetingIds = meetings.Select(m => m.Id).ToList();
        var startById = meetings.ToDictionary(m => m.Id, m => m.Start);

        var misses = await db.MeetingAttendances
            .Where(a => meetingIds.Contains(a.MeetingId) && a.Status == MeetingAttendanceStatus.Missing)
            .Select(a => new { a.MeetingId, a.AgentId, a.AgentCodename })
            .ToListAsync(cancellationToken);
        if (misses.Count == 0)
        {
            return ChartMatrix.Empty;
        }

        var buckets = StatisticsBuckets.Starts(scope, now);
        var agents = misses
            .Select(m => m.AgentCodename ?? "—")
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        var byAgent = agents.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var cells = agents.Select(_ => new int[buckets.Count]).ToList();

        foreach (var miss in misses)
        {
            if (!startById.TryGetValue(miss.MeetingId, out var meetingStart))
            {
                continue;
            }
            var bucket = StatisticsBuckets.IndexOf(buckets, meetingStart);
            if (bucket >= 0 && byAgent.TryGetValue(miss.AgentCodename ?? "—", out var rowIndex))
            {
                cells[rowIndex][bucket]++;
            }
        }

        var max = cells.SelectMany(c => c).DefaultIfEmpty(0).Max();
        return new ChartMatrix(
            agents,
            buckets.Select(b => StatisticsBuckets.Label(b, scope)).ToList(),
            cells.Select(c => (IReadOnlyList<int>)c).ToList(),
            max);
    }

    public async Task<IReadOnlyList<ChartDay>> GetAbsenceCalendarAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var start = DateOnly.FromDateTime(scope.StartUtc(DateTime.UtcNow));

        var spans = await db.Absences
            .Where(a => a.ToDate >= start)
            .Select(a => new { a.FromDate, a.ToDate })
            .ToListAsync(cancellationToken);

        // each absence covers a range, so it contributes one agent-day to every day it spans
        var perDay = new Dictionary<DateOnly, int>();
        foreach (var span in spans)
        {
            var from = span.FromDate < start ? start : span.FromDate;
            for (var day = from; day <= span.ToDate; day = day.AddDays(1))
            {
                perDay[day] = perDay.GetValueOrDefault(day) + 1;
            }
        }

        return perDay
            .OrderBy(p => p.Key)
            .Select(p => new ChartDay(p.Key, p.Value))
            .ToList();
    }

    public async Task<ChartGrid> GetRecruitingFunnelAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var start = scope.StartUtc(DateTime.UtcNow);

        var counts = (await db.Bewerbungen
                .Where(b => b.SubmittedAt >= start)
                .GroupBy(b => b.Status)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);

        return new ChartGrid(
            FunnelStages.Select(BewerbungStatusDisplay.Name).ToList(),
            [new ChartSeriesData("Bewerbungen", FunnelStages
                .Select(s => (double)counts.GetValueOrDefault(s)).ToList())]);
    }

    /// <summary>In-house staff only: TeamLeads are RP-invisible and partners are not personnel.</summary>
    private static IQueryable<Data.Entities.Agent> Roster(AppDbContext db) => db.Users.OnlySelectable();
}
