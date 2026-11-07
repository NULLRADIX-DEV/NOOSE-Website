using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Regulation;

/// <summary>One paragraph of the service regulation; its wording lives in immutable versions, its audience owes the acknowledgment.</summary>
[Table("DvoAbsaetze")]
public class RegulationParagraph : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("AbschnittId")]
    public string SectionId { get; set; } = string.Empty;
    public RegulationSection? Section { get; set; }

    /// <summary>Heading of the current version, such as "Funkdisziplin"; the versions keep their own snapshot.</summary>
    [Column("Titel")]
    public string Heading { get; set; } = string.Empty;

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }

    /// <summary>Who owes the acknowledgment; every internal agent reads every paragraph.</summary>
    [Column("Zielgruppe")]
    public RegulationAudience Audience { get; set; }

    /// <summary>Latest version number; the concurrency token that keeps two publishers apart.</summary>
    [Column("AktuelleFassung")]
    public int CurrentVersion { get; set; }

    /// <summary>Last version that asked for an acknowledgment; an earlier one stays valid from here on.</summary>
    [Column("KenntnisnahmeAbFassung")]
    public int AcknowledgeFromVersion { get; set; }

    [Column("AufgehobenAm")]
    public DateTime? RepealedAt { get; set; }

    /// <summary>Who repealed it; an id without a foreign key, the name is the snapshot.</summary>
    [Column("AufgehobenVonId")]
    public string? RepealedById { get; set; }

    [Column("AufgehobenVonName")]
    public string? RepealedByName { get; set; }

    [Column("Aufhebungsgrund")]
    public string? RepealReason { get; set; }

    public ICollection<RegulationParagraphVersion> Versions { get; set; } = new List<RegulationParagraphVersion>();

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
