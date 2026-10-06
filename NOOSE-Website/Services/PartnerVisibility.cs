using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Partner visibility: a record is visible only if released to the viewer's agency or account (by share or rule), and not classified.</summary>
public static class PartnerVisibility
{
    /// <summary>Record types that can be released to partners; everything else is never partner-visible.</summary>
    public static bool IsReleasableType(string entityType) => entityType is
        nameof(Person) or nameof(Faction) or nameof(PersonGroup) or nameof(Party)
        or nameof(Operation) or nameof(Case) or nameof(Document) or nameof(Law)
        or nameof(Taskforce);

    /// <summary>True if an active share row grants this record to the agency or the viewer's account (whole or shell).</summary>
    public static Task<bool> HasShareAsync(AppDbContext db, string entityType, string entityId, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
        => db.PartnerShares.AnyAsync(s => s.EntityType == entityType && s.EntityId == entityId && s.Agency == agency
            && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId), cancellationToken);

    /// <summary>Parent point-check: released to agency or account (share or rule), exists, and not classified.</summary>
    public static async Task<bool> IsRecordVisibleToPartnerAsync(
        AppDbContext db, string entityType, string entityId, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
        => entityType switch
        {
            nameof(Person) => await db.People.Where(p => p.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Faction) => await db.Factions.Where(f => f.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups.Where(g => g.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Party) => await db.Parties.Where(p => p.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Operation) => await db.Operations.Where(o => o.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Taskforce) => await db.Taskforces.Where(t => t.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Case) => await db.Cases.Where(v => v.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Document) => await db.Documents.Where(d => d.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            nameof(Law) => await db.Laws.Where(l => l.Id == entityId)
                .OnlyPartnerVisible(db, agency, partnerAgentId).AnyAsync(cancellationToken),
            _ => false,
        };

    /// <summary>Child point-check: not blocked for the agency, parent partner-visible AND (parent whole-record OR an explicit child release).</summary>
    public static async Task<bool> IsChildVisibleToPartnerAsync(
        AppDbContext db, string parentType, string parentId, string childType, string childId, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
    {
        if (await IsContentBlockedAsync(db, agency, childType, cancellationToken))
        {
            return false;
        }
        if (!await IsRecordVisibleToPartnerAsync(db, parentType, parentId, agency, partnerAgentId, cancellationToken))
        {
            return false;
        }
        return await ParentIncludesChildrenAsync(db, parentType, parentId, agency, partnerAgentId, cancellationToken)
            || await HasShareAsync(db, childType, childId, agency, partnerAgentId, cancellationToken);
    }

    /// <summary>True if the parent record is released whole (all children covered) to the agency or the viewer's account. Says nothing about the parent's own visibility; check that first.</summary>
    public static async Task<bool> ParentIncludesChildrenAsync(AppDbContext db, string parentType, string parentId, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
        => await db.PartnerShares.AnyAsync(s => s.EntityType == parentType && s.EntityId == parentId && s.Agency == agency && s.IncludesChildren
                && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId), cancellationToken)
            || (await RuleMatchedIdsAsync(db, agency, parentType, new[] { parentId }, wholeOnly: true, cancellationToken)).Count > 0;

    /// <summary>True when the agency profile never shows this child type, whatever was released.</summary>
    public static async Task<bool> IsContentBlockedAsync(AppDbContext db, PartnerAgency agency, string childType, CancellationToken cancellationToken = default)
    {
        if (PartnerContentCatalog.ForChildType(childType) == PartnerContent.None)
        {
            return false;
        }
        return PartnerContentCatalog.Blocks(await BlockedContentAsync(db, agency, cancellationToken), childType);
    }

    /// <summary>Functions switched on for the viewer's agency; None for internal accounts or without a profile row.</summary>
    public static async Task<PartnerFeature> FeaturesAsync(AppDbContext db, PartnerAgency? agency, CancellationToken cancellationToken = default)
        => agency is null
            ? PartnerFeature.None
            : await db.PartnerAgencyProfiles
                .Where(p => p.Agency == agency)
                .Select(p => p.Features)
                .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Content the agency never sees; None without a profile row.</summary>
    public static async Task<PartnerContent> BlockedContentAsync(AppDbContext db, PartnerAgency agency, CancellationToken cancellationToken = default)
        => await db.PartnerAgencyProfiles
            .Where(p => p.Agency == agency)
            .Select(p => p.BlockedContent)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Of a candidate child-id set, those individually released to the agency or the viewer's account; none when the type is blocked.</summary>
    public static async Task<HashSet<string>> ReleasedChildIdsAsync(AppDbContext db, string childType, IReadOnlyCollection<string> childIds, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
    {
        if (childIds.Count == 0 || await IsContentBlockedAsync(db, agency, childType, cancellationToken))
        {
            return new();
        }
        var ids = await db.PartnerShares
            .Where(s => s.EntityType == childType && s.Agency == agency && childIds.Contains(s.EntityId)
                && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
            .Select(s => s.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    /// <summary>Filters a child list for a partner: none when blocked, all when the parent is released whole, else only individually released items.</summary>
    public static async Task<List<T>> FilterChildrenAsync<T>(AppDbContext db, string parentType, string parentId, string childType,
        List<T> items, Func<T, string> idSelector, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return items;
        }
        if (await IsContentBlockedAsync(db, agency, childType, cancellationToken))
        {
            return new List<T>();
        }
        if (await ParentIncludesChildrenAsync(db, parentType, parentId, agency, partnerAgentId, cancellationToken))
        {
            return items;
        }
        var released = await ReleasedChildIdsAsync(db, childType, items.Select(idSelector).ToList(), agency, partnerAgentId, cancellationToken);
        return items.Where(i => released.Contains(idSelector(i))).ToList();
    }

    /// <summary>Batched twin of <see cref="FilterChildrenAsync"/> for a hit list spanning many parents.</summary>
    /// <remarks>The per-record helper is O(parents) round trips, which a search result set cannot afford. Semantics
    /// are identical: a blocked type shows nothing, a parent released whole covers all its children, otherwise only
    /// individually released ones.</remarks>
    public static async Task<HashSet<string>> VisibleChildIdsAsync(
        AppDbContext db, string childType,
        IReadOnlyCollection<(string ParentType, string ParentId, string ChildId)> candidates,
        PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count == 0 || await IsContentBlockedAsync(db, agency, childType, cancellationToken))
        {
            return visible;
        }

        var parentIds = candidates.Select(c => c.ParentId).Distinct().ToList();
        // whole by share
        var whole = (await db.PartnerShares
                .Where(s => s.Agency == agency && s.IncludesChildren && parentIds.Contains(s.EntityId)
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                .Select(s => new { s.EntityType, s.EntityId })
                .ToListAsync(cancellationToken))
            .Select(s => (s.EntityType, s.EntityId))
            .ToHashSet();
        // whole by rule
        foreach (var byType in candidates.GroupBy(c => c.ParentType))
        {
            var ids = byType.Select(c => c.ParentId).Distinct().ToList();
            foreach (var id in await RuleMatchedIdsAsync(db, agency, byType.Key, ids, wholeOnly: true, cancellationToken))
            {
                whole.Add((byType.Key, id));
            }
        }

        var rest = candidates.Where(c => !whole.Contains((c.ParentType, c.ParentId))).ToList();
        foreach (var candidate in candidates.Where(c => whole.Contains((c.ParentType, c.ParentId))))
        {
            visible.Add(candidate.ChildId);
        }
        if (rest.Count == 0)
        {
            return visible;
        }

        // individually released
        var released = await ReleasedChildIdsAsync(
            db, childType, rest.Select(c => c.ChildId).Distinct().ToList(), agency, partnerAgentId, cancellationToken);
        visible.UnionWith(released);
        return visible;
    }

    /// <summary>Of a candidate parent-id set, those released to the agency or the viewer's account (share or rule). Caller still applies the classified check.</summary>
    public static async Task<HashSet<string>> ReleasedParentIdsAsync(
        AppDbContext db, string entityType, IReadOnlyCollection<string> candidateIds, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken = default)
    {
        if (candidateIds.Count == 0)
        {
            return new();
        }
        var ids = await db.PartnerShares
            .Where(s => s.EntityType == entityType && s.Agency == agency && candidateIds.Contains(s.EntityId)
                && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
            .Select(s => s.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var released = ids.ToHashSet();
        released.UnionWith(await RuleMatchedIdsAsync(db, agency, entityType, candidateIds, wholeOnly: false, cancellationToken));
        return released;
    }

    /// <summary>Of a candidate id set, those a release rule of the agency covers. Existence and classification are the caller's: every caller checks parent visibility first.</summary>
    private static async Task<HashSet<string>> RuleMatchedIdsAsync(
        AppDbContext db, PartnerAgency agency, string entityType, IReadOnlyCollection<string> candidateIds, bool wholeOnly, CancellationToken cancellationToken)
    {
        var scope = await db.PartnerReleaseRules
            .Where(r => r.Agency == agency && r.EntityType == entityType && (!wholeOnly || r.IncludesChildren))
            .Select(r => (PartnerRuleScope?)r.Scope)
            .FirstOrDefaultAsync(cancellationToken);
        if (scope is null || candidateIds.Count == 0)
        {
            return new();
        }
        var ids = candidateIds.ToList();
        return scope switch
        {
            PartnerRuleScope.All => ids.ToHashSet(),
            PartnerRuleScope.BadFactions when entityType == nameof(Faction) => (await db.Factions
                .Where(f => ids.Contains(f.Id) && f.IsBadFaction && !f.IsArchived)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken)).ToHashSet(),
            PartnerRuleScope.BadFactionMembers when entityType == nameof(Person) => (await BadFactionMemberIds(db)
                .Where(id => ids.Contains(id))
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet(),
            _ => new(),
        };
    }

    /// <summary>People currently in an active, unclassified Badfrak.</summary>
    private static IQueryable<string> BadFactionMemberIds(AppDbContext db)
        => db.FactionMembers
            .Where(m => db.Factions.Any(f => f.Id == m.FactionId && f.IsBadFaction && !f.IsClassified && !f.IsArchived))
            .Select(m => m.PersonId);

    /// <summary>Release rules of one agency for one type.</summary>
    private static IQueryable<PartnerReleaseRule> Rules(AppDbContext db, PartnerAgency agency, string entityType)
        => db.PartnerReleaseRules.Where(r => r.Agency == agency && r.EntityType == entityType);

    // ---- list predicates: released (share or rule, agency or account) and not classified ----

    public static IQueryable<Person> OnlyPartnerVisible(this IQueryable<Person> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Person));
        var badMembers = BadFactionMemberIds(db);
        return query.Where(p => !p.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(Person) && s.EntityId == p.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)
                || (rules.Any(r => r.Scope == PartnerRuleScope.BadFactionMembers) && badMembers.Contains(p.Id))));
    }

    public static IQueryable<Faction> OnlyPartnerVisible(this IQueryable<Faction> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Faction));
        return query.Where(f => !f.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(Faction) && s.EntityId == f.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)
                || (f.IsBadFaction && !f.IsArchived && rules.Any(r => r.Scope == PartnerRuleScope.BadFactions))));
    }

    public static IQueryable<PersonGroup> OnlyPartnerVisible(this IQueryable<PersonGroup> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(PersonGroup));
        return query.Where(g => !g.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(PersonGroup) && s.EntityId == g.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)));
    }

    public static IQueryable<Party> OnlyPartnerVisible(this IQueryable<Party> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Party));
        return query.Where(p => !p.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(Party) && s.EntityId == p.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)));
    }

    public static IQueryable<Operation> OnlyPartnerVisible(this IQueryable<Operation> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Operation));
        return query.Where(o => !o.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(Operation) && s.EntityId == o.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)));
    }

    public static IQueryable<Case> OnlyPartnerVisible(this IQueryable<Case> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Case));
        return query.Where(v => !v.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(Case) && s.EntityId == v.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)));
    }

    public static IQueryable<Document> OnlyPartnerVisible(this IQueryable<Document> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Document));
        return query.Where(d => !(d.IsClassified || d.IsTRUClassified || d.IsHRBClassified)
            && ((partnerAgentId != null && d.CreatedById == partnerAgentId)
                || db.PartnerShares.Any(s => s.EntityType == nameof(Document) && s.EntityId == d.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)));
    }

    public static IQueryable<Law> OnlyPartnerVisible(this IQueryable<Law> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Law));
        return query.Where(l => db.PartnerShares.Any(s => s.EntityType == nameof(Law) && s.EntityId == l.Id && s.Agency == agency
                && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
            || rules.Any(r => r.Scope == PartnerRuleScope.All));
    }

    public static IQueryable<Taskforce> OnlyPartnerVisible(this IQueryable<Taskforce> query, AppDbContext db, PartnerAgency agency, string? partnerAgentId)
    {
        var rules = Rules(db, agency, nameof(Taskforce));
        return query.Where(t => !t.IsClassified
            && (db.PartnerShares.Any(s => s.EntityType == nameof(Taskforce) && s.EntityId == t.Id && s.Agency == agency
                    && (s.PartnerAgentId == null || s.PartnerAgentId == partnerAgentId))
                || rules.Any(r => r.Scope == PartnerRuleScope.All)));
    }
}
