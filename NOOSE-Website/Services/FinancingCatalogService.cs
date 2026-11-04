using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFinancingCatalogService" />
public class FinancingCatalogService(
    IDbContextFactory<AppDbContext> dbFactory,
    IProfileSuggestionService suggestions) : IFinancingCatalogService
{
    public async Task<List<FinancingItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Ordered(db.FinancingItems.AsNoTracking()).ToListAsync(cancellationToken);
    }

    public async Task<List<FinancingItem>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Ordered(db.FinancingItems.AsNoTracking().Where(i => i.IsActive)).ToListAsync(cancellationToken);
    }

    public async Task<List<FinancingItem>> GetActiveForAsync(Rank? rank, Division? division, CancellationToken cancellationToken = default)
    {
        if (rank is null)
        {
            // no rank, no entitlement
            return new();
        }
        // small catalog, filtered in memory
        return (await GetActiveAsync(cancellationToken))
            .Where(i => FinancingItemRanks.MayRequest(i, rank, division))
            .ToList();
    }

    public async Task<FinancingItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FinancingItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<FinancingItem> CreateAsync(FinancingItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireNameFreeAsync(db, input.Name, null, cancellationToken);

        var item = new FinancingItem();
        Apply(item, input);
        db.FinancingItems.Add(item);
        // stage the category so a new value is learned atomically with the position
        await StageCategoryAsync(db, item.Category, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task UpdateAsync(string id, FinancingItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.FinancingItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Finanzierungsposition '{id}' nicht gefunden.");
        await RequireNameFreeAsync(db, input.Name, id, cancellationToken);

        Apply(item, input);
        await StageCategoryAsync(db, item.Category, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.FinancingItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return;
        }
        // soft-delete only: filed request lines keep pointing here, and their snapshot stays readable
        db.FinancingItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<FinancingItem> Ordered(IQueryable<FinancingItem> query)
        => query.OrderBy(i => i.Sorting).ThenBy(i => i.Name);

    private static void Apply(FinancingItem item, FinancingItemInput input)
    {
        item.Name = input.Name.Trim();
        item.Category = input.Category.TrimToNull();
        item.Description = input.Description.TrimToNull();
        item.UnitPrice = input.UnitPrice;
        item.SubsidyPercent = input.SubsidyPercent;
        item.MinimumRank = input.MinimumRank;
        // same as base: stored empty
        item.MinimumRankCtd = input.MinimumRankCtd == input.MinimumRank ? null : input.MinimumRankCtd;
        item.MinimumRankHrt = input.MinimumRankHrt == input.MinimumRank ? null : input.MinimumRankHrt;
        item.MaxQuantity = input.MaxQuantity;
        item.IsActive = input.IsActive;
        item.Sorting = input.Sorting;
    }

    private async Task StageCategoryAsync(AppDbContext db, string? category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }
        await suggestions.StageAsync(db, SuggestionType.FinancingCategory, [category], cancellationToken);
    }

    /// <summary>Names are unique among live positions; a soft-deleted one must not block reuse.</summary>
    private static async Task RequireNameFreeAsync(AppDbContext db, string name, string? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        var taken = await db.FinancingItems
            .AnyAsync(i => i.Name == trimmed && (exceptId == null || i.Id != exceptId), cancellationToken);
        if (taken)
        {
            throw new InvalidOperationException($"Eine Finanzierungsposition „{trimmed}“ existiert bereits.");
        }
    }

    private static void Validate(FinancingItemInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new InvalidOperationException("Bitte eine Bezeichnung angeben.");
        }
        if (input.Name.Trim().Length > 200)
        {
            throw new InvalidOperationException("Die Bezeichnung darf höchstens 200 Zeichen lang sein.");
        }
        if (input.UnitPrice <= 0)
        {
            throw new InvalidOperationException("Bitte einen Einzelpreis größer 0 angeben.");
        }
        // whole dollars only: a fractional price could round the subsidy above the goods value,
        // which would make the agent's own share negative
        if (input.UnitPrice != decimal.Truncate(input.UnitPrice))
        {
            throw new InvalidOperationException("Der Einzelpreis muss ein ganzer Dollar-Betrag sein.");
        }
        if (input.SubsidyPercent is < 1 or > 100)
        {
            throw new InvalidOperationException("Der Zuschuss-Anteil muss zwischen 1 und 100 % liegen.");
        }
        if (input.MaxQuantity < 1)
        {
            throw new InvalidOperationException("Die maximale Menge muss mindestens 1 betragen.");
        }
    }

    private static void RequireManage(ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
    }
}
