using System.Globalization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Food;

/// <summary>Weekly food and drink limit per division and rank. Stored as JSON in a SystemSetting row.</summary>
public sealed class FoodLimitConfig
{
    /// <summary>Base table keyed by <see cref="RankKey"/>; a missing key means no allowance for that rank.</summary>
    public Dictionary<string, FoodRankLimit> Ranks { get; set; } = new();

    /// <summary>Own tables keyed by <see cref="DivisionKey"/>, then <see cref="RankKey"/>; a missing entry falls back to <see cref="Ranks"/>.</summary>
    public Dictionary<string, Dictionary<string, FoodRankLimit>> Divisions { get; set; } = new();

    /// <summary>Stable key for a rank (the int, so JSON round-trips cleanly).</summary>
    public static string RankKey(Rank rank) => ((int)rank).ToString(CultureInfo.InvariantCulture);

    /// <summary>Stable key for a division (the int).</summary>
    public static string DivisionKey(Division division) => ((int)division).ToString(CultureInfo.InvariantCulture);

    /// <summary>Limit of a rank in the base table; an unranked or unconfigured agent gets nothing.</summary>
    public FoodRankLimit For(Rank? rank)
        => rank is { } r && Ranks.TryGetValue(RankKey(r), out var limit) ? limit : new FoodRankLimit();

    /// <summary>Limit of a rank in its home division, else the base table.</summary>
    public FoodRankLimit For(Rank? rank, Division? division)
        => rank is { } r && division is { } d
            && Divisions.TryGetValue(DivisionKey(d), out var table)
            && table.TryGetValue(RankKey(r), out var limit)
                ? limit
                : For(rank);

    /// <summary>Starting values; leadership tunes them at runtime.</summary>
    public static FoodLimitConfig Default() => new()
    {
        Ranks = RankDisplay.All.ToDictionary(RankKey, _ => new FoodRankLimit { FoodPerWeek = 10, DrinkPerWeek = 10 }),
    };
}

/// <summary>Weekly allowance of one rank.</summary>
public sealed class FoodRankLimit
{
    /// <summary>Food pieces per calendar week.</summary>
    public int FoodPerWeek { get; set; }

    /// <summary>Drink pieces per calendar week.</summary>
    public int DrinkPerWeek { get; set; }

    public int For(FoodKind kind) => kind == FoodKind.Drink ? DrinkPerWeek : FoodPerWeek;
}
