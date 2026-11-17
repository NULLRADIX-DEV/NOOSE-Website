using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFoodCatalogService" />
public class FoodCatalogService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEvidenceService evidence) : IFoodCatalogService
{
    public Task<List<FoodItemDisplay>> GetAllAsync(CancellationToken cancellationToken = default)
        => LoadAsync(activeOnly: false, cancellationToken);

    public Task<List<FoodItemDisplay>> GetActiveAsync(CancellationToken cancellationToken = default)
        => LoadAsync(activeOnly: true, cancellationToken);

    public async Task<FoodItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.FoodItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is not null)
        {
            // separate lookup, so a deleted evidence item leaves the position editable
            item.EvidenceItem = await db.EvidenceItems.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == item.EvidenceItemId, cancellationToken);
        }
        return item;
    }

    public async Task<FoodItem> CreateAsync(FoodItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireEvidenceItemFreeAsync(db, input.EvidenceItemId, null, cancellationToken);

        var item = new FoodItem();
        Apply(item, input);
        db.FoodItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task UpdateAsync(string id, FoodItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.FoodItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Verpflegungsposition '{id}' nicht gefunden.");
        await RequireEvidenceItemFreeAsync(db, input.EvidenceItemId, id, cancellationToken);

        Apply(item, input);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.FoodItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return;
        }
        // soft-delete only: filed request lines keep pointing here
        db.FoodItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<FoodItemDisplay>> LoadAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // no Include: the required nav would inner-join away positions whose item is deleted
        var query = db.FoodItems.AsNoTracking().AsQueryable();
        if (activeOnly)
        {
            query = query.Where(i => i.IsActive);
        }
        var items = await query.ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return new();
        }
        var evidenceIds = items.Select(i => i.EvidenceItemId).Distinct().ToList();
        var evidenceItems = await db.EvidenceItems.AsNoTracking()
            .Where(e => evidenceIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);
        foreach (var item in items)
        {
            item.EvidenceItem = evidenceItems.GetValueOrDefault(item.EvidenceItemId);
        }
        var stock = await evidence.GetOnHandManyAsync(evidenceIds, cancellationToken);
        return items
            .Select(i => new FoodItemDisplay(i,
                i.EvidenceItem?.Name ?? "(gelöschtes Asservat)",
                !string.IsNullOrEmpty(i.EvidenceItem?.ImageFileName),
                stock.GetValueOrDefault(i.EvidenceItemId)))
            // an item deleted in the evidence room can no longer be issued
            .Where(d => !activeOnly || d.Item.EvidenceItem is not null)
            .OrderBy(d => d.Item.Sorting).ThenBy(d => d.Item.Kind).ThenBy(d => d.Name)
            .ToList();
    }

    private static void Apply(FoodItem item, FoodItemInput input)
    {
        item.EvidenceItemId = input.EvidenceItemId;
        item.Kind = input.Kind;
        item.Description = input.Description.TrimToNull();
        item.MaxQuantity = input.MaxQuantity;
        item.IsActive = input.IsActive;
        item.Sorting = input.Sorting;
    }

    /// <summary>One live position per evidence item, so stock and catalog never disagree about what a thing is.</summary>
    private static async Task RequireEvidenceItemFreeAsync(AppDbContext db, string evidenceItemId, string? exceptId,
        CancellationToken cancellationToken)
    {
        var name = await db.EvidenceItems.AsNoTracking()
            .Where(i => i.Id == evidenceItemId).Select(i => i.Name)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Der gewählte Gegenstand existiert in der Asservatenkammer nicht mehr.");
        var taken = await db.FoodItems
            .AnyAsync(i => i.EvidenceItemId == evidenceItemId && (exceptId == null || i.Id != exceptId), cancellationToken);
        if (taken)
        {
            throw new InvalidOperationException($"„{name}“ steht bereits im Verpflegungskatalog.");
        }
    }

    private static void Validate(FoodItemInput input)
    {
        if (string.IsNullOrWhiteSpace(input.EvidenceItemId))
        {
            throw new InvalidOperationException("Bitte einen Gegenstand aus der Asservatenkammer wählen.");
        }
        if (input.Description is { Length: > 1000 })
        {
            throw new InvalidOperationException("Die Beschreibung darf höchstens 1000 Zeichen lang sein.");
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
