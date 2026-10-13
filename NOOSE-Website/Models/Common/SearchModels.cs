using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Models.Common;

/// <summary>Global search criteria; persisted as JSON for saved searches (missing flags default to false).</summary>
public class SearchCriteria
{
    public string? Text { get; set; }

    /// <summary>Restricts what the SERVER queries. Set by <c>suche_akten</c> and by the "Rest nachladen" retry —
    /// never by the search page.</summary>
    public List<string> Categories { get; set; } = new();

    public List<string> TagIds { get; set; } = new();

    /// <summary>Category the PAGE narrows the already-complete result to. Catalog key, or null for "Alle".</summary>
    /// <remarks>Separate from <see cref="Categories"/> on purpose: that one restricts what the server queries, and
    /// a pre-filter the reader later forgets about hides whole categories from them without saying so.</remarks>
    public string? Facet { get; set; }

    /// <summary>Typo tolerance via in-memory Levenshtein on top of exact search.</summary>
    public bool Fuzzy { get; set; }

    /// <summary>Also searches all side fields, forces docs/sources/comments, and extends fuzzy to content fields.</summary>
    public bool MaxMode { get; set; }

    /// <summary>Includes archived records. Off by default: the archive is out of the way, not gone.</summary>
    public bool IncludeArchived { get; set; }
}

/// <summary>A single search hit. Category is the CLR type of the source; TargetType null means category is the target type.</summary>
public record SearchHit(string Category, string TargetId, string Title, string Snippet, string CaseNumber, string? TargetType = null)
{
    /// <summary>Stripped here rather than at the ~20 call sites: result lists render the snippet raw and never resolve mention tokens.</summary>
    /// <remarks>Providers must additionally deliver PLAIN TEXT (see <c>HtmlCleanup.PlainText</c>) — never markup.</remarks>
    public string Snippet { get; init; } = NOOSE_Website.Services.MentionParser.Strip(Snippet);

    /// <summary>When the hit happened. Rendered by the log and personal row shapes.</summary>
    public DateTime? Timestamp { get; init; }

    /// <summary>Who acted. Codename only — a real name never reaches a result row.</summary>
    public string? Actor { get; init; }

    /// <summary>Section slug, overriding the category's default. Rare.</summary>
    public string? Tab { get; init; }

    /// <summary>Route this row leads to, when it varies per hit rather than per category.</summary>
    /// <remarks>Must be produced by <see cref="SearchNavigation"/> or be a constant page route — never assembled
    /// from a raw id, or a hit could link into a record the viewer never passed a gate for.</remarks>
    public string? Href { get; init; }
}

/// <summary>Hits of one category bundled for grouped display.</summary>
/// <param name="Capped">The category filled its per-category cap; there may be more. Rendered as "50+" so a
/// count is never read as a statement about the corpus.</param>
public record SearchResultGroup(string Category, string Display, List<SearchHit> Hit, bool Capped = false);

/// <summary>What one global search produced.</summary>
/// <remarks>Incomplete is the honest half of a wall-clock budget: zero hits in a category that was cut off means
/// "not looked at", not "nothing there", and the two must not render the same.</remarks>
public sealed record SearchResults(
    IReadOnlyList<SearchResultGroup> Groups,
    int Total,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Incomplete,
    int VisibleCategories,
    TimeSpan Elapsed)
{
    public static readonly SearchResults None = new([], 0, [], [], 0, TimeSpan.Zero);

    /// <summary>A fuzzy search ran out of time before the phonetic pass; its weakest matches are missing.</summary>
    public bool SideIndexSkipped { get; init; }
}

/// <summary>Compact hit for the command palette.</summary>
public record QuickHit(string Category, string TargetId, string Name, string CaseNumber);

/// <summary>Target route of a hit; content resolves to its parent record.</summary>
public static class SearchNavigation
{
    /// <summary>Route of a record type, or null when it has no page of its own.</summary>
    /// <remarks>Null rather than a guess. The former fallback sent every unmapped type to <c>/personen/{id}</c>,
    /// so a comment on a faction opened a person file with the faction's id — a wrong record, silently.</remarks>
    public static string? For(string? recordsType, string targetId)
        => SearchCatalog.Route(recordsType, targetId);

    /// <summary>Route of a hit: the record page of its explicit target type, else of its category, plus the
    /// section the content lives in.</summary>
    public static string? For(SearchHit hit)
    {
        if (hit.Href is { Length: > 0 } own)
        {
            return own;
        }
        var target = string.IsNullOrEmpty(hit.TargetType) ? hit.Category : hit.TargetType;
        if (For(target, hit.TargetId) is not { } route)
        {
            return null;
        }
        var tab = hit.Tab ?? SearchCatalog.ParentTab(hit.Category);
        return tab is null ? route : $"{route}?tab={tab}";
    }

    /// <summary>Whether a record type leads to a page of its own.</summary>
    public static bool Knows(string? recordsType) => SearchCatalog.IsRoutable(recordsType);
}
