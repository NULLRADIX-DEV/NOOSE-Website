using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>What one viewer may do in one investigation, tab by tab.</summary>
public sealed record InvestigationAccessView(
    bool CanSee,
    bool FullAccess,
    bool MayManage,
    bool MaySignWarrants,
    bool MayWrite,
    IReadOnlyDictionary<string, InvestigationAccessLevel> Tabs,
    string? MeId,
    string? MyUnitId,
    bool ReadsAll = false)
{
    public static readonly InvestigationAccessView None = new(false, false, false, false, false,
        new Dictionary<string, InvestigationAccessLevel>(), null, null);

    /// <summary>Sees every entry, restricted ones included: full access, supervision, a unit lead that sees all.</summary>
    public bool SeesAllEntries => CanSee && (FullAccess || ReadsAll || Level(InvestigationTabs.Participants) == InvestigationAccessLevel.Edit);

    public InvestigationAccessLevel Level(string slug) => Tabs.GetValueOrDefault(slug);

    public bool CanRead(string slug) => CanSee && Level(slug) >= InvestigationAccessLevel.Read;

    public bool CanEdit(string slug) => CanSee && MayWrite && Level(slug) >= InvestigationAccessLevel.Edit;
}

/// <summary>
/// The one place that decides who sees an investigation and what they may do in it: list predicate, point check,
/// tab rights, entry restrictions and the prosecutor's signature. The viewer's unit, lead flag and taskforce role are
/// read from the database, never from claims, so a moved account loses its rights at once.
/// </summary>
public static class InvestigationAccess
{
    /// <summary>Investigations the viewer may open at all.</summary>
    public static IQueryable<Investigation> OnlyVisible(this IQueryable<Investigation> query, AppDbContext db, ViewerScope scope)
    {
        // leadership and supervision see every investigation; partners never take this arm
        if (scope.PartnerAgency is null && scope.MayAllTaskforces)
        {
            return query;
        }
        var me = scope.MeId;
        if (me is null)
        {
            return query.Where(_ => false);
        }
        return query.Where(i =>
            db.InvestigationParticipants.Any(p => p.InvestigationId == i.Id
                && (p.AgentId == me
                    || (p.PartnerUnitId != null && db.Users.Any(u => u.Id == me && u.PartnerAgency != null && u.PartnerUnitId == p.PartnerUnitId))))
            || db.TaskforceAgents.Any(ta => ta.TaskforceId == i.TaskforceId && ta.AgentId == me && ta.Role != TaskforceRole.Member
                && db.Users.Any(u => u.Id == me && u.PartnerAgency == null))
            || LeadArm(db, me).Contains(i.TaskforceId));
    }

    /// <summary>Point check, same predicate as the list.</summary>
    public static Task<bool> IsVisibleAsync(AppDbContext db, string investigationId, ViewerScope scope, CancellationToken cancellationToken = default)
        => db.Investigations.Where(i => i.Id == investigationId).OnlyVisible(db, scope).AnyAsync(cancellationToken);

    /// <summary>Cross-agency taskforces whose investigations this account sees whole as lead of a unit that may.</summary>
    private static IQueryable<string> LeadArm(AppDbContext db, string me)
        => db.TaskforcePartnerAgencies
            .Where(tp => db.Taskforces.Any(t => t.Id == tp.TaskforceId && t.Scope == TaskforceScope.CrossAgency)
                && db.Users.Any(u => u.Id == me && u.PartnerAgency == tp.Agency && u.IsPartnerUnitLead
                    && db.PartnerUnits.Any(pu => pu.Id == u.PartnerUnitId && pu.LeadSeesAllInvestigations)))
            .Select(tp => tp.TaskforceId);

    /// <summary>Full resolution for one viewer and one investigation.</summary>
    public static Task<InvestigationAccessView> ResolveAsync(AppDbContext db, string investigationId, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
        => ResolveCoreAsync(db, investigationId, user.GetAgentId(), user.IsLeadership(), user.IsOnlyReader(),
            // demo and supervision read, nobody else writes without contributing rights
            isPartner => isPartner ? user.MayContribute() && !user.IsOnlyReader() && !user.IsDemo() : user.MayWrite(),
            cancellationToken);

    /// <summary>Read-only view for a viewer known only by scope; CanEdit and MayManage are always false.</summary>
    /// <remarks>The read-all arm takes <c>MayAllTaskforces</c> only when the account row is no partner, so a caller
    /// that forgot the agency cannot widen what a partner reads.</remarks>
    public static Task<InvestigationAccessView> ResolveReadAsync(AppDbContext db, string investigationId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => scope.MeId is null
            ? Task.FromResult(InvestigationAccessView.None)
            : ResolveCoreAsync(db, investigationId, scope.MeId, leadershipClaim: false, readerClaim: scope.MayAllTaskforces,
                _ => false, cancellationToken);

    /// <summary>Full resolution for one viewer and many investigations; an unknown or invisible id maps to <see cref="InvestigationAccessView.None"/>.</summary>
    /// <remarks>A fixed number of queries whatever the count, for lists and pickers that would otherwise resolve file by file.</remarks>
    public static Task<Dictionary<string, InvestigationAccessView>> ResolveManyAsync(AppDbContext db, IReadOnlyCollection<string> investigationIds,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
        => ResolveManyCoreAsync(db, investigationIds, user.GetAgentId(), user.IsLeadership(), user.IsOnlyReader(),
            isPartner => isPartner ? user.MayContribute() && !user.IsOnlyReader() && !user.IsDemo() : user.MayWrite(),
            cancellationToken);

    /// <summary>Read-only batch twin of <see cref="ResolveReadAsync"/>.</summary>
    public static Task<Dictionary<string, InvestigationAccessView>> ResolveReadManyAsync(AppDbContext db, IReadOnlyCollection<string> investigationIds,
        ViewerScope scope, CancellationToken cancellationToken = default)
        => ResolveManyCoreAsync(db, investigationIds, scope.MeId, leadershipClaim: false, readerClaim: scope.MayAllTaskforces,
            _ => false, cancellationToken);

    /// <summary>Shared body of both resolutions; only the two claim flags and the write rule come from the caller.</summary>
    private static async Task<InvestigationAccessView> ResolveCoreAsync(AppDbContext db, string investigationId, string? meId,
        bool leadershipClaim, bool readerClaim, Func<bool, bool> mayWriteFor, CancellationToken cancellationToken)
        => (await ResolveManyCoreAsync(db, [investigationId], meId, leadershipClaim, readerClaim, mayWriteFor, cancellationToken))
            .GetValueOrDefault(investigationId) ?? InvestigationAccessView.None;

    /// <summary>One pass for all files: the account and its unit once, then each per-file fact in one query for all files.</summary>
    private static async Task<Dictionary<string, InvestigationAccessView>> ResolveManyCoreAsync(AppDbContext db,
        IReadOnlyCollection<string> investigationIds, string? meId, bool leadershipClaim, bool readerClaim, Func<bool, bool> mayWriteFor,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, InvestigationAccessView>(StringComparer.Ordinal);
        var ids = investigationIds.Distinct().ToList();
        if (ids.Count == 0 || meId is null)
        {
            return result;
        }
        var invs = await db.Investigations.AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.TaskforceId })
            .ToListAsync(cancellationToken);
        if (invs.Count == 0)
        {
            return result;
        }
        var me = await db.Users.AsNoTracking()
            .Where(u => u.Id == meId)
            .Select(u => new { u.PartnerAgency, u.PartnerUnitId, u.IsPartnerUnitLead })
            .FirstOrDefaultAsync(cancellationToken);
        if (me is null)
        {
            return result;
        }
        var isPartner = me.PartnerAgency is not null;
        var unitId = isPartner ? me.PartnerUnitId : null;
        var unit = unitId is null ? null : await db.PartnerUnits.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, cancellationToken);
        var mayWrite = mayWriteFor(isPartner);
        var unitDefaults = unit is null ? new Dictionary<string, InvestigationAccessLevel>()
            : InvestigationTabAccess.Parse(unit.InvestigationDefaultsJson);
        var taskforceIds = invs.Select(i => i.TaskforceId).Distinct().ToList();
        var foundIds = invs.Select(i => i.Id).ToList();

        // taskforce lead arm: internal accounts without the leadership claim only
        var ledTaskforces = isPartner || leadershipClaim
            ? []
            : (await db.TaskforceAgents.AsNoTracking()
                .Where(ta => taskforceIds.Contains(ta.TaskforceId) && ta.AgentId == meId && ta.Role != TaskforceRole.Member)
                .Select(ta => ta.TaskforceId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        // a unit lead that sees all works on the whole file; deleting it and handing out the lead stay with full access
        var agency = me.PartnerAgency;
        var leadTaskforces = !(isPartner && me.IsPartnerUnitLead && unit?.LeadSeesAllInvestigations == true)
            ? []
            : (await db.TaskforcePartnerAgencies.AsNoTracking()
                .Where(tp => taskforceIds.Contains(tp.TaskforceId) && tp.Agency == agency
                    && db.Taskforces.Any(t => t.Id == tp.TaskforceId && t.Scope == TaskforceScope.CrossAgency))
                .Select(tp => tp.TaskforceId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var participantRows = (await db.InvestigationParticipants.AsNoTracking()
                .Where(p => foundIds.Contains(p.InvestigationId)
                    && (p.AgentId == meId || (unitId != null && p.PartnerUnitId == unitId)))
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.InvestigationId, StringComparer.Ordinal);

        foreach (var inv in invs)
        {
            result[inv.Id] = BuildView(meId, unitId, unit, unitDefaults, isPartner, mayWrite, leadershipClaim, readerClaim,
                ledTaskforces.Contains(inv.TaskforceId), leadTaskforces.Contains(inv.TaskforceId), participantRows[inv.Id].ToList());
        }
        return result;
    }

    /// <summary>The rights of one file from the facts already loaded; no database access.</summary>
    private static InvestigationAccessView BuildView(string meId, string? unitId, PartnerUnit? unit,
        IReadOnlyDictionary<string, InvestigationAccessLevel> unitDefaults, bool isPartner, bool mayWrite, bool leadershipClaim,
        bool readerClaim, bool leadsTaskforce, bool leadReads, List<InvestigationParticipant> rows)
    {
        // full access arms
        var full = false;
        var readAll = false;
        if (!isPartner && leadershipClaim)
        {
            full = true;
        }
        else if (!isPartner && readerClaim)
        {
            readAll = true;
        }
        if (!isPartner && !full && leadsTaskforce)
        {
            full = true;
        }
        var own = rows.FirstOrDefault(r => r.AgentId == meId);
        if (own?.Role == InvestigationRole.Lead)
        {
            full = true;
        }

        var tabs = new Dictionary<string, InvestigationAccessLevel>(StringComparer.Ordinal);
        foreach (var tab in InvestigationTabs.All)
        {
            InvestigationAccessLevel level;
            if (full)
            {
                level = InvestigationAccessLevel.Edit;
            }
            else if (readAll)
            {
                level = InvestigationAccessLevel.Read;
            }
            else
            {
                level = leadReads ? InvestigationAccessLevel.Edit : InvestigationAccessLevel.None;
                foreach (var row in rows)
                {
                    var granted = RowLevel(row, tab.Slug, isPartner, unitDefaults);
                    if (granted > level)
                    {
                        level = granted;
                    }
                }
            }
            tabs[tab.Slug] = InvestigationTabs.Cap(tab.Slug, level);
        }

        var canSee = full || readAll || leadReads || rows.Count > 0;
        if (!canSee)
        {
            return InvestigationAccessView.None;
        }
        // the cover sheet is always open to whoever is in
        if (tabs[InvestigationTabs.Overview] < InvestigationAccessLevel.Read)
        {
            tabs[InvestigationTabs.Overview] = InvestigationAccessLevel.Read;
        }
        // the history shows rows of every tab; only who reads them all reads it
        if (InvestigationTabs.Slugs.Where(s => s != InvestigationTabs.History).Any(s => tabs[s] < InvestigationAccessLevel.Read))
        {
            tabs[InvestigationTabs.History] = InvestigationAccessLevel.None;
        }

        // signing a warrant one may not read would be blind
        var maySign = mayWrite && isPartner && own?.Role == InvestigationRole.Prosecutor && unit?.IsProsecution == true
            && tabs[InvestigationTabs.Warrants] >= InvestigationAccessLevel.Read;
        return new InvestigationAccessView(true, full && mayWrite, full && mayWrite || (mayWrite && tabs[InvestigationTabs.Participants] == InvestigationAccessLevel.Edit),
            maySign, mayWrite, tabs, meId, unitId, ReadsAll: full || readAll || leadReads);
    }

    /// <summary>What one participant row grants on one tab: the role or unit default, then the row's own deviation.</summary>
    internal static InvestigationAccessLevel RowLevel(InvestigationParticipant row, string slug, bool viewerIsPartner,
        IReadOnlyDictionary<string, InvestigationAccessLevel> unitDefaults)
    {
        var overrides = InvestigationTabAccess.Parse(row.TabAccessJson);
        if (overrides.TryGetValue(slug, out var set))
        {
            return set;
        }
        if (row.PartnerUnitId is not null || viewerIsPartner)
        {
            var level = unitDefaults.GetValueOrDefault(slug);
            // the prosecutor is there for the warrants; signing one needs to read it
            if (row.Role == InvestigationRole.Prosecutor && slug == InvestigationTabs.Warrants && level < InvestigationAccessLevel.Read)
            {
                return InvestigationAccessLevel.Read;
            }
            return row.Role == InvestigationRole.Observer && level > InvestigationAccessLevel.Read ? InvestigationAccessLevel.Read : level;
        }
        return RoleDefault(row.Role, slug);
    }

    /// <summary>Defaults for an internal agent by role.</summary>
    public static InvestigationAccessLevel RoleDefault(InvestigationRole role, string slug) => role switch
    {
        InvestigationRole.Lead => InvestigationAccessLevel.Edit,
        InvestigationRole.Observer => InvestigationAccessLevel.Read,
        _ => slug == InvestigationTabs.Participants ? InvestigationAccessLevel.Read : InvestigationAccessLevel.Edit,
    };

    /// <summary>Of the given entries, the ids this viewer may not see because they are limited to others.</summary>
    public static async Task<HashSet<string>> HiddenItemIdsAsync(AppDbContext db, string investigationId, string itemType,
        InvestigationAccessView access, CancellationToken cancellationToken = default)
    {
        if (access.SeesAllEntries)
        {
            return new();
        }
        var rows = await db.InvestigationItemRestrictions.AsNoTracking()
            .Where(r => r.InvestigationId == investigationId && r.ItemType == itemType)
            .Select(r => new { r.ItemId, r.AgentId, r.PartnerUnitId })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ItemId)
            .Where(g => !g.Any(r => (r.AgentId != null && r.AgentId == access.MeId)
                                    || (r.PartnerUnitId != null && r.PartnerUnitId == access.MyUnitId)))
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>Batch twin of <see cref="HiddenItemIdsAsync(AppDbContext, string, string, InvestigationAccessView, CancellationToken)"/>:
    /// the (file, type, entry) triples one viewer may not see, one query for all files and types.</summary>
    /// <remarks>Every view must belong to the same viewer; a file missing from the map counts as not seen at all and is not asked for.</remarks>
    public static async Task<HashSet<(string InvestigationId, string ItemType, string ItemId)>> HiddenItemIdsAsync(AppDbContext db,
        IReadOnlyDictionary<string, InvestigationAccessView> accessByFile, IReadOnlyCollection<string> itemTypes,
        CancellationToken cancellationToken = default)
    {
        var limited = accessByFile.Where(a => a.Value.CanSee && !a.Value.SeesAllEntries).ToList();
        if (limited.Count == 0 || itemTypes.Count == 0)
        {
            return new();
        }
        var fileIds = limited.Select(a => a.Key).ToList();
        var types = itemTypes.Distinct().ToList();
        var rows = await db.InvestigationItemRestrictions.AsNoTracking()
            .Where(r => fileIds.Contains(r.InvestigationId) && types.Contains(r.ItemType))
            .Select(r => new { r.InvestigationId, r.ItemType, r.ItemId, r.AgentId, r.PartnerUnitId })
            .ToListAsync(cancellationToken);
        var me = limited[0].Value.MeId;
        var myUnit = limited[0].Value.MyUnitId;
        return rows.GroupBy(r => (r.InvestigationId, r.ItemType, r.ItemId))
            .Where(g => !g.Any(r => (r.AgentId != null && r.AgentId == me) || (r.PartnerUnitId != null && r.PartnerUnitId == myUnit)))
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>Throws unless the viewer may read the tab.</summary>
    public static void RequireRead(InvestigationAccessView access, string slug)
    {
        if (!access.CanRead(slug))
        {
            throw new UnauthorizedAccessException($"Kein Zugriff auf „{InvestigationTabs.Label(slug)}“ dieser Ermittlung.");
        }
    }

    /// <summary>Throws unless the viewer may edit the tab.</summary>
    public static void RequireEdit(InvestigationAccessView access, string slug)
    {
        if (!access.CanEdit(slug))
        {
            throw new UnauthorizedAccessException($"Keine Berechtigung, „{InvestigationTabs.Label(slug)}“ dieser Ermittlung zu bearbeiten.");
        }
    }
}
