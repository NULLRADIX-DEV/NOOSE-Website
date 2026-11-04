using System.Security.Claims;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;

namespace NOOSE_Website.Services;

/// <summary>The catalog of financeable positions; leadership maintains it, agents request from it.</summary>
public interface IFinancingCatalogService
{
    Task<List<FinancingItem>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Active positions only, in display order.</summary>
    Task<List<FinancingItem>> GetActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Active positions a rank may request in its home division; an unranked agent sees nothing.</summary>
    Task<List<FinancingItem>> GetActiveForAsync(Rank? rank, Division? division, CancellationToken cancellationToken = default);

    Task<FinancingItem?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<FinancingItem> CreateAsync(FinancingItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateAsync(string id, FinancingItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
