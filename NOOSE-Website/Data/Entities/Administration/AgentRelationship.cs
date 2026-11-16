using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Administration;

/// <summary>A reported personal relationship between two agents; makes each biased in the other's personnel matters.</summary>
[Table("Personalbeziehungen")]
public class AgentRelationship : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The lower of the two ids (ordinal), so a pair has one spelling.</summary>
    [Column("AgentAId")]
    public string AgentAId { get; set; } = string.Empty;

    [Column("AgentBId")]
    public string AgentBId { get; set; } = string.Empty;

    [Column("Art")]
    public AgentRelationshipKind Kind { get; set; }

    [Column("Seit")]
    public DateTime? Since { get; set; }

    [Column("Notiz")]
    public string? Note { get; set; }

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
