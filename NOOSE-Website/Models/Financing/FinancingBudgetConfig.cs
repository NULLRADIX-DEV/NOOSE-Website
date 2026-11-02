using System.Globalization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Financing;

/// <summary>Per-rank monthly funding budget and carry-over share. Stored as JSON in a SystemSetting row.</summary>
public sealed class FinancingBudgetConfig
{
    /// <summary>Keyed by <see cref="RankKey"/>; a missing key means no budget for that rank.</summary>
    public Dictionary<string, FinancingRankBudget> Ranks { get; set; } = new();

    /// <summary>Stable key for a rank (the int, so JSON round-trips cleanly).</summary>
    public static string RankKey(Rank rank) => ((int)rank).ToString(CultureInfo.InvariantCulture);

    /// <summary>Budget of a rank; an unranked or unconfigured agent gets nothing.</summary>
    public FinancingRankBudget For(Rank? rank)
        => rank is { } r && Ranks.TryGetValue(RankKey(r), out var budget) ? budget : new FinancingRankBudget();

    /// <summary>Starting values; leadership tunes them at runtime.</summary>
    public static FinancingBudgetConfig Default() => new()
    {
        Ranks = new Dictionary<string, FinancingRankBudget>
        {
            [RankKey(Rank.Trainee)] = new() { BaseMonthly = 25_000m, CarryOverPercent = 0 },
            [RankKey(Rank.SpecialAgent)] = new() { BaseMonthly = 50_000m, CarryOverPercent = 0 },
            [RankKey(Rank.SeniorSpecialAgent)] = new() { BaseMonthly = 75_000m, CarryOverPercent = 25 },
            [RankKey(Rank.SpecialAgentInCharge)] = new() { BaseMonthly = 150_000m, CarryOverPercent = 50 },
            [RankKey(Rank.DeputyDirector)] = new() { BaseMonthly = 200_000m, CarryOverPercent = 50 },
            [RankKey(Rank.Director)] = new() { BaseMonthly = 250_000m, CarryOverPercent = 50 },
        },
    };
}

/// <summary>Budget rules of one rank.</summary>
public sealed class FinancingRankBudget
{
    /// <summary>Base amount available each calendar month.</summary>
    public decimal BaseMonthly { get; set; }

    /// <summary>Share of the unused rest handed to the next month (0-100); only the direct successor may use it.</summary>
    public int CarryOverPercent { get; set; }
}
