using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Presentations;

/// <summary>An agent invited to a private presentation.</summary>
[Table("PraesentationEinladungen")]
public class PresentationInvite : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PresentationId { get; set; } = string.Empty;
    public Presentation? Presentation { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
