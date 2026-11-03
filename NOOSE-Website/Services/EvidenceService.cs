using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Evidence;

namespace NOOSE_Website.Services;

/// <summary>Evidence room: catalog items (auto-created on first use) plus the deposit/withdrawal ledger; on-hand is computed, never stored.</summary>
public class EvidenceService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    IEvidenceImageStorageService imageStorage,
    IProfileSuggestionService suggestions) : IEvidenceService
{
    private const string CasePrefix = "ASS";
    public const string NooseOwner = "NOOSE";

    /// <summary>Owner record types a caller may set.</summary>
    private static readonly string[] OwnerTypes = { NooseOwner, nameof(Agent), nameof(Person) };

    // ---- items ----

    public async Task<List<EvidenceItemDisplay>> GetItemsAsync(string? search = null, string? category = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.EvidenceItems.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(i => EF.Functions.Like(i.Name, $"%{s}%"));
        }
        if (category is not null)
        {
            // empty string asks for the uncategorised ones; null means no filter at all
            query = EvidenceCategories.IsNone(category)
                ? query.Where(i => i.Category == null || i.Category == "")
                : query.Where(i => i.Category == category);
        }
        var items = await query.OrderBy(i => i.Name).ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            // an empty id list makes ComputeOnHandAsync drop its WHERE and read the whole ledger
            return new();
        }
        var balances = await ComputeOnHandAsync(db, items.Select(i => i.Id).ToList(), cancellationToken);
        return items.Select(i => new EvidenceItemDisplay(i, balances.GetValueOrDefault(i.Id))).ToList();
    }

    public async Task<EvidenceItem?> GetItemAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.EvidenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<EvidenceItem?> GetItemByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var n = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(n))
        {
            return null;
        }
        var lower = n.ToLower();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.EvidenceItems.FirstOrDefaultAsync(i => i.Name.ToLower() == lower, cancellationToken);
    }

    public async Task<EvidenceItemDisplay?> GetItemDisplayAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.EvidenceItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }
        var balances = await ComputeOnHandAsync(db, new[] { id }, cancellationToken);
        return new EvidenceItemDisplay(item, balances.GetValueOrDefault(id));
    }

    public async Task<EvidenceItem> CreateItemAsync(EvidenceItemInput input, Stream? image, string? imageContentType, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        var name = (input.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Bitte eine Bezeichnung für das Item angeben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var lower = name.ToLower();
        if (await db.EvidenceItems.AnyAsync(i => i.Name.ToLower() == lower, cancellationToken))
        {
            throw new InvalidOperationException($"Ein Item „{name}“ existiert bereits.");
        }

        var item = new EvidenceItem
        {
            Name = name,
            Description = input.Description.TrimToNull(),
            Category = NormalizeCategory(input.Category),
        };
        await ApplyImageAsync(item, image, imageContentType, cancellationToken);
        db.EvidenceItems.Add(item);
        // stage the category so a new value is learned atomically with the item
        await StageCategoryAsync(db, item.Category, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task UpdateItemAsync(string id, EvidenceItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        var name = (input.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Bitte eine Bezeichnung für das Item angeben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Item '{id}' nicht gefunden.");

        var lower = name.ToLower();
        if (await db.EvidenceItems.AnyAsync(i => i.Id != id && i.Name.ToLower() == lower, cancellationToken))
        {
            throw new InvalidOperationException($"Ein Item „{name}“ existiert bereits.");
        }

        item.Name = name;
        item.Description = input.Description.TrimToNull();
        item.Category = NormalizeCategory(input.Category);
        await StageCategoryAsync(db, item.Category, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetItemImageAsync(string id, Stream image, string imageContentType, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Item '{id}' nicht gefunden.");

        var old = item.ImageFileName;
        // guard needs the item's state, so it runs after the load
        Permission.RequireEvidenceImageWrite(actor, itemHasImage: !string.IsNullOrEmpty(old));
        await ApplyImageAsync(item, image, imageContentType, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (!string.IsNullOrEmpty(old))
        {
            imageStorage.Delete(old); /* best effort */
        }
    }

    public async Task RemoveItemImageAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null || string.IsNullOrEmpty(item.ImageFileName))
        {
            return;
        }
        var old = item.ImageFileName;
        item.ImageFileName = null;
        item.ImageContentType = null;
        await db.SaveChangesAsync(cancellationToken);
        imageStorage.Delete(old); /* best effort */
    }

    public async Task DeleteItemAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return;
        }
        db.EvidenceItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<EvidenceItem>> GetItemTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.EvidenceItems.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(i => i.IsDeleted)
            .OrderByDescending(i => i.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreItemAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.EvidenceItems.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Item '{id}' nicht gefunden.");
        item.IsDeleted = false;
        item.DeletedAt = null;
        item.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- on-hand ----

    public async Task<int> GetOnHandAsync(string itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await ComputeOnHandAsync(db, new[] { itemId }, cancellationToken)).GetValueOrDefault(itemId);
    }

    public async Task<Dictionary<string, int>> GetOnHandManyAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ComputeOnHandAsync(db, itemIds, cancellationToken);
    }

    /// <summary>On-hand per item = Σ deposit quantities − Σ withdrawal quantities over live (non-deleted) entries; excludeEntryId leaves one entry out (for edit-time checks).</summary>
    private static async Task<Dictionary<string, int>> ComputeOnHandAsync(AppDbContext db, IReadOnlyCollection<string>? itemIds, CancellationToken cancellationToken, string? excludeEntryId = null)
    {
        // deleted entries are excluded by the global query filter on EvidenceEntries
        var query = from e in db.EvidenceEntries
                    from l in e.Lines
                    select new { l.ItemId, EntryId = e.Id, e.Type, l.Quantity };
        if (itemIds is { Count: > 0 })
        {
            query = query.Where(x => itemIds.Contains(x.ItemId));
        }
        if (excludeEntryId is not null)
        {
            query = query.Where(x => x.EntryId != excludeEntryId);
        }
        var rows = await query.ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Type == EvidenceEntryType.Deposit ? r.Quantity : -r.Quantity));
    }

    /// <summary>Blocks a withdrawal that would take more than is on hand; on-hand is computed excluding the entry being edited.</summary>
    private static async Task EnsureWithdrawalWithinStockAsync(AppDbContext db, EvidenceEntryInput input, string? excludeEntryId, CancellationToken cancellationToken)
    {
        if (input.Type != EvidenceEntryType.Withdrawal)
        {
            return;
        }

        // resolve each position to an existing item; aggregate requested quantity per item (and per new-item name)
        var perItem = new Dictionary<string, (string Display, int Want)>();
        var newItemWants = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in NormalizeLines(input.Lines))
        {
            EvidenceItem? item = null;
            if (!string.IsNullOrWhiteSpace(line.ItemId))
            {
                item = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Id == line.ItemId, cancellationToken);
            }
            if (item is null)
            {
                var name = (line.ItemName ?? string.Empty).Trim();
                var lower = name.ToLower();
                item = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Name.ToLower() == lower, cancellationToken);
                if (item is null)
                {
                    // never-stocked item: any withdrawal is impossible
                    newItemWants[name] = newItemWants.GetValueOrDefault(name) + line.Quantity;
                    continue;
                }
            }
            var cur = perItem.GetValueOrDefault(item.Id);
            perItem[item.Id] = (item.Name, cur.Want + line.Quantity);
        }

        foreach (var (name, want) in newItemWants)
        {
            if (want > 0)
            {
                throw new InvalidOperationException($"„{name}“ ist nicht eingelagert — es kann nichts entnommen werden.");
            }
        }

        if (perItem.Count == 0)
        {
            return;
        }
        var onHand = await ComputeOnHandAsync(db, perItem.Keys.ToList(), cancellationToken, excludeEntryId);
        foreach (var (itemId, v) in perItem)
        {
            var available = onHand.GetValueOrDefault(itemId);
            if (v.Want > available)
            {
                throw new InvalidOperationException(
                    $"Nicht genug „{v.Display}“ auf Lager: verfügbar {available}, angefragt {v.Want}.");
            }
        }
    }

    // ---- entries ----

    public async Task<List<EvidenceEntryDisplay>> GetEntriesAsync(EvidenceEntryType? type = null, string? itemId = null, string? category = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.EvidenceEntries.Include(e => e.Lines).ThenInclude(l => l.Item).AsQueryable();
        if (type is { } t)
        {
            query = query.Where(e => e.Type == t);
        }
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            query = query.Where(e => e.Lines.Any(l => l.ItemId == itemId));
        }
        if (category is not null)
        {
            // flat id list first, then the same EXISTS shape the itemId filter already uses
            var matching = EvidenceCategories.IsNone(category)
                ? db.EvidenceItems.Where(i => i.Category == null || i.Category == "")
                : db.EvidenceItems.Where(i => i.Category == category);
            var categoryItemIds = await matching.Select(i => i.Id).ToListAsync(cancellationToken);
            if (categoryItemIds.Count == 0)
            {
                return new();
            }
            query = query.Where(e => e.Lines.Any(l => categoryItemIds.Contains(l.ItemId)));
        }
        var list = await query.OrderByDescending(e => e.Timestamp).ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, list, cancellationToken);
    }

    public async Task<EvidenceEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.EvidenceEntries
            .Include(e => e.Lines).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<EvidenceEntryDisplay?> GetEntryDisplayAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.EvidenceEntries
            .Include(e => e.Lines).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null)
        {
            return null;
        }
        return (await ToDisplayAsync(db, new List<EvidenceEntry> { entry }, cancellationToken)).FirstOrDefault();
    }

    public async Task<List<EvidenceEntryDisplay>> GetEntriesForItemAsync(string itemId, CancellationToken cancellationToken = default)
        => await GetEntriesAsync(null, itemId, null, cancellationToken);

    public async Task<List<EvidenceEntryDisplay>> GetEntriesForOwnerAsync(string ownerType, string ownerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ownerType) || string.IsNullOrEmpty(ownerId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.EvidenceEntries
            .Include(e => e.Lines).ThenInclude(l => l.Item)
            .Where(e => e.OwnerType == ownerType && e.OwnerId == ownerId)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, list, cancellationToken);
    }

    public async Task<List<EvidenceEntryDisplay>> GetEntriesForFactionMembersAsync(string factionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(factionId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // current members only (soft-deleted memberships are past leavers, filtered globally)
        var memberIds = await db.FactionMembers
            .Where(m => m.FactionId == factionId)
            .Select(m => m.PersonId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (memberIds.Count == 0)
        {
            return new();
        }
        var list = await db.EvidenceEntries
            .Include(e => e.Lines).ThenInclude(l => l.Item)
            .Where(e => e.OwnerType == nameof(Person) && e.OwnerId != null && memberIds.Contains(e.OwnerId))
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, list, cancellationToken);
    }

    public async Task<EvidenceEntry> CreateEntryAsync(EvidenceEntryInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireEvidenceEntryWrite(actor, input.Type);
        ValidateEntry(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureWithdrawalWithinStockAsync(db, input, null, cancellationToken);
        // case-number allocation needs the caller's transaction so counter + record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var entry = new EvidenceEntry
        {
            CaseNumber = await caseNumber.NextAsync(db, CasePrefix, cancellationToken),
            Type = input.Type,
            OwnerType = input.OwnerType,
            OwnerId = input.OwnerType == NooseOwner ? null : input.OwnerId.TrimToNull(),
            HandlerAgentId = string.IsNullOrWhiteSpace(input.HandlerAgentId)
                ? (actor.GetAgentId() ?? string.Empty)
                : input.HandlerAgentId,
            Timestamp = input.Timestamp,
            Notes = input.Notes.TrimToNull(),
        };
        db.EvidenceEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var line in NormalizeLines(input.Lines))
        {
            var resolvedItemId = await ResolveOrCreateItemAsync(db, line, cancellationToken);
            db.EvidenceEntryLines.Add(new EvidenceEntryLine
            {
                EntryId = entry.Id,
                ItemId = resolvedItemId,
                Quantity = line.Quantity,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task UpdateEntryAsync(string id, EvidenceEntryInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        ValidateEntry(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.EvidenceEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Asservat-Eintrag '{id}' nicht gefunden.");

        // available stock is computed without this entry's own (old) lines
        await EnsureWithdrawalWithinStockAsync(db, input, id, cancellationToken);
        // positions are replaced wholesale, so a throw mid-loop must not leave the entry empty
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        entry.Type = input.Type;
        entry.OwnerType = input.OwnerType;
        entry.OwnerId = input.OwnerType == NooseOwner ? null : input.OwnerId.TrimToNull();
        if (!string.IsNullOrWhiteSpace(input.HandlerAgentId))
        {
            entry.HandlerAgentId = input.HandlerAgentId;
        }
        entry.Timestamp = input.Timestamp;
        entry.Notes = input.Notes.TrimToNull();

        // positions carry no user-facing identity → replace wholesale
        db.EvidenceEntryLines.RemoveRange(entry.Lines);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var line in NormalizeLines(input.Lines))
        {
            var resolvedItemId = await ResolveOrCreateItemAsync(db, line, cancellationToken);
            db.EvidenceEntryLines.Add(new EvidenceEntryLine
            {
                EntryId = entry.Id,
                ItemId = resolvedItemId,
                Quantity = line.Quantity,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteEntryAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.EvidenceEntries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null)
        {
            return;
        }
        db.EvidenceEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<EvidenceEntry>> GetEntryTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.EvidenceEntries.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(e => e.IsDeleted)
            .OrderByDescending(e => e.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreEntryAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.EvidenceEntries.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Asservat-Eintrag '{id}' nicht gefunden.");
        entry.IsDeleted = false;
        entry.DeletedAt = null;
        entry.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- clearing ----

    /// <summary>Stable note prefixes so a clearing is recognisable in the ledger and findable in the global search.</summary>
    private const string ClearingNote = "Räumung der Asservatenkammer";
    private const string ClearingCorrectionNote = "Räumung der Asservatenkammer · Korrektur Negativbestand";

    public async Task<EvidenceClearingResult> ClearStockAsync(IReadOnlyCollection<string> itemIds, string? notes, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireManage(actor);

        var ids = itemIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return EvidenceClearingResult.Empty;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // one transaction so both halves and both counter bumps commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // soft-deleted items drop out through the global query filter
        var live = await db.EvidenceItems
            .Where(i => ids.Contains(i.Id))
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
        if (live.Count == 0)
        {
            // an empty id list would make ComputeOnHandAsync scan the whole catalog
            return EvidenceClearingResult.Empty with { SkippedItems = ids.Count };
        }

        // authoritative balance at apply time; the caller's view may be stale
        var onHand = await ComputeOnHandAsync(db, live, cancellationToken);
        var surplus = live.Where(id => onHand.GetValueOrDefault(id) > 0)
            .Select(id => (ItemId: id, Quantity: onHand[id])).ToList();
        var deficit = live.Where(id => onHand.GetValueOrDefault(id) < 0)
            .Select(id => (ItemId: id, Quantity: -onHand[id])).ToList();

        var skipped = ids.Count - surplus.Count - deficit.Count;
        if (surplus.Count == 0 && deficit.Count == 0)
        {
            return EvidenceClearingResult.Empty with { SkippedItems = skipped };
        }

        var note = notes.TrimToNull();
        var handlerId = actor.GetAgentId() ?? string.Empty;
        // one instant ties both halves together on the ledger
        var timestamp = DateTime.UtcNow;

        var withdrawal = surplus.Count == 0 ? null : await BookClearingEntryAsync(
            db, EvidenceEntryType.Withdrawal, surplus, handlerId, timestamp, Compose(ClearingNote, note), cancellationToken);

        // Type is per entry and Quantity is positive, so a negative balance needs its own deposit
        var correction = deficit.Count == 0 ? null : await BookClearingEntryAsync(
            db, EvidenceEntryType.Deposit, deficit, handlerId, timestamp, Compose(ClearingCorrectionNote, note), cancellationToken);

        await tx.CommitAsync(cancellationToken);

        return new EvidenceClearingResult(
            surplus.Count, surplus.Sum(p => p.Quantity),
            deficit.Count, deficit.Sum(p => p.Quantity),
            skipped,
            withdrawal?.Id, withdrawal?.CaseNumber,
            correction?.Id, correction?.CaseNumber);
    }

    /// <summary>Books one NOOSE-owned clearing entry whose positions carry each item's whole balance.</summary>
    private async Task<EvidenceEntry> BookClearingEntryAsync(AppDbContext db, EvidenceEntryType type, IReadOnlyList<(string ItemId, int Quantity)> positions, string handlerId, DateTime timestamp, string? note, CancellationToken cancellationToken)
    {
        var entry = new EvidenceEntry
        {
            CaseNumber = await caseNumber.NextAsync(db, CasePrefix, cancellationToken),
            Type = type,
            OwnerType = NooseOwner,
            OwnerId = null,
            HandlerAgentId = handlerId,
            Timestamp = timestamp,
            Notes = note,
        };
        foreach (var p in positions)
        {
            entry.Lines.Add(new EvidenceEntryLine { ItemId = p.ItemId, Quantity = p.Quantity });
        }
        db.EvidenceEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    private static string Compose(string prefix, string? note)
        => note is null ? prefix : $"{prefix} · {note}";

    // ---- helpers ----

    /// <summary>Enrich entries with resolved owner (NOOSE sentinel / Agent / Person), handler codename and item positions.</summary>
    private static async Task<List<EvidenceEntryDisplay>> ToDisplayAsync(AppDbContext db, List<EvidenceEntry> list, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
        {
            return new();
        }

        var refs = list
            .Where(e => e.OwnerType != NooseOwner && !string.IsNullOrEmpty(e.OwnerId))
            .Select(e => (e.OwnerType, e.OwnerId!))
            .Distinct()
            .ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken);

        var handlerIds = list.Select(e => e.HandlerAgentId).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var codenames = await db.Users
            .Where(u => handlerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);

        return list.Select(e =>
        {
            string ownerDisplay;
            string? ownerHref;
            if (e.OwnerType == NooseOwner)
            {
                ownerDisplay = "FIB";
                ownerHref = null;
            }
            else
            {
                resolved.TryGetValue((e.OwnerType, e.OwnerId ?? string.Empty), out var r);
                ownerDisplay = string.IsNullOrWhiteSpace(r.Display) ? "(unbekannt)" : r.Display;
                ownerHref = r.Href;
            }

            var handler = codenames.TryGetValue(e.HandlerAgentId ?? string.Empty, out var cn) && !string.IsNullOrWhiteSpace(cn)
                ? cn : "(unbekannt)";

            var lines = e.Lines.Select(l => new EvidenceLineDisplay(
                l.ItemId,
                l.Item?.Name ?? "(gelöschtes Item)",
                !string.IsNullOrEmpty(l.Item?.ImageFileName),
                l.Quantity)).ToList();

            return new EvidenceEntryDisplay(e, ownerDisplay, ownerHref, handler, lines);
        }).ToList();
    }

    /// <summary>Resolve an existing item by id/name or auto-create a new catalog item from the position's name.</summary>
    private async Task<string> ResolveOrCreateItemAsync(AppDbContext db, EvidenceLineInput line, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(line.ItemId)
            && await db.EvidenceItems.AnyAsync(i => i.Id == line.ItemId, cancellationToken))
        {
            return line.ItemId!;
        }

        var name = (line.ItemName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Jede Position braucht ein Item.");
        }

        var lower = name.ToLower();
        var match = await db.EvidenceItems.FirstOrDefaultAsync(i => i.Name.ToLower() == lower, cancellationToken);
        if (match is not null)
        {
            // an existing item keeps its own category; only the item editor may change it
            return match.Id;
        }

        var created = new EvidenceItem { Name = name, Category = NormalizeCategory(line.NewItemCategory) };
        db.EvidenceItems.Add(created);
        await StageCategoryAsync(db, created.Category, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); // persist so later positions in this entry dedupe against it
        return created.Id;
    }

    /// <summary>Trims the category and rejects what the catalog could not hold; a raw DbUpdateException would kill the circuit.</summary>
    private static string? NormalizeCategory(string? category)
    {
        var value = category.TrimToNull();
        if (value is { Length: > 300 })
        {
            throw new InvalidOperationException("Die Kategorie darf höchstens 300 Zeichen lang sein.");
        }
        return value;
    }

    /// <summary>Learns a new category into the suggestion catalog; the caller persists it.</summary>
    private async Task StageCategoryAsync(AppDbContext db, string? category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }
        await suggestions.StageAsync(db, SuggestionType.EvidenceCategory, [category], cancellationToken);
    }

    private static List<EvidenceLineInput> NormalizeLines(IEnumerable<EvidenceLineInput> lines)
        => lines
            .Where(l => l.Quantity > 0 && (!string.IsNullOrWhiteSpace(l.ItemId) || !string.IsNullOrWhiteSpace(l.ItemName)))
            .ToList();

    private static void ValidateEntry(EvidenceEntryInput input)
    {
        if (!OwnerTypes.Contains(input.OwnerType))
        {
            throw new InvalidOperationException("Ungültiger Besitzertyp.");
        }
        if (input.OwnerType != NooseOwner && string.IsNullOrWhiteSpace(input.OwnerId))
        {
            throw new InvalidOperationException("Bitte einen Besitzer (Agent oder Person) auswählen.");
        }
        if (NormalizeLines(input.Lines).Count == 0)
        {
            throw new InvalidOperationException("Bitte mindestens eine Position mit Menge angeben.");
        }
    }

    private async Task ApplyImageAsync(EvidenceItem item, Stream? image, string? imageContentType, CancellationToken cancellationToken)
    {
        if (image is null || string.IsNullOrWhiteSpace(imageContentType))
        {
            return;
        }
        if (!imageStorage.IsAllowedType(imageContentType))
        {
            throw new InvalidOperationException("Nicht unterstütztes Bildformat.");
        }
        item.ImageFileName = await imageStorage.SaveAsync(image, imageContentType, cancellationToken);
        item.ImageContentType = imageContentType;
    }

    private static void RequireManage(ClaimsPrincipal actor)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
    }
}
