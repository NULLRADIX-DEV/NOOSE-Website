using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <summary>Which record types a batch write from the search page may touch, decided here and enforced by the services.</summary>
/// <remarks>
/// The search page only asks. <c>LinkService.CreateManyAsync</c>, <c>TagService.AddManyAsync</c> and
/// <c>WatchlistService.FollowManyAsync</c> refuse any type outside these sets, because
/// <see cref="Visibility.IsRecordVisibleAsync"/> answers an unknown type as visible - a set left open here
/// would let a forged call write to a record nobody vetted.
/// </remarks>
public static class RecordBatch
{
    /// <summary>Most records one batch action may touch.</summary>
    public const int Max = 50;

    /// <summary>Types that may receive a link: the link engine resolves them and each has a page of its own.</summary>
    /// <remarks>Derived rather than listed: a document, an observation or an agenda item is a known link type too,
    /// but its search hit points at its parent, so it can never be picked.</remarks>
    public static readonly IReadOnlySet<string> Linkable = LinkService.KnownTypes
        .Where(t => SearchCatalog.Find(t) is { Shape: SearchHitShape.Record, RouteTemplate: not null })
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Types the picked records may be linked to: each lists its links on its own page.</summary>
    /// <remarks>
    /// Narrower than <see cref="Linkable"/> on purpose. Faction, group and party only list conflicts and
    /// alliances, a document, a personnel file and a ticket list no links at all - a batch link there would be
    /// visible from the other end only. A citizen tip is left out because a tip-to-person link means "taken
    /// over into this person file" to <c>TipTakeoverService</c>, and eight of them would block the takeover.
    /// </remarks>
    public static readonly IReadOnlySet<string> LinkAnchors = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Case), nameof(Operation), nameof(Taskforce), nameof(Job),
        nameof(Person), nameof(Meeting), nameof(Law), nameof(Bewerbung), nameof(Presentation),
    };

    private static readonly IReadOnlySet<string> Involved = LinkService.InvolvedTypes.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Types that may be linked to this anchor; empty for a type that is no anchor.</summary>
    /// <remarks>Operation and taskforce list every link under "Beteiligte" and accept only people and
    /// organisations on their own page, so the batch accepts exactly those.</remarks>
    public static IReadOnlySet<string> LinkTargetsFor(string anchorType)
        => !LinkAnchors.Contains(anchorType) ? None
            : anchorType is nameof(Operation) or nameof(Taskforce) ? Involved
            : Linkable;

    /// <summary>Types that may carry a tag: exactly those the search's tag filter finds again.</summary>
    /// <remarks>Document and appointment show tag chips too, but their search rows carry no tag filter - tagging
    /// eight hits and filtering by that tag would then return six.</remarks>
    public static readonly IReadOnlySet<string> Taggable = SearchCatalog.Clrs(SearchTraits.Tagged)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Types that may be followed: the records that carry a follow button.</summary>
    public static readonly IReadOnlySet<string> Followable = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party),
        nameof(Operation), nameof(Case), nameof(Taskforce), nameof(Agent),
    };

    /// <summary>Drops blanks and duplicates; throws above <see cref="Max"/>.</summary>
    public static IReadOnlyList<(string Type, string Id)> Normalize(IEnumerable<(string Type, string Id)> records)
    {
        var result = records
            .Where(r => !string.IsNullOrWhiteSpace(r.Type) && !string.IsNullOrWhiteSpace(r.Id))
            .Distinct()
            .ToList();
        if (result.Count > Max)
        {
            throw new InvalidOperationException($"Höchstens {Max} Akten auf einmal.");
        }
        return result;
    }

    /// <summary>Record exists and the viewer may see it; the personnel file needs its own existence check.</summary>
    public static async Task<bool> ExistsAndVisibleAsync(AppDbContext db, string type, string id, ViewerScope scope,
        CancellationToken cancellationToken = default)
    {
        // the visibility gate answers the personnel file by rank alone
        if (type == nameof(Agent)
            && !await db.Users.OnlyWithPersonnelFile().AnyAsync(a => a.Id == id, cancellationToken))
        {
            return false;
        }
        return await Visibility.IsRecordVisibleAsync(db, type, id, scope, cancellationToken);
    }
}
