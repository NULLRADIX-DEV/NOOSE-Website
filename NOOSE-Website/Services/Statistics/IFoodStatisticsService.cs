using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <summary>Aggregated food-request statistics (issued pieces over time, stages, top positions, weekly limit utilisation).</summary>
public interface IFoodStatisticsService
{
    Task<FoodStatistics> GetAsync(StatisticsScope scope, CancellationToken cancellationToken = default);
}
