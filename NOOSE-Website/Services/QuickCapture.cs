using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <summary>What the quick-capture dialog may write to, decided here rather than in the dialog.</summary>
/// <remarks>
/// The dialog asks the ordinary quick search for candidates, and that search answers for every category
/// carrying <c>SearchTraits.Quick</c> - which is a wider set than the one a note may be filed against. The
/// narrowing lives here because a table in a <c>.razor</c> code block would have no test: there is no bUnit
/// in this repo. <c>MentionService.CandidatesAsync</c> narrows the same way for the same reason.
/// </remarks>
public static class QuickCapture
{
    /// <summary>Record types a note may be filed against from the header.</summary>
    /// <remarks>
    /// The intersection of "has a comment section" (sixteen types) and "the quick search finds it" (fourteen
    /// categories), minus the personnel file. Four of the quick categories are deliberately absent and the
    /// reasons differ: a handbook article, a glossary term and a radio channel fall through the tail of
    /// <see cref="Visibility.IsRecordVisibleAsync"/>, where an unknown type counts as visible - a note filed
    /// there would pass no gate at all and be displayed nowhere, and the handbook hit carries the slug rather
    /// than an id. A document is gated properly but shows no comments, so the note would be written and
    /// invisible for good. The personnel file is gated to leadership while the quick search offers it to
    /// everyone, so the entry would promise nine agents out of ten something the service then refuses - and
    /// its own "Vermerk" is a different record (<c>AgentNote</c>) with a different form.
    /// </remarks>
    public static readonly IReadOnlySet<string> CommentTargets = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(DrugRoute), nameof(Operation),
        nameof(Case), nameof(Taskforce), nameof(Job), nameof(Meeting),
    };

    /// <summary>Record types an activity may be linked to. The link table accepts no others.</summary>
    public static readonly IReadOnlySet<string> ActivityTargets = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Faction), nameof(PersonGroup),
    };

    /// <summary>Upper bound on what a picker fetches, however narrow its type set is.</summary>
    private const int MaxFetch = 60;

    /// <summary>How many hits to ask the quick search for so the accepted types can still fill the list.</summary>
    /// <remarks>
    /// The quick search hands its slots out round-robin across every quick category and only then cuts at the
    /// number asked for, so a picker that accepts two categories out of fourteen sees two rows no matter what
    /// it asked for - and an agent typing a faction name would be told there is no such faction while seven
    /// match. Asking for the share that gets thrown away is the whole fix; the cap keeps the narrowest picker
    /// from sweeping the index for one row.
    /// </remarks>
    public static int FetchCount(int max, IReadOnlySet<string> allowed)
    {
        if (max <= 0)
        {
            return 0;
        }
        var quick = SearchCatalog.Categories.Count(c => c.Has(SearchTraits.Quick));
        var mine = allowed.Count(a => SearchCatalog.Has(a, SearchTraits.Quick));
        if (mine <= 0 || quick <= mine)
        {
            return max;
        }
        // round up: asking for the exact share leaves the last row to the rounding
        return Math.Min(max * ((quick + mine - 1) / mine), MaxFetch);
    }

    /// <summary>Quick hits reduced to the types this picker accepts.</summary>
    /// <remarks>
    /// The quick search hands out its slots round-robin across every category, so asking for as many as the
    /// picker shows would leave almost nothing after the filter. Callers ask for more and cap here.
    /// </remarks>
    public static List<QuickHit> Only(IEnumerable<QuickHit>? hits, IReadOnlySet<string> allowed, int max)
    {
        if (hits is null || max <= 0)
        {
            return [];
        }
        return hits.Where(h => allowed.Contains(h.Category)).Take(max).ToList();
    }

    /// <summary>Does the text already carry a pasted picture?</summary>
    /// <remarks>
    /// The picture is stored against the record that was picked when it was pasted, so changing the target
    /// afterwards would file the note on one record and leave its picture hanging on another. Not a leak - the
    /// picture stays behind its own carrier - but a picture pointing nowhere, so the picker locks instead.
    /// </remarks>
    public static bool HoldsImage(string? text)
        => MentionParser.Parse(text).Any(t => string.Equals(t.Type, nameof(TextImage), StringComparison.Ordinal));
}
