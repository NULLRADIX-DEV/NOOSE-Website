using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>A statute/legal basis in the knowledge base; leadership-curated, readable by all active agents.</summary>
[Table("Gesetze")]
public class Law : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Gesetzbuch")]
    public string LawBook { get; set; } = string.Empty;

    [Column("Paragraf")]
    public string Paragraph { get; set; } = string.Empty;

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Heading of the part of the book the paragraph stands in, e.g. "I. – Allgemeiner Teil".</summary>
    [Column("Abschnitt")]
    public string? Section { get; set; }

    /// <summary>Position within the book.</summary>
    [Column("Reihenfolge")]
    public int SortOrder { get; set; }

    /// <summary>Sanitized HTML.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Optional sentence / legal consequence as free text.</summary>
    [Column("Strafmass")]
    public string? Sentence { get; set; }

    /// <summary>Released for the public law page; off unless somebody says otherwise.</summary>
    /// <remarks>
    /// Written by <c>IPublicLawService</c> alone, so the release decision and the public snapshot cannot drift apart.
    /// </remarks>
    [Column("IstOeffentlich")]
    public bool IsPublic { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }

    [Column("IstGeloescht")]
    public bool IsDeleted { get; set; }
    [Column("GeloeschtAm")]
    public DateTime? DeletedAt { get; set; }
    [Column("GeloeschtVonId")]
    public string? DeletedById { get; set; }
}
