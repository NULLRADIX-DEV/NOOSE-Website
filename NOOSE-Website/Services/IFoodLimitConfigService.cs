using System.Security.Claims;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <summary>Weekly food and drink limits per division and rank, kept as JSON in one SystemSetting row.</summary>
public interface IFoodLimitConfigService
{
    /// <summary>Current rules, cached briefly.</summary>
    Task<FoodLimitConfig> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Current rules straight from the database, for the editor.</summary>
    Task<FoodLimitConfig> GetEditableAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(FoodLimitConfig config, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
