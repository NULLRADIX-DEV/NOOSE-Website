namespace NOOSE_Website.Models.Public;

/// <summary>One released paragraph as the public reads it.</summary>
/// <remarks>
/// <see cref="Text"/> is sanitized HTML with every mention token stripped — the page renders it as markup, never
/// through the mention resolver, which would print internal record names outside. <see cref="PlainText"/> is the
/// same body without tags, for the public search. Carries no id, no author and no timestamps: a statute is the
/// text, and the rest is bookkeeping.
/// </remarks>
public sealed record PublicLawEntry(string Paragraph, string Title, string Text, string? Sentence, string PlainText = "");

/// <summary>The released paragraphs of one law book, in reading order.</summary>
/// <param name="Name">The abbreviation, e.g. "StGB".</param>
/// <param name="Title">The full name from the catalog; null when the book has no catalog row.</param>
public sealed record PublicLawBook(string Name, IReadOnlyList<PublicLawEntry> Entries, string? Title = null);

/// <summary>Everything the public law page reads, cached as one unit.</summary>
public sealed record PublicLawSnapshot(IReadOnlyList<PublicLawBook> Books)
{
    public static PublicLawSnapshot Empty { get; } = new([]);
}

/// <summary>One paragraph in the release panel: what it is, and whether it is out.</summary>
public sealed record LawReleaseRow(string Id, string LawBook, string Paragraph, string Title, bool IsPublic);
