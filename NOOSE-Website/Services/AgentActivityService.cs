using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Models.Activities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IAgentActivityService" />
public class AgentActivityService(IDbContextFactory<AppDbContext> dbFactory, IThreatScoreService threat) : IAgentActivityService
{
    private const int PlainSnippetMax = 1000;

    public async Task<List<AgentActivityListItem>> GetListAsync(ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activities = await db.AgentActivities
            .Include(a => a.Links)
            .OrderByDescending(a => a.ActivityDate)
            .ToListAsync(cancellationToken);
        return await ProjectAsync(db, activities, scope, cancellationToken);
    }

    public async Task<AgentActivityDetailView?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.AgentActivities
            .Include(a => a.Links)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (activity is null)
        {
            return null;
        }

        var owners = await OwnerNamesAsync(db, new[] { activity.CreatedById }, cancellationToken);
        var orgNames = await ResolveVisibleOrgNamesAsync(db, activity.Links, scope, cancellationToken);
        return new AgentActivityDetailView
        {
            Activity = activity,
            OwnerName = OwnerName(owners, activity.CreatedById),
            Orgs = MapOrgs(activity.Links, orgNames),
        };
    }

    public async Task<List<AgentActivityListItem>> GetLinkedAsync(string targetType, string targetId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activityIds = await db.AgentActivityLinks
            .Where(l => l.TargetType == targetType && l.TargetId == targetId)
            .Select(l => l.AgentActivityId)
            .ToListAsync(cancellationToken);
        if (activityIds.Count == 0)
        {
            return new();
        }
        var activities = await db.AgentActivities
            .Include(a => a.Links)
            .Where(a => activityIds.Contains(a.Id))
            .OrderByDescending(a => a.ActivityDate)
            .ToListAsync(cancellationToken);
        return await ProjectAsync(db, activities, scope, cancellationToken);
    }

    public async Task<List<AgentActivity>> GetLinkedFullAsync(string targetType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activityIds = await db.AgentActivityLinks
            .Where(l => l.TargetType == targetType && l.TargetId == targetId)
            .Select(l => l.AgentActivityId)
            .ToListAsync(cancellationToken);
        if (activityIds.Count == 0)
        {
            return new();
        }
        return await db.AgentActivities
            .Where(a => activityIds.Contains(a.Id))
            .OrderByDescending(a => a.ActivityDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AgentActivityListItem>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activities = await db.AgentActivities.IgnoreQueryFilters()
            .Where(a => a.IsDeleted)
            .OrderByDescending(a => a.DeletedAt)
            .ToListAsync(cancellationToken);

        var owners = await OwnerNamesAsync(db, activities.Select(a => a.CreatedById), cancellationToken);
        return activities.Select(a => new AgentActivityListItem
        {
            Id = a.Id,
            Title = a.Title,
            Kind = a.Kind,
            ActivityDate = a.ActivityDate,
            CreatedAt = a.CreatedAt,
            DeletedAt = a.DeletedAt,
            OwnerId = a.CreatedById,
            OwnerName = OwnerName(owners, a.CreatedById),
        }).ToList();
    }

    public async Task<List<string>> GetKindsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentActivities
            .Where(a => a.Kind != null && a.Kind != "")
            .Select(a => a.Kind!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Closed since deployments became operations; the old reports stay readable and editable.</remarks>
    public Task<AgentActivity> CreateAsync(AgentActivityInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        throw new InvalidOperationException("Neue Aktivitäten gibt es nicht mehr: einen Einsatz legst du als Operation an.");
    }

    public async Task UpdateAsync(string id, AgentActivityInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var title = (input.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Der Titel darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.AgentActivities
            .Include(a => a.Links)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aktivität '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(activity, actor);
        var beforeFactions = FactionLinkIds(activity.Links).ToList();

        activity.Title = title;
        activity.Kind = input.Kind.TrimToNull();
        activity.ActivityDate = input.ActivityDate.ToUniversalTime();
        activity.ContentHtml = HtmlCleanup.Clean(input.ContentHtml);

        var wanted = DistinctOrgLinks(input.OrgLinks)
            .Select(l => (l.TargetType, l.TargetId))
            .ToHashSet();
        // add new links
        foreach (var (type, targetId) in wanted)
        {
            if (!activity.Links.Any(l => l.TargetType == type && l.TargetId == targetId))
            {
                activity.Links.Add(new AgentActivityLink { TargetType = type, TargetId = targetId });
            }
        }
        // remove links the actor could see but dropped; keep links hidden from the actor untouched
        var visible = await ResolveVisibleOrgNamesAsync(db, activity.Links, ViewerScope.From(actor), cancellationToken);
        var toRemove = activity.Links
            .Where(l => !wanted.Contains((l.TargetType, l.TargetId))
                && visible.ContainsKey((l.TargetType, l.TargetId)))
            .ToList();
        foreach (var link in toRemove)
        {
            activity.Links.Remove(link);
            db.AgentActivityLinks.Remove(link);
        }

        await db.SaveChangesAsync(cancellationToken);
        await FactionsTouchedAsync(db, beforeFactions.Concat(FactionLinkIds(activity.Links)), cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.AgentActivities.Include(a => a.Links).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aktivität '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(activity, actor);
        var factionIds = FactionLinkIds(activity.Links).ToList();
        db.AgentActivities.Remove(activity);
        await db.SaveChangesAsync(cancellationToken);
        await FactionsTouchedAsync(db, factionIds, cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.AgentActivities.IgnoreQueryFilters().Include(a => a.Links)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Aktivität '{id}' nicht gefunden.");
        activity.IsDeleted = false;
        activity.DeletedAt = null;
        activity.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
        await FactionsTouchedAsync(db, FactionLinkIds(activity.Links), cancellationToken);
    }

    /// <summary>Throws unless the actor is leadership or the activity's creator.</summary>
    private static void RequireCreatorOrLeadership(AgentActivity activity, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId) && activity.CreatedById == meId)
        {
            return;
        }
        throw new UnauthorizedAccessException("Diese Aktivität darf nur ihr Ersteller oder die Führung bearbeiten.");
    }

    // build list items with owner names + visible org links
    private static async Task<List<AgentActivityListItem>> ProjectAsync(AppDbContext db, List<AgentActivity> activities, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (activities.Count == 0)
        {
            return new();
        }
        var owners = await OwnerNamesAsync(db, activities.Select(a => a.CreatedById), cancellationToken);
        var allLinks = activities.SelectMany(a => a.Links).ToList();
        var orgNames = await ResolveVisibleOrgNamesAsync(db, allLinks, scope, cancellationToken);

        return activities.Select(a => new AgentActivityListItem
        {
            Id = a.Id,
            Title = a.Title,
            Kind = a.Kind,
            ActivityDate = a.ActivityDate,
            CreatedAt = a.CreatedAt,
            OwnerId = a.CreatedById,
            OwnerName = OwnerName(owners, a.CreatedById),
            Orgs = MapOrgs(a.Links, orgNames),
            ContentPlain = PlainText(a.ContentHtml),
        }).ToList();
    }

    private static List<AgentActivityOrgRef> MapOrgs(IEnumerable<AgentActivityLink> links, Dictionary<(string, string), string> names)
        => links
            .Where(l => names.ContainsKey((l.TargetType, l.TargetId)))
            .Select(l => new AgentActivityOrgRef
            {
                TargetType = l.TargetType,
                TargetId = l.TargetId,
                DisplayName = names[(l.TargetType, l.TargetId)],
            })
            .ToList();

    private static async Task<Dictionary<string, string>> OwnerNamesAsync(AppDbContext db, IEnumerable<string?> ownerIds, CancellationToken cancellationToken)
    {
        var ids = ownerIds.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new();
        }
        return await db.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Codename })
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
    }

    private static string OwnerName(Dictionary<string, string> owners, string? ownerId)
        => ownerId is not null && owners.TryGetValue(ownerId, out var name) ? name : "—";

    // resolve display names for org links visible to the viewer (hides classified orgs)
    private static async Task<Dictionary<(string, string), string>> ResolveVisibleOrgNamesAsync(
        AppDbContext db, IReadOnlyCollection<AgentActivityLink> links, ViewerScope scope, CancellationToken cancellationToken)
    {
        var map = new Dictionary<(string, string), string>();
        var factionIds = links.Where(l => l.TargetType == nameof(Faction)).Select(l => l.TargetId).Distinct().ToList();
        var groupIds = links.Where(l => l.TargetType == nameof(PersonGroup)).Select(l => l.TargetId).Distinct().ToList();

        if (factionIds.Count > 0)
        {
            var rows = await db.Factions.OnlyVisible(scope)
                .Where(f => factionIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name })
                .ToListAsync(cancellationToken);
            foreach (var r in rows)
            {
                map[(nameof(Faction), r.Id)] = r.Name;
            }
        }
        if (groupIds.Count > 0)
        {
            var rows = await db.PersonGroups.OnlyVisible(scope)
                .Where(g => groupIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name })
                .ToListAsync(cancellationToken);
            foreach (var r in rows)
            {
                map[(nameof(PersonGroup), r.Id)] = r.Name;
            }
        }
        return map;
    }

    private static IEnumerable<string> FactionLinkIds(IEnumerable<AgentActivityLink> links)
        => links.Where(l => l.TargetType == nameof(Faction) && !string.IsNullOrEmpty(l.TargetId)).Select(l => l.TargetId);

    // an activity is core of a faction's S1 heat and one of its four freshness facets, so linking/unlinking touches both
    private async Task FactionsTouchedAsync(AppDbContext db, IEnumerable<string> factionIds, CancellationToken cancellationToken)
    {
        var ids = factionIds.Distinct().ToList();
        await FactionRecency.StampAsync(db, ids, FactionRecencyFacet.Activities, cancellationToken);
        foreach (var factionId in ids)
        {
            await threat.NewCalculateAsync(factionId, cancellationToken);
        }
    }

    private static IEnumerable<AgentActivityOrgRef> DistinctOrgLinks(IEnumerable<AgentActivityOrgRef>? links)
        => (links ?? Enumerable.Empty<AgentActivityOrgRef>())
            .Where(l => (l.TargetType == nameof(Faction) || l.TargetType == nameof(PersonGroup)) && !string.IsNullOrEmpty(l.TargetId))
            .GroupBy(l => (l.TargetType, l.TargetId))
            .Select(g => g.First());

    // strip tags to a short plain-text snippet for client-side filtering
    private static string PlainText(string? html)
    {
        var text = HtmlCleanup.PlainText(html);
        return text.Length > PlainSnippetMax ? text[..PlainSnippetMax] : text;
    }
}
