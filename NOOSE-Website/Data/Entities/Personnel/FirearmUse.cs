using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Personnel;

/// <summary>One reported use of a firearm: who, when, and a short reason; filed by the agent from the top bar.</summary>
[Table("Schusswaffengebraeuche")]
public class FirearmUse : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>The agent who fired and reported; the service stamps it from the actor, never from input.</summary>
    public string AgentId { get; set; } = string.Empty;
    public Agent? Agent { get; set; }

    /// <summary>When the report was filed (UTC); the agent does not choose it.</summary>
    [Column("Zeitpunkt")]
    public DateTime Timestamp { get; set; }

    [Column("Begruendung")]
    public string Reason { get; set; } = string.Empty;

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
