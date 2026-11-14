using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
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
    string? MyUnitId)
{
    public static readonly InvestigationAccessView None = new(false, false, false, false, false,
        new Dictionary<string, InvestigationAccessLevel>(), null, null);

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
    public static async Task<InvestigationAccessView> ResolveAsync(AppDbContext db, string investigationId, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var meId = user.GetAgentId();
        var inv = await db.Investigations.AsNoTracking()
            .Where(i => i.Id == investigationId)
            .Select(i => new { i.Id, i.TaskforceId })
            .FirstOrDefaultAsync(cancellationToken);
        if (inv is null || meId is null)
        {
            return InvestigationAccessView.None;
        }
        var me = await db.Users.AsNoTracking()
            .Where(u => u.Id == meId)
            .Select(u => new { u.PartnerAgency, u.PartnerUnitId, u.IsPartnerUnitLead })
            .FirstOrDefaultAsync(cancellationToken);
        if (me is null)
        {
            return InvestigationAccessView.None;
        }
        var isPartner = me.PartnerAgency is not null;
        var unitId = isPartner ? me.PartnerUnitId : null;
        var unit = unitId is null ? null : await db.PartnerUnits.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, cancellationToken);
        // demo and supervision read, nobody else writes without contributing rights
        var mayWrite = isPartner ? user.MayContribute() && !user.IsOnlyReader() && !user.IsDemo() : user.MayWrite();

        // full access arms
        var full = false;
        var readAll = false;
        if (!isPartner && user.IsLeadership())
        {
            full = true;
        }
        else if (!isPartner && user.IsOnlyReader())
        {
            readAll = true;
        }
        if (!isPartner && !full && await db.TaskforceAgents.AnyAsync(
                ta => ta.TaskforceId == inv.TaskforceId && ta.AgentId == meId && ta.Role != TaskforceRole.Member, cancellationToken))
        {
            full = true;
        }
        if (isPartner && me.IsPartnerUnitLead && unit?.LeadSeesAllInvestigations == true
            && await db.TaskforcePartnerAgencies.AnyAsync(tp => tp.TaskforceId == inv.TaskforceId && tp.Agency == me.PartnerAgency
                && db.Taskforces.Any(t => t.Id == tp.TaskforceId && t.Scope == TaskforceScope.CrossAgency), cancellationToken))
        {
            full = true;
        }

        var rows = await db.InvestigationParticipants.AsNoTracking()
            .Where(p => p.InvestigationId == investigationId
                && (p.AgentId == meId || (unitId != null && p.PartnerUnitId == unitId)))
            .ToListAsync(cancellationToken);
        var own = rows.FirstOrDefault(r => r.AgentId == meId);
        if (own?.Role == InvestigationRole.Lead)
        {
            full = true;
        }

        var unitDefaults = unit is null ? new Dictionary<string, InvestigationAccessLevel>()
            : InvestigationTabAccess.Parse(unit.InvestigationDefaultsJson);
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
                level = InvestigationAccessLevel.None;
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

        var canSee = full || readAll || rows.Count > 0;
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

        var maySign = mayWrite && isPartner && own?.Role == InvestigationRole.Prosecutor && unit?.IsProsecution == true;
        return new InvestigationAccessView(true, full && mayWrite, full && mayWrite || (mayWrite && tabs[InvestigationTabs.Participants] == InvestigationAccessLevel.Edit),
            maySign, mayWrite, tabs, meId, unitId);
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
        if (access.FullAccess || (access.CanSee && access.Level(InvestigationTabs.Participants) == InvestigationAccessLevel.Edit))
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
