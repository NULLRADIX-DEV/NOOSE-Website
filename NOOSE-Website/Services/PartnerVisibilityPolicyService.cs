using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPartnerVisibilityPolicyService" />
public class PartnerVisibilityPolicyService(IDbContextFactory<AppDbContext> dbFactory, IPartnerUnitService units)
    : IPartnerVisibilityPolicyService
{
    public async Task<IReadOnlySet<string>?> GetAllowedTypesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (user.GetPartnerAgency() is not { } agency)
        {
            return null;
        }
        var meId = user.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // released types
        var shared = await PartnerVisibility.ActiveShares(db)
            .Where(s => s.Agency == agency && (s.PartnerAgentId == null || s.PartnerAgentId == meId))
            .Select(s => new { s.EntityType, Individual = s.PartnerAgentId != null })
            .Distinct()
            .ToListAsync(cancellationToken);
        var unitShared = await PartnerVisibility.ActiveUnitShares(db, meId)
            .Select(s => s.EntityType)
            .Distinct()
            .ToListAsync(cancellationToken);
        var ruled = await db.PartnerReleaseRules
            .Where(r => r.Agency == agency)
            .Select(r => r.EntityType)
            .ToListAsync(cancellationToken);
        var unitRuled = await db.PartnerUnitReleaseRules
            .Where(r => db.Users.Any(u => u.Id == meId && u.PartnerUnitId == r.PartnerUnitId))
            .Select(r => r.EntityType)
            .ToListAsync(cancellationToken);
        var released = new HashSet<string>(shared.Select(s => s.EntityType).Concat(unitShared).Concat(ruled).Concat(unitRuled))
        {
            nameof(Document),
        };
        // a unit lead of a cross-agency taskforce reaches it without any release
        var leads = await PartnerVisibility.LeadTaskforceIds(db, agency, meId).AnyAsync(cancellationToken);
        if (leads)
        {
            released.Add(nameof(Taskforce));
        }

        // the unit from the account row, not the claim: a moved account must not keep its old unit's allowlist
        var unit = await units.GetAsync(await UnitIdAsync(db, meId, cancellationToken), cancellationToken);
        var entry = unit?.Visibility;
        // investigations: in the menu for whoever takes part in one or may open one
        var investigations = await db.Investigations.OnlyVisible(db, ViewerScope.From(user)).AnyAsync(cancellationToken)
            || (unit?.MayCreateInvestigations == true && await db.TaskforcePartnerAgencies.AnyAsync(tp => tp.Agency == agency
                && db.Taskforces.Any(t => t.Id == tp.TaskforceId && t.Scope == TaskforceScope.CrossAgency && !t.IsArchived), cancellationToken));
        // unconfigured: all released
        if (entry is null)
        {
            var all = PartnerTabCatalog.All.Select(t => t.TypeKey).Where(released.Contains).ToHashSet();
            if (investigations)
            {
                all.Add(Navigation.NavCatalog.InvestigationTypeKey);
            }
            return all;
        }

        var allowed = new HashSet<string>(entry.Types);
        // individual account releases widen beyond the unit default
        foreach (var type in shared.Where(s => s.Individual).Select(s => s.EntityType))
        {
            allowed.Add(type);
        }
        if (leads)
        {
            allowed.Add(nameof(Taskforce));
        }
        allowed.IntersectWith(released);
        if (investigations)
        {
            allowed.Add(Navigation.NavCatalog.InvestigationTypeKey);
        }
        return allowed;
    }

    public async Task<IReadOnlySet<string>?> GetVisibleTabsAsync(ClaimsPrincipal user, string typeKey, string recordId, CancellationToken cancellationToken = default)
    {
        if (user.GetPartnerAgency() is not { } agency)
        {
            return null;
        }
        var tabs = await UnitTabsAsync(user, agency, typeKey, recordId, cancellationToken);

        // blocks beat releases
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var blocked = await PartnerVisibility.BlockedContentAsync(db, agency, cancellationToken);
        if (blocked == PartnerContent.None)
        {
            return tabs;
        }
        var visible = new HashSet<string>(tabs ?? PartnerTabCatalog.TabSlugs(typeKey));
        visible.ExceptWith(PartnerContentCatalog.BlockedTabs(blocked));
        return visible;
    }

    /// <summary>The account's unit as stored now; claims may lag behind a move.</summary>
    private static Task<string?> UnitIdAsync(AppDbContext db, string? meId, CancellationToken cancellationToken)
        => meId is null ? Task.FromResult<string?>(null)
            : db.Users.Where(u => u.Id == meId && u.PartnerAgency != null).Select(u => u.PartnerUnitId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Unit allowlist for a record; null = all tabs (unconfigured unit, individually released, or led taskforce).</summary>
    private async Task<IReadOnlySet<string>?> UnitTabsAsync(ClaimsPrincipal user, PartnerAgency agency, string typeKey, string recordId, CancellationToken cancellationToken)
    {
        string? unitId;
        await using (var unitDb = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            unitId = await UnitIdAsync(unitDb, user.GetAgentId(), cancellationToken);
        }
        if ((await units.GetAsync(unitId, cancellationToken))?.Visibility is not { } entry)
        {
            return null;
        }

        // individually released record or a taskforce led as unit lead shows in full, regardless of the unit default
        var meId = user.GetAgentId();
        if (meId is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var individual = await PartnerVisibility.ActiveShares(db).AnyAsync(
                s => s.EntityType == typeKey && s.EntityId == recordId && s.PartnerAgentId == meId && s.Agency == agency,
                cancellationToken);
            if (individual
                || (typeKey == nameof(Taskforce) && await PartnerVisibility.LeadTaskforceIds(db, agency, meId).ContainsAsync(recordId, cancellationToken)))
            {
                return null;
            }
        }

        if (entry.Tabs.TryGetValue(typeKey, out var slugs))
        {
            return new HashSet<string>(slugs);
        }
        // listed type without a tab restriction = all tabs; unlisted type = no tabs
        return entry.Types.Contains(typeKey) ? null : new HashSet<string>();
    }
}
