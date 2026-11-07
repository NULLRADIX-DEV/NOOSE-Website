using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Directives;

/// <summary>One wording of a directive; written once and never changed afterwards.</summary>
[Table("DienstanweisungFassungen")]
public class DirectiveVersion : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("DienstanweisungId")]
    public string DirectiveId { get; set; } = string.Empty;
    public Directive? Directive { get; set; }

    [Column("Fassung")]
    public int Version { get; set; }

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
