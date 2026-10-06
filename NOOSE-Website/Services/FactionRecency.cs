using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Faction freshness: only members, stocks, activities and docs count as an update, each with its own stamp. The oldest of the four drives the record's light, so editing master data no longer hides a neglected section.</summary>
public static class FactionRecency
{
    /// <summary>One facet's stamp and whether it is the one driving the record's light.</summary>
    public sealed record Stamp(FactionRecencyFacet Facet, DateTime RefreshedUtc, bool IsOldest);

    /// <summary>A facet's stamp, falling back to the record's creation when never refreshed.</summary>
    public static DateTime RefreshedAt(Faction faction, FactionRecencyFacet facet) => (facet switch
    {
        FactionRecencyFacet.Members => faction.MembersRefreshedAt,
        FactionRecencyFacet.Stock => faction.StockRefreshedAt,
        FactionRecencyFacet.Activities => faction.ActivitiesRefreshedAt,
        FactionRecencyFacet.Docs => faction.DocsRefreshedAt,
        _ => null,
    }) ?? faction.CreatedAt;

    /// <summary>Reference date for the freshness light: the oldest of the four facet stamps.</summary>
    public static DateTime Reference(Faction faction) => Reference(faction, includeDocs: true);

    /// <summary>Reference date, optionally without the docs stamp (partners never learn about docs).</summary>
    public static DateTime Reference(Faction faction, bool includeDocs)
        => Reference(faction.CreatedAt, faction.MembersRefreshedAt, faction.StockRefreshedAt,
            faction.ActivitiesRefreshedAt, includeDocs ? faction.DocsRefreshedAt : faction.MembersRefreshedAt);

    /// <summary>Reference date from raw columns, for query projections that never materialize the entity.</summary>
    public static DateTime Reference(DateTime createdAt, DateTime? members, DateTime? stock,
        DateTime? activities, DateTime? docs)
    {
        var oldest = members ?? createdAt;
        oldest = Older(oldest, stock ?? createdAt);
        oldest = Older(oldest, activities ?? createdAt);
        return Older(oldest, docs ?? createdAt);
    }

    /// <summary>The facet whose stamp is oldest; ties resolve in display order.</summary>
    public static FactionRecencyFacet Oldest(Faction faction) => Oldest(faction, includeDocs: true);

    /// <summary>The oldest facet, optionally ignoring docs.</summary>
    public static FactionRecencyFacet Oldest(Faction faction, bool includeDocs)
    {
        var oldest = FactionRecencyFacet.Members;
        var oldestAt = RefreshedAt(faction, oldest);
        foreach (var facet in Shown(includeDocs))
        {
            var at = RefreshedAt(faction, facet);
            if (at < oldestAt)
            {
                oldest = facet;
                oldestAt = at;
            }
        }
        return oldest;
    }

    /// <summary>All four facets in display order, with the light-driving one flagged.</summary>
    public static IReadOnlyList<Stamp> Facets(Faction faction) => Facets(faction, includeDocs: true);

    /// <summary>Facets in display order, optionally without docs.</summary>
    public static IReadOnlyList<Stamp> Facets(Faction faction, bool includeDocs)
    {
        var oldest = Oldest(faction, includeDocs);
        return Shown(includeDocs)
            .Select(f => new Stamp(f, RefreshedAt(faction, f), f == oldest))
            .ToList();
    }

    private static IEnumerable<FactionRecencyFacet> Shown(bool includeDocs)
        => FactionRecencyFacetDisplay.All.Where(f => includeDocs || f != FactionRecencyFacet.Docs);

    /// <summary>Filter for records whose reference date is before the cutoff. Any facet older than the cutoff makes the oldest one older too, so the OR-form matches the same rows as the minimum would — and stays translatable to SQL.</summary>
    public static Expression<Func<Faction, bool>> ReferenceBefore(DateTime cutoffUtc)
        => f => (f.MembersRefreshedAt ?? f.CreatedAt) < cutoffUtc
             || (f.StockRefreshedAt ?? f.CreatedAt) < cutoffUtc
             || (f.ActivitiesRefreshedAt ?? f.CreatedAt) < cutoffUtc
             || (f.DocsRefreshedAt ?? f.CreatedAt) < cutoffUtc;

    /// <summary>Marks a facet as refreshed now. Raw update on purpose: the stamp is a derived signal, so it must not stamp ModifiedAt or land in the audit log as a record change.</summary>
    public static Task StampAsync(AppDbContext db, string factionId, FactionRecencyFacet facet,
        CancellationToken cancellationToken = default)
        => StampAsync(db, new[] { factionId }, facet, cancellationToken);

    /// <summary>Marks a facet as refreshed now on several records; activity and doc links fan out over factions.</summary>
    public static async Task StampAsync(AppDbContext db, IEnumerable<string> factionIds, FactionRecencyFacet facet,
        CancellationToken cancellationToken = default)
    {
        var ids = factionIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var rows = db.Factions.Where(f => ids.Contains(f.Id));
        _ = facet switch
        {
            FactionRecencyFacet.Members => await rows.ExecuteUpdateAsync(
                s => s.SetProperty(f => f.MembersRefreshedAt, now), cancellationToken),
            FactionRecencyFacet.Stock => await rows.ExecuteUpdateAsync(
                s => s.SetProperty(f => f.StockRefreshedAt, now), cancellationToken),
            FactionRecencyFacet.Activities => await rows.ExecuteUpdateAsync(
                s => s.SetProperty(f => f.ActivitiesRefreshedAt, now), cancellationToken),
            FactionRecencyFacet.Docs => await rows.ExecuteUpdateAsync(
                s => s.SetProperty(f => f.DocsRefreshedAt, now), cancellationToken),
            _ => 0,
        };
    }

    private static DateTime Older(DateTime a, DateTime b) => a <= b ? a : b;
}
