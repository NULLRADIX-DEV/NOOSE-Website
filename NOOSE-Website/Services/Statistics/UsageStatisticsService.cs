using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Services.Statistics;

/// <inheritdoc cref="IUsageStatisticsService" />
public sealed class UsageStatisticsService(IDbContextFactory<AppDbContext> dbFactory) : IUsageStatisticsService
{
    public async Task<UsageStatistics> GetAsync(ClaimsPrincipal actor, StatisticsScope scope,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);

        var now = DateTime.UtcNow;
        var to = UsageCollector.DayOf(now);
        var earliest = to.AddDays(-(UsageRules.RetentionDays - 1));
        // Berlin days on both ends
        var from = DateOnly.FromDateTime(scope.StartUtc(MeetingTime.Local(now)));
        var clipped = from < earliest;
        if (clipped)
        {
            from = earliest;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sums = await db.UsageDays.AsNoTracking()
            .Where(u => u.Day >= from && u.Day <= to)
            .GroupBy(u => u.Key)
            .Select(g => new
            {
                Key = g.Key,
                Events = g.Sum(u => u.Events),
                AgentDays = g.Sum(u => u.Agents),
                PeakAgents = g.Max(u => u.Agents),
            })
            .ToDictionaryAsync(x => x.Key, cancellationToken);
        DateOnly? first = await db.UsageDays.AsNoTracking().AnyAsync(cancellationToken)
            ? await db.UsageDays.AsNoTracking().MinAsync(u => u.Day, cancellationToken)
            : null;

        // catalog first, so unused shows
        var rows = UsageCatalog.All
            .Select(f => sums.TryGetValue(f.Key, out var s)
                ? new UsageRow(f.Key, f.Label, f.Group, s.Events, s.AgentDays, s.PeakAgents)
                : new UsageRow(f.Key, f.Label, f.Group, 0, 0, 0))
            .ToList();
        return new UsageStatistics(from, to, first, clipped, rows);
    }
}
