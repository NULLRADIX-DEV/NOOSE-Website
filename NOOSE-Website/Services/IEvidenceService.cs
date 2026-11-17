using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Evidence;

namespace NOOSE_Website.Services;

/// <summary>Manages the evidence room: catalog items with computed on-hand plus the deposit/withdrawal ledger.</summary>
public interface IEvidenceService
{
    // ---- items ----
    /// <summary>Catalog items with on-hand; category null means no filter, <see cref="EvidenceCategories.None"/> asks for the uncategorised ones.</summary>
    Task<List<EvidenceItemDisplay>> GetItemsAsync(string? search = null, string? category = null, CancellationToken cancellationToken = default);
    Task<EvidenceItem?> GetItemAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Finds a live item by exact (case-insensitive) name; used to attach an image to a just-auto-created item.</summary>
    Task<EvidenceItem?> GetItemByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<EvidenceItemDisplay?> GetItemDisplayAsync(string id, CancellationToken cancellationToken = default);
    Task<EvidenceItem> CreateItemAsync(EvidenceItemInput input, Stream? image, string? imageContentType, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateItemAsync(string id, EvidenceItemInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task SetItemImageAsync(string id, Stream image, string imageContentType, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RemoveItemImageAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<EvidenceItem>> GetItemTrashAsync(CancellationToken cancellationToken = default);
    Task RestoreItemAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Current on-hand quantity of one item (Σ deposits − Σ withdrawals over live entries).</summary>
    Task<int> GetOnHandAsync(string itemId, CancellationToken cancellationToken = default);
    /// <summary>Batched on-hand lookup for the given items (missing ids resolve to 0).</summary>
    Task<Dictionary<string, int>> GetOnHandManyAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken = default);

    // ---- entries (ledger) ----
    /// <summary>Ledger entries; category keeps an entry when any of its positions carries that item category.</summary>
    Task<List<EvidenceEntryDisplay>> GetEntriesAsync(EvidenceEntryType? type = null, string? itemId = null, string? category = null, CancellationToken cancellationToken = default);
    Task<EvidenceEntry?> GetEntryAsync(string id, CancellationToken cancellationToken = default);
    Task<EvidenceEntryDisplay?> GetEntryDisplayAsync(string id, CancellationToken cancellationToken = default);
    Task<EvidenceEntry> CreateEntryAsync(EvidenceEntryInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Books an entry inside the caller's context and transaction, so a linked record commits with it.</summary>
    Task<EvidenceEntry> CreateEntryAsync(AppDbContext db, EvidenceEntryInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateEntryAsync(string id, EvidenceEntryInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteEntryAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<EvidenceEntry>> GetEntryTrashAsync(CancellationToken cancellationToken = default);
    Task RestoreEntryAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<EvidenceEntryDisplay>> GetEntriesForItemAsync(string itemId, CancellationToken cancellationToken = default);
    /// <summary>Entries whose owner is the given record (e.g. a person), newest first.</summary>
    Task<List<EvidenceEntryDisplay>> GetEntriesForOwnerAsync(string ownerType, string ownerId, CancellationToken cancellationToken = default);
    /// <summary>Entries owned by any current member (person) of the faction, newest first.</summary>
    Task<List<EvidenceEntryDisplay>> GetEntriesForFactionMembersAsync(string factionId, CancellationToken cancellationToken = default);

    // ---- clearing ----
    /// <summary>Books every selected item's whole balance to zero: one withdrawal for positive stock plus, where needed, one deposit correcting negative stock. Quantities are recomputed server-side, never taken from the caller.</summary>
    Task<EvidenceClearingResult> ClearStockAsync(IReadOnlyCollection<string> itemIds, string? notes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
