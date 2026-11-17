using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Food;

namespace NOOSE_Website.Services;

/// <summary>Central food-request visibility rule: an agent sees only their own requests, readers of everything see all. Always filter through here.</summary>
public static class FoodVisibility
{
    /// <summary>Filters a request query to the entries visible to the caller.</summary>
    public static IQueryable<FoodRequest> OnlyVisible(this IQueryable<FoodRequest> query, bool mayAll, string? meId)
    {
        if (mayAll)
        {
            return query;
        }
        if (string.IsNullOrEmpty(meId))
        {
            // fail-closed: no agent context
            return query.Where(_ => false);
        }
        return query.Where(r => r.AgentId == meId);
    }

    /// <summary>True if the request exists and is visible to the caller.</summary>
    public static async Task<bool> IsVisibleAsync(AppDbContext db, string requestId, bool mayAll, string? meId,
        CancellationToken cancellationToken = default)
    {
        if (mayAll)
        {
            return await db.FoodRequests.AnyAsync(r => r.Id == requestId, cancellationToken);
        }
        return !string.IsNullOrEmpty(meId)
            && await db.FoodRequests.AnyAsync(r => r.Id == requestId && r.AgentId == meId, cancellationToken);
    }

    /// <summary>Visible request ids from a candidate set (for batch reference resolution).</summary>
    public static async Task<HashSet<string>> VisibleIdsAsync(AppDbContext db, IReadOnlyCollection<string> requestIds,
        bool mayAll, string? meId, CancellationToken cancellationToken = default)
    {
        if (requestIds.Count == 0)
        {
            return new();
        }
        if (mayAll)
        {
            return requestIds.ToHashSet();
        }
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }
        var visible = await db.FoodRequests
            .Where(r => requestIds.Contains(r.Id) && r.AgentId == meId)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        return visible.ToHashSet();
    }
}
