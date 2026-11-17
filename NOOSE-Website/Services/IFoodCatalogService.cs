using System.Security.Claims;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <summary>The catalog of requestable food and drinks, each tied to an evidence-room item; leadership maintains it, agents request from it.</summary>
public interface IFoodCatalogService
{
    /// <summary>All positions with item name and stock, in display order.</summary>
    Task<List<FoodItemDisplay>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Active positions only, in display order.</summary>
    Task<List<FoodItemDisplay>> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<FoodItem?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<FoodItem> CreateAsync(FoodItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateAsync(string id, FoodItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
