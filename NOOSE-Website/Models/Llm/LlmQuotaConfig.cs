using System.Globalization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Llm;

/// <summary>Per-rank weekly AI token quota, carry-over share and the anomaly thresholds. Stored as JSON in a SystemSetting row.</summary>
public sealed class LlmQuotaConfig
{
    /// <summary>Keyed by <see cref="RankKey"/>; a missing key means no quota for that rank.</summary>
    public Dictionary<string, LlmRankQuota> Ranks { get; set; } = new();

    public LlmAnomalyThresholds Anomalies { get; set; } = new();

    /// <summary>Stable key for a rank (the int, so JSON round-trips cleanly).</summary>
    public static string RankKey(Rank rank) => ((int)rank).ToString(CultureInfo.InvariantCulture);

    /// <summary>Per partner agency, keyed by <see cref="AgencyKey"/>; every account of the agency gets it like a rank.
    /// A missing key means no quota, so the law chat stays closed until the AI owner sets one.</summary>
    public Dictionary<string, LlmRankQuota> PartnerAgencies { get; set; } = new();

    /// <summary>Stable key for a partner agency.</summary>
    public static string AgencyKey(PartnerAgency agency) => ((int)agency).ToString(CultureInfo.InvariantCulture);

    /// <summary>Quota of a rank; an unranked or unconfigured agent gets nothing.</summary>
    public LlmRankQuota For(Rank? rank)
        => rank is { } r && Ranks.TryGetValue(RankKey(r), out var quota) ? quota : new LlmRankQuota();

    /// <summary>Quota of an account: its rank, or for a partner account its agency.</summary>
    public LlmRankQuota For(Rank? rank, PartnerAgency? agency)
        => rank is null && agency is { } a
            ? PartnerAgencies.TryGetValue(AgencyKey(a), out var quota) ? quota : new LlmRankQuota()
            : For(rank);

    /// <summary>Starting values; the AI owner tunes them at runtime.</summary>
    public static LlmQuotaConfig Default() => new()
    {
        Ranks = new Dictionary<string, LlmRankQuota>
        {
            [RankKey(Rank.JuniorAgent)] = new() { BaseWeekly = 20_000, CarryOverPercent = 0 },
            [RankKey(Rank.SpecialAgent)] = new() { BaseWeekly = 35_000, CarryOverPercent = 0 },
            [RankKey(Rank.SeniorSpecialAgent)] = new() { BaseWeekly = 50_000, CarryOverPercent = 25 },
            [RankKey(Rank.SupervisorySpecialAgent)] = new() { BaseWeekly = 80_000, CarryOverPercent = 50 },
            [RankKey(Rank.DeputyDirector)] = new() { BaseWeekly = 120_000, CarryOverPercent = 50 },
            [RankKey(Rank.Director)] = new() { BaseWeekly = 200_000, CarryOverPercent = 50 },
        },
    };
}

/// <summary>Quota rules of one rank.</summary>
public sealed class LlmRankQuota
{
    /// <summary>Base amount available each ISO week, in quota tokens (1.000 = 1 cent of real cost).</summary>
    public long BaseWeekly { get; set; }

    /// <summary>Share of the unused rest handed to the next week (0-100), itself capped at that share of the base.</summary>
    public int CarryOverPercent { get; set; }

    /// <summary>Share of the weekly base spendable in one local day (0-100); 0 switches the daily ceiling off.</summary>
    /// <remarks>The burn-rate rule reports a runaway agent but stops nothing. This is the stop, and it is set high
    /// on purpose: it must catch a loop, not a busy afternoon.</remarks>
    public int DailyPercent { get; set; } = DefaultDailyPercent;

    public const int DefaultDailyPercent = 40;
}

/// <summary>Tunable thresholds of the four anomaly rules.</summary>
public sealed class LlmAnomalyThresholds
{
    // R1 — a single expensive request
    public bool SpikeEnabled { get; set; } = true;

    /// <summary>Multiple of the rolling average that makes one request an outlier.</summary>
    public double SpikeFactor { get; set; } = 5.0;

    public int SpikeBaselineDays { get; set; } = 14;

    /// <summary>Without a minimum baseline an agent's second request is always a "spike".</summary>
    public int SpikeMinBaselineCount { get; set; } = 20;

    /// <summary>Without an absolute floor a week of tiny spell-checks makes an ordinary answer an outlier.</summary>
    public long SpikeMinTokens { get; set; } = 2_000;

    public bool SpikeUseGlobalFallback { get; set; } = true;

    // R2 — burn rate
    public bool BurnEnabled { get; set; } = true;

    public int BurnPercent { get; set; } = 60;

    public int BurnHours { get; set; } = 6;

    // R3 — burst plus near-identical prompts
    public bool BurstEnabled { get; set; } = true;

    public int BurstMinutes { get; set; } = 10;

    public int BurstRequests { get; set; } = 8;

    public int BurstDuplicates { get; set; } = 3;

    public int BurstSimilarityPercent { get; set; } = 90;

    // R4 — weekly consumption outlier
    public bool OutlierEnabled { get; set; } = true;

    public double OutlierOwnFactor { get; set; } = 3.0;

    public double OutlierRankFactor { get; set; } = 2.5;

    public int OutlierTrailingWeeks { get; set; } = 6;

    public int OutlierMinWeeks { get; set; } = 3;

    /// <summary>Push a notification to leadership when a request trips R1 or crosses the R2 threshold.</summary>
    public bool NotifyLeadership { get; set; } = true;
}
