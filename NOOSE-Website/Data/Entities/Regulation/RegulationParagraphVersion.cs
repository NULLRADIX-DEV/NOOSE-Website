using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Regulation;

/// <summary>One wording of a paragraph; written once and never changed afterwards.</summary>
[Table("DvoAbsatzFassungen")]
public class RegulationParagraphVersion : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("AbsatzId")]
    public string ParagraphId { get; set; } = string.Empty;
    public RegulationParagraph? Paragraph { get; set; }

    [Column("Fassung")]
    public int Version { get; set; }

    /// <summary>Heading as published with this version.</summary>
    [Column("Titel")]
    public string Heading { get; set; } = string.Empty;

    /// <summary>Audience as published with this version.</summary>
    [Column("Zielgruppe")]
    public RegulationAudience Audience { get; set; }

    [Column("InhaltHtml")]
    public string ContentHtml { get; set; } = string.Empty;

    [Column("GueltigAb")]
    public DateOnly ValidFrom { get; set; }

    [Column("Aenderungsnotiz")]
    public string ChangeNote { get; set; } = string.Empty;

    /// <summary>False for a pure correction: every earlier acknowledgment keeps counting.</summary>
    [Column("KenntnisnahmeNoetig")]
    public bool RequiresAcknowledgment { get; set; }

    /// <summary>Who published it; an id without a foreign key, the name is the snapshot.</summary>
    [Column("VeroeffentlichtVonId")]
    public string? PublishedById { get; set; }

    [Column("VeroeffentlichtVonName")]
    public string? PublishedByName { get; set; }

    [Column("VeroeffentlichtAm")]
    public DateTime PublishedAt { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
