namespace NOOSE_Website.Models.Common;

/// <summary>Input model for a law-book paragraph.</summary>
public class LawInput
{
    public string LawBook { get; set; } = string.Empty;
    public string Paragraph { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>HTML from the editor; sanitized on save.</summary>
    public string Text { get; set; } = string.Empty;
    public string? Sentence { get; set; }
    public string? Section { get; set; }

    /// <summary>Position in the book; 0 or less appends at the end.</summary>
    public int SortOrder { get; set; }
}

/// <summary>Input model for a law book in the catalog.</summary>
public class LawBookInput
{
    public string Abbreviation { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>One law book as the overview shows it.</summary>
/// <param name="Id">Catalog id; null for a book only named by its paragraphs.</param>
public sealed record LawBookSummary(
    string? Id,
    string Abbreviation,
    string Name,
    string? Description,
    int SortOrder,
    int ParagraphCount);

/// <summary>One law book with its paragraphs in reading order.</summary>
public sealed record LawBookContent(LawBookSummary Book, IReadOnlyList<Data.Entities.Common.Law> Paragraphs);
