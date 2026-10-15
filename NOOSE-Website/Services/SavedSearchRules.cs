using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <summary>What a saved search means when it is applied or watched; shared by the search page and the watch worker.</summary>
public static class SavedSearchRules
{
    /// <summary>Watched searches per agent; each one is a full search every <see cref="WatchInterval"/>.</summary>
    public const int MaxWatchedPerAgent = 5;

    /// <summary>How often one watched search runs at most.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromHours(1);

    /// <summary>Watched searches one worker tick runs, one after another.</summary>
    public const int WatchBatch = 10;

    /// <summary>Upper bound of stored hit keys; above every category's cap times the catalog.</summary>
    public const int MaxKnownHits = 5_000;

    /// <summary>Upper bound of marked-new keys between two visits.</summary>
    public const int MaxUnseenHits = 200;

    private const int PreviewTitles = 3;
    private const int MaxTitle = 300;

    /// <summary>The stored criteria, or null for an unreadable row.</summary>
    public static SearchCriteria? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SearchCriteria>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The category a saved search narrows to, or null for all of them.</summary>
    /// <remarks>Rows from the old checkbox filter carry <see cref="SearchCriteria.Categories"/> as a pre-filter. Not
    /// honoured, it hid categories from the reader; a single saved category survives as the facet.</remarks>
    public static string? FacetOf(SearchCriteria criteria)
        => criteria.Facet is { Length: > 0 } facet && SearchCatalog.Find(facet) is not null ? facet
            : criteria.Categories is [var only] && SearchCatalog.Find(only) is not null ? only
            : null;

    /// <summary>Categories a watch searches: records, their content and the administration.</summary>
    /// <remarks>Never the agent's own entries or the logs. The report itself is a notification that names the search,
    /// so watching notifications would report its own report on every run; a log grows with every edit or view of a
    /// matching record and would read as a new hit each time.</remarks>
    public static readonly IReadOnlyList<string> WatchableCategories = SearchCatalog.Categories
        .Where(c => c.Group is SearchGroup.Records or SearchGroup.Content or SearchGroup.Administration)
        .Select(c => c.Clr)
        .ToList();

    // part of the stamp: a new category would report every old match as new
    private static readonly string CatalogStamp = Fingerprint(string.Join(',', WatchableCategories))[..8];

    /// <summary>Whether a search says enough to be watched; an empty one would report every new record.</summary>
    public static bool IsWatchable(SearchCriteria criteria)
        => ((criteria.Text?.Trim().Length ?? 0) >= 2 || criteria.TagIds.Count > 0)
           && (FacetOf(criteria) is not { } facet || WatchableCategories.Contains(facet));

    /// <summary>Who may watch: internal agents who may write. The worker asks again on every run.</summary>
    public static bool MayWatch(ClaimsPrincipal user) => user.MayWrite() && user.IsInternalAgent();

    /// <summary>The query a watch run sends: the saved facet restricts the server, since only its hits are reported.</summary>
    /// <remarks>Never an empty category list: to the search service that means every category.</remarks>
    public static SearchCriteria ForWatch(SearchCriteria criteria)
    {
        var facet = FacetOf(criteria) is { } f && WatchableCategories.Contains(f) ? f : null;
        return new SearchCriteria
        {
            Text = criteria.Text,
            TagIds = criteria.TagIds.ToList(),
            Fuzzy = criteria.Fuzzy,
            MaxMode = criteria.MaxMode,
            IncludeArchived = criteria.IncludeArchived,
            Facet = facet,
            Categories = facet is null ? WatchableCategories.ToList() : [facet],
        };
    }

    /// <summary>Fingerprint of everything that decides what this viewer may find, and of where a watch looks.</summary>
    /// <remarks>A changed stamp makes the next run a silent baseline: after a promotion every newly visible record
    /// would otherwise arrive as a "new" hit, and after a deploy that makes a category watchable every old record
    /// of it would.</remarks>
    public static string ScopeStamp(ViewerScope scope)
        => string.Join('|',
            Bit(scope.MayClassifiedRead), Bit(scope.MayAllTaskforces), Bit(scope.IsTru), Bit(scope.IsHrb),
            Bit(scope.IsLeadership), Bit(scope.MayAgenda), Bit(scope.IsAdmin), Bit(scope.MayRealName),
            Bit(scope.IsInternalAgent), scope.Rank is { } rank ? ((int)rank).ToString() : "-",
            scope.PartnerAgency is { } agency ? ((int)agency).ToString() : "-", CatalogStamp);

    /// <summary>Marks a baseline that ran short and gets one more try; it never equals a real stamp.</summary>
    public static string RetryStamp(string stamp) => "~" + stamp;

    /// <summary>Where a report points: the search page applies the saved search. The id names nothing.</summary>
    public static string Href(string savedSearchId) => $"/suche?gespeichert={Uri.EscapeDataString(savedSearchId)}";

    /// <summary>A hit's identity for a watch and its new-marks.</summary>
    /// <remarks>A content hit carries its parent's id, so a second matching comment on a known record would share
    /// the first one's key and never be reported. A fingerprint of its text tells them apart, without storing it.</remarks>
    public static string Key(SearchHit hit)
        => SearchCatalog.Shape(hit.Category) == SearchHitShape.Content
            ? $"{SearchSelection.Key(hit)}:{Fingerprint(hit.Snippet)}"
            : SearchSelection.Key(hit);

    /// <summary>Stored hit keys; an unreadable value counts as none.</summary>
    public static List<string> ReadKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteKeys(IEnumerable<string> keys) => JsonSerializer.Serialize(keys);

    /// <summary>What a watch run keeps and what it reports.</summary>
    /// <param name="Known">Keys to compare the next run against.</param>
    /// <param name="Fresh">Hits the previous run did not have, in result order; empty on a baseline.</param>
    public sealed record Outcome(IReadOnlyList<string> Known, IReadOnlyList<SearchHit> Fresh);

    /// <summary>Compares a run against everything earlier runs found.</summary>
    /// <param name="known">The keys found so far, or null when no run set a baseline yet.</param>
    /// <param name="baseline">Record without reporting: first run, or the owner's rights changed.</param>
    /// <remarks>A key stays known once found. A hit drops out for reasons that are not news - the budget cut its
    /// category off, the cap or the ranking pushed it out, the phonetic pass did not run, it was archived for a
    /// while - and it must not come back as "new". The current run goes first, so the cap trims the oldest.</remarks>
    public static Outcome Compare(IReadOnlyCollection<string>? known, bool baseline, SearchResults results)
    {
        var current = new List<string>();
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in results.Groups.SelectMany(g => g.Hit))
        {
            var key = Key(hit);
            if (seen.Add(key))
            {
                current.Add(key);
                hits.Add(hit);
            }
        }

        var next = current.Concat((known ?? []).Where(k => !seen.Contains(k)))
            .Distinct(StringComparer.Ordinal).Take(MaxKnownHits).ToList();
        if (baseline || known is null)
        {
            return new Outcome(next, []);
        }
        var before = known as IReadOnlySet<string> ?? known.ToHashSet(StringComparer.Ordinal);
        return new Outcome(next, hits.Where(h => !before.Contains(Key(h))).ToList());
    }

    /// <summary>Adds freshly reported keys to the ones the owner has not looked at yet, newest first.</summary>
    public static List<string> AddUnseen(IEnumerable<string> unseen, IEnumerable<string> fresh)
        => fresh.Concat(unseen).Distinct(StringComparer.Ordinal).Take(MaxUnseenHits).ToList();

    /// <summary>The bell line: the search's name, how many hits wait, and the first few of this run.</summary>
    public static string Title(string name, int unseen, IReadOnlyList<SearchHit> fresh)
    {
        var count = unseen == 1 ? "1 neuer Treffer" : $"{unseen} neue Treffer";
        var titles = fresh.Select(h => h.Title.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        var text = $"Gespeicherte Suche „{name.Trim()}“: {count}";
        if (titles.Count > 0)
        {
            text += " – " + string.Join(", ", titles.Take(PreviewTitles));
            if (titles.Count > PreviewTitles)
            {
                text += $" und {titles.Count - PreviewTitles} weitere";
            }
        }
        return text.Length > MaxTitle ? text[..(MaxTitle - 1)] + "…" : text;
    }

    private static string Bit(bool value) => value ? "1" : "0";

    // stable across processes, unlike string.GetHashCode
    private static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}
