using NOOSE_Website.Models.Dashboard;

namespace NOOSE_Website.Models.Statistics;

/// <summary>Aggregated food-request statistics for the statistics page.</summary>
public record FoodStatistics(
    int IssuedFood,
    int IssuedDrink,
    int RequestCount,
    int OpenCount,
    int DecidedCount,
    int RejectedCount,
    ChartGrid IssuedOverTime,
    IReadOnlyList<DistributionSegment> ByStatus,
    IReadOnlyList<DistributionSegment> TopItems,
    ChartGrid FoodUtilisationByRank,
    ChartGrid DrinkUtilisationByRank)
{
    /// <summary>Share of decided requests that were rejected (0-1); withdrawn ones were never decided.</summary>
    public double RejectionRate => DecidedCount <= 0 ? 0d : (double)RejectedCount / DecidedCount;

    public static FoodStatistics Empty { get; } =
        new(0, 0, 0, 0, 0, 0, ChartGrid.Empty, [], [], ChartGrid.Empty, ChartGrid.Empty);
}
