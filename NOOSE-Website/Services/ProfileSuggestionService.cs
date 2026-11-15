using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IProfileSuggestionService" />
public class ProfileSuggestionService(IDbContextFactory<AppDbContext> dbFactory) : IProfileSuggestionService
{
    public async Task<IReadOnlyList<string>> GetAsync(SuggestionType type, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ProfileSuggestions
            .Where(v => v.Type == type)
            .OrderBy(v => v.Value)
            .Select(v => v.Value)
            .ToListAsync(cancellationToken);
    }

    public async Task StageAsync(AppDbContext db, SuggestionType type, IEnumerable<string> values, CancellationToken cancellationToken = default)
    {
        // trim, drop empties, dedupe case-insensitively
        var candidates = values
            .Select(w => w?.Trim() ?? string.Empty)
            .Where(w => w.Length > 0)
            .GroupBy(w => w.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        // find already-present values so only genuinely new ones are added
        var candidatesLower = candidates.Select(w => w.ToLowerInvariant()).ToList();
        var exists = await db.ProfileSuggestions
            .Where(v => v.Type == type && candidatesLower.Contains(v.Value.ToLower()))
            .Select(v => v.Value)
            .ToListAsync(cancellationToken);
        var existsSet = exists.Select(w => w.ToLowerInvariant()).ToHashSet();

        foreach (var value in candidates)
        {
            // set grows as we go, catching duplicates within one call
            if (existsSet.Add(value.ToLowerInvariant()))
            {
                // stage only; caller persists in the same SaveChanges
                db.ProfileSuggestions.Add(new ProfileSuggestion { Type = type, Value = value });
            }
        }
    }

    public async Task<IReadOnlyList<SuggestionEntry>> GetEntriesAsync(SuggestionType type, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ProfileSuggestions
            .Where(v => v.Type == type)
            .OrderBy(v => v.Value)
            .Select(v => new { v.Id, v.Value })
            .ToListAsync(cancellationToken);
        var counts = await UsageCountsAsync(db, type, cancellationToken);
        return rows
            .Select(r => new SuggestionEntry(r.Id, type, r.Value, counts.GetValueOrDefault(r.Value)))
            .ToList();
    }

    public async Task CreateAsync(SuggestionType type, string value, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        value = (value ?? string.Empty).Trim();
        if (value.Length is 0 or > 300)
        {
            throw new InvalidOperationException("Der Wert darf nicht leer und höchstens 300 Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ProfileSuggestions.AnyAsync(v => v.Type == type && v.Value.ToLower() == value.ToLower(), cancellationToken))
        {
            throw new InvalidOperationException($"Der Wert „{value}“ existiert bereits.");
        }

        db.ProfileSuggestions.Add(new ProfileSuggestion { Type = type, Value = value });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RenameAsync(string entryId, string newValue, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        // bulk propagation bypasses the SaveChanges read-only barrier, so write access is enforced here
        Permission.RequireWriteAccess(actor);

        newValue = (newValue ?? string.Empty).Trim();
        if (newValue.Length is 0 or > 300)
        {
            throw new InvalidOperationException("Der Wert darf nicht leer und höchstens 300 Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ProfileSuggestions.FirstOrDefaultAsync(v => v.Id == entryId, cancellationToken)
            ?? throw new InvalidOperationException("Wert nicht gefunden.");
        if (await db.ProfileSuggestions.AnyAsync(v => v.Id != entryId && v.Type == entry.Type && v.Value.ToLower() == newValue.ToLower(), cancellationToken))
        {
            throw new InvalidOperationException($"Der Wert „{newValue}“ existiert bereits.");
        }
        var oldValue = entry.Value;
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        entry.Value = newValue;
        await db.SaveChangesAsync(cancellationToken);
        await PropagateRenameAsync(db, entry.Type, oldValue, newValue, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(string entryId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.ProfileSuggestions.FirstOrDefaultAsync(v => v.Id == entryId, cancellationToken)
            ?? throw new InvalidOperationException("Wert nicht gefunden.");

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ProfileSuggestions.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        await PropagateDeleteAsync(db, entry.Type, entry.Value, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SuggestionEntry>> GetActivityKindsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AgentActivities.IgnoreQueryFilters()
            .Where(a => a.Kind != null && a.Kind != "")
            .GroupBy(a => a.Kind)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        // no catalog here: the kind text itself is the key
        return rows
            .OrderBy(r => r.Value)
            .Select(r => new SuggestionEntry(r.Value!, null, r.Value!, r.Count))
            .ToList();
    }

    public async Task RenameActivityKindAsync(string oldKind, string newKind, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        newKind = (newKind ?? string.Empty).Trim();
        if (newKind.Length is 0 or > 100)
        {
            throw new InvalidOperationException("Die Art darf nicht leer und höchstens 100 Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.AgentActivities.IgnoreQueryFilters()
            .Where(a => a.Kind == oldKind)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Kind, newKind), cancellationToken);
    }

    public async Task DeleteActivityKindAsync(string kind, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.AgentActivities.IgnoreQueryFilters()
            .Where(a => a.Kind == kind)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Kind, (string?)null), cancellationToken);
    }

    // bulk updates bypass audit stamping on purpose: one catalog edit would flood the log
    private static async Task PropagateRenameAsync(AppDbContext db, SuggestionType type, string oldValue, string newValue, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case SuggestionType.Weapon:
                await db.PersonWeapons.IgnoreQueryFilters().Where(w => w.Text == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Text, newValue), cancellationToken);
                await db.FactionWeaponStocks.IgnoreQueryFilters().Where(w => w.Designation == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Designation, newValue), cancellationToken);
                break;
            case SuggestionType.Vehicle:
                await db.PersonVehicles.IgnoreQueryFilters().Where(v => v.Designation == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.Designation, newValue), cancellationToken);
                break;
            case SuggestionType.Location:
                await db.PersonLocations.IgnoreQueryFilters().Where(o => o.Text == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Text, newValue), cancellationToken);
                break;
            case SuggestionType.Inventory:
                await db.FactionInventories.IgnoreQueryFilters().Where(l => l.Designation == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Designation, newValue), cancellationToken);
                break;
            case SuggestionType.Kind:
                await db.Factions.IgnoreQueryFilters().Where(f => f.Kind == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.Kind, newValue), cancellationToken);
                break;
            case SuggestionType.PartyRole:
                await db.PartyMembers.IgnoreQueryFilters().Where(m => m.Role == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, newValue), cancellationToken);
                break;
            case SuggestionType.OperationType:
                await db.Operations.IgnoreQueryFilters().Where(o => o.Type == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Type, newValue), cancellationToken);
                break;
            case SuggestionType.CaseType:
                await db.Cases.IgnoreQueryFilters().Where(c => c.Type == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Type, newValue), cancellationToken);
                break;
            case SuggestionType.FinancingCategory:
                // only the live catalog: a filed request line carries a frozen snapshot and must not move
                await db.FinancingItems.IgnoreQueryFilters().Where(i => i.Category == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Category, newValue), cancellationToken);
                break;
            case SuggestionType.EvidenceCategory:
                await db.EvidenceItems.IgnoreQueryFilters().Where(i => i.Category == oldValue)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Category, newValue), cancellationToken);
                break;
        }
    }

    // child rows are removed, scalar fields nulled; bulk ops bypass audit on purpose (see rename)
    private static async Task PropagateDeleteAsync(AppDbContext db, SuggestionType type, string value, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case SuggestionType.Weapon:
                await db.PersonWeapons.IgnoreQueryFilters().Where(w => w.Text == value)
                    .ExecuteDeleteAsync(cancellationToken);
                await db.FactionWeaponStocks.IgnoreQueryFilters().Where(w => w.Designation == value)
                    .ExecuteDeleteAsync(cancellationToken);
                break;
            case SuggestionType.Vehicle:
                await db.PersonVehicles.IgnoreQueryFilters().Where(v => v.Designation == value)
                    .ExecuteDeleteAsync(cancellationToken);
                break;
            case SuggestionType.Location:
                await db.PersonLocations.IgnoreQueryFilters().Where(o => o.Text == value)
                    .ExecuteDeleteAsync(cancellationToken);
                break;
            case SuggestionType.Inventory:
                await db.FactionInventories.IgnoreQueryFilters().Where(l => l.Designation == value)
                    .ExecuteDeleteAsync(cancellationToken);
                break;
            case SuggestionType.Kind:
                await db.Factions.IgnoreQueryFilters().Where(f => f.Kind == value)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.Kind, (string?)null), cancellationToken);
                break;
            case SuggestionType.PartyRole:
                await db.PartyMembers.IgnoreQueryFilters().Where(m => m.Role == value)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, (string?)null), cancellationToken);
                break;
            case SuggestionType.OperationType:
                await db.Operations.IgnoreQueryFilters().Where(o => o.Type == value)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Type, (string?)null), cancellationToken);
                break;
            case SuggestionType.CaseType:
                await db.Cases.IgnoreQueryFilters().Where(c => c.Type == value)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Type, (string?)null), cancellationToken);
                break;
            case SuggestionType.FinancingCategory:
                await db.FinancingItems.IgnoreQueryFilters().Where(i => i.Category == value)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Category, (string?)null), cancellationToken);
                break;
            case SuggestionType.EvidenceCategory:
                await db.EvidenceItems.IgnoreQueryFilters().Where(i => i.Category == value)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.Category, (string?)null), cancellationToken);
                break;
        }
    }

    // per-value usage across the mapped record tables; trash included so admins see the real impact
    private static async Task<Dictionary<string, int>> UsageCountsAsync(AppDbContext db, SuggestionType type, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        switch (type)
        {
            case SuggestionType.Weapon:
                await MergeCountsAsync(db.PersonWeapons.IgnoreQueryFilters().Select(w => w.Text), map, cancellationToken);
                await MergeCountsAsync(db.FactionWeaponStocks.IgnoreQueryFilters().Select(w => w.Designation), map, cancellationToken);
                break;
            case SuggestionType.Vehicle:
                await MergeCountsAsync(db.PersonVehicles.IgnoreQueryFilters().Select(v => v.Designation), map, cancellationToken);
                break;
            case SuggestionType.Location:
                await MergeCountsAsync(db.PersonLocations.IgnoreQueryFilters().Select(o => o.Text), map, cancellationToken);
                break;
            case SuggestionType.Inventory:
                await MergeCountsAsync(db.FactionInventories.IgnoreQueryFilters().Select(l => l.Designation), map, cancellationToken);
                break;
            case SuggestionType.Kind:
                await MergeCountsAsync(db.Factions.IgnoreQueryFilters().Where(f => f.Kind != null).Select(f => f.Kind!), map, cancellationToken);
                break;
            case SuggestionType.PartyRole:
                await MergeCountsAsync(db.PartyMembers.IgnoreQueryFilters().Where(m => m.Role != null).Select(m => m.Role!), map, cancellationToken);
                break;
            case SuggestionType.OperationType:
                await MergeCountsAsync(db.Operations.IgnoreQueryFilters().Where(o => o.Type != null).Select(o => o.Type!), map, cancellationToken);
                break;
            case SuggestionType.CaseType:
                await MergeCountsAsync(db.Cases.IgnoreQueryFilters().Where(c => c.Type != null).Select(c => c.Type!), map, cancellationToken);
                break;
            case SuggestionType.FinancingCategory:
                await MergeCountsAsync(db.FinancingItems.IgnoreQueryFilters().Where(i => i.Category != null).Select(i => i.Category!), map, cancellationToken);
                break;
            case SuggestionType.EvidenceCategory:
                await MergeCountsAsync(db.EvidenceItems.IgnoreQueryFilters().Where(i => i.Category != null).Select(i => i.Category!), map, cancellationToken);
                break;
        }
        return map;
    }

    private static async Task MergeCountsAsync(IQueryable<string> values, Dictionary<string, int> map, CancellationToken cancellationToken)
    {
        var counts = await values
            .GroupBy(v => v)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        foreach (var c in counts)
        {
            map[c.Value] = map.GetValueOrDefault(c.Value) + c.Count;
        }
    }
}
