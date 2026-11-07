using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Directives;

/// <summary>An agent's acknowledgment of one version of a directive. FK to Agent is Restrict; FK to Directive is Cascade.</summary>
[Table("DienstanweisungKenntnisnahmen")]
public class DirectiveAcknowledgment : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("DienstanweisungId")]
    public string DirectiveId { get; set; } = string.Empty;
    public Directive? Directive { get; set; }

    [Column("Fassung")]
    public int Version { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    [Column("KenntnisGenommenAm")]
    public DateTime AcknowledgedAt { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
