using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Directives;

/// <summary>A numbered service directive issued by leadership; its wording lives in immutable versions.</summary>
[Table("Dienstanweisungen")]
public class Directive : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Permanent number, never reissued; shown as DA-01.</summary>
    [Column("Nummer")]
    public int Number { get; set; }

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    [Column("Sachgebiet")]
    public string? Subject { get; set; }

    /// <summary>Who may see it, and so who owes the acknowledgment.</summary>
    [Column("Einstufung")]
    public DocumentClassification Classification { get; set; }

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

    public ICollection<DirectiveVersion> Versions { get; set; } = new List<DirectiveVersion>();

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
