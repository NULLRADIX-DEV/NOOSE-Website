using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Public;

/// <summary>One hit of the public search.</summary>
/// <remarks>
/// Outward. Structurally carries no record id, no internal case number, no codename, no hazard level and no amount:
/// what is not on the type cannot be rendered by accident. <c>Reference</c> is the PUBLIC designation only — an
/// <c>FA-</c>/<c>PM-</c> number, a book and paragraph, a report period — never a <c>FIB-P-</c> file number.
/// <para>
/// Relevance is a sort order, not a field. A score property would publish the matcher's weighting and read as a
/// ranking; the two surfaces that do rank published rows are the hazard lists, which say so and name their cap.
/// </para>
/// <para><c>Snippet</c> is PLAIN TEXT, always produced through <c>HtmlCleanup.PlainText</c>: it is an excerpt cut
/// mid-tag out of published markup, so rendering it as markup would emit unbalanced tags on an anonymous page.</para>
/// </remarks>
public sealed record PublicSearchHit(
    PublicSearchArea Area,
    string Title,
    string Snippet,
    string? Href,
    string? Reference,
    DateTime? PublishedAt);

/// <summary>The hits of one published surface.</summary>
/// <param name="Capped">The surface filled its per-area cap; the page names the cap rather than cutting silently.</param>
public sealed record PublicSearchGroup(
    PublicSearchArea Area,
    IReadOnlyList<PublicSearchHit> Hits,
    bool Capped);

/// <summary>What one public search produced.</summary>
public sealed record PublicSearchResults(string Query, IReadOnlyList<PublicSearchGroup> Groups)
{
    public static PublicSearchResults Empty { get; } = new(string.Empty, []);

    /// <summary>How many rows are shown. Never a statement about the stock — the same rule the internal search keeps.</summary>
    public int Shown => Groups.Sum(g => g.Hits.Count);

    public bool AnyCapped => Groups.Any(g => g.Capped);
}
