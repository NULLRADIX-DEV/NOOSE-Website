using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Live NOOSEI misuse flags for the admin overview.</summary>
public interface ILlmAnomalyService
{
    Task<IReadOnlyList<LlmAnomalyFlag>> GetFlagsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>A fresh evaluation and the week it speaks for; no actor guard, the finding watch gates the result.</summary>
    Task<LlmAnomalySnapshot> EvaluateAsync(CancellationToken cancellationToken = default);
}

/// <summary>The running week's flags together with that week's start, so both always agree.</summary>
public sealed record LlmAnomalySnapshot(DateTime WeekStartUtc, IReadOnlyList<LlmAnomalyFlag> Flags);

/// <inheritdoc cref="ILlmAnomalyService" />
public sealed class LlmAnomalyService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILlmQuotaConfigService configService,
    ILlmQuotaService quota,
    IMemoryCache cache) : ILlmAnomalyService
{
    private const string CacheKey = "ki:anomalien";

    /// <summary>Upper bound on the rows one evaluation reads; the duplicate check is quadratic in them.</summary>
    private const int MaxRows = 5_000;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<LlmAnomalyFlag>> GetFlagsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // the panel names agents and their behaviour, so it follows the counter-intelligence gate
        Permission.RequireLeadershipNoReader(actor);
        if (cache.TryGetValue(CacheKey, out LlmAnomalySnapshot? cached) && cached is not null)
        {
            return cached.Flags;
        }
        return (await EvaluateAsync(cancellationToken)).Flags;
    }

    public async Task<LlmAnomalySnapshot> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var config = await configService.GetAsync(cancellationToken);
        var statuses = await quota.GetRosterStatusAsync(cancellationToken);
        var (year, week) = IsoWeekPeriod.Current();
        var weekStartUtc = IsoWeekPeriod.Start(year, week).ToUniversalTime();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // newest first, then reversed: the rules walk the week in order, and a runaway week must not push its
        // own burst out of the window. Prompt is longtext and only compared by its prefix — clip it in SQL.
        var rows = await db.LlmRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= weekStartUtc && r.Success)
            .OrderByDescending(r => r.CreatedAt)
            .Take(MaxRows)
            .Select(r => new
            {
                r.AgentId,
                r.CreatedAt,
                r.QuotaTokens,
                r.PromptFingerprint,
                Prompt = r.Prompt!.Substring(0, LlmAnomalyEvaluator.MaxPromptCompareChars),
            })
            .ToListAsync(cancellationToken);
        rows.Reverse();

        var usage = rows
            .Select(r => new LlmUsageRow(r.AgentId, null, r.CreatedAt.ToLocalTime(), r.QuotaTokens,
                r.PromptFingerprint, r.Prompt))
            .ToList();

        var trailing = await TrailingMeansAsync(db, config.Anomalies.OutlierTrailingWeeks,
            config.Anomalies.OutlierMinWeeks, year, week, cancellationToken);

        // the rank baseline is derived inside the evaluator, per agent and without them in it
        var flags = LlmAnomalyEvaluator.Evaluate(usage, statuses, trailing, config.Anomalies, DateTime.Now);
        // one cache for panel and watch, so a reported anomaly is never missing from the list it is marked in
        var snapshot = new LlmAnomalySnapshot(weekStartUtc, flags);
        cache.Set(CacheKey, snapshot, CacheDuration);
        return snapshot;
    }

    /// <summary>Mean consumption of an agent's recent closed weeks; agents without enough history are left out.</summary>
    private static async Task<Dictionary<string, double>> TrailingMeansAsync(
        AppDbContext db, int trailingWeeks, int minWeeks, int currentYear, int currentWeek, CancellationToken cancellationToken)
    {
        var oldest = (currentYear, currentWeek);
        for (var i = 0; i < Math.Max(1, trailingWeeks); i++)
        {
            oldest = IsoWeekPeriod.Previous(oldest.Item1, oldest.Item2);
        }

        var periods = await db.LlmQuotaPeriods.AsNoTracking()
            .Where(p => p.Year > oldest.Item1 || (p.Year == oldest.Item1 && p.Week >= oldest.Item2))
            .Select(p => new { p.AgentId, p.Consumed })
            .ToListAsync(cancellationToken);

        return periods
            .GroupBy(p => p.AgentId)
            .Where(g => g.Count() >= Math.Max(1, minWeeks))
            .ToDictionary(g => g.Key, g => g.Average(p => (double)p.Consumed));
    }
}
