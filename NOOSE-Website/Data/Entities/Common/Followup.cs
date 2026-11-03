using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>A scheduled follow-up/reminder on any record (polymorphic by entity type + id); the background worker notifies on due date.</summary>
[Table("Wiedervorlagen")]
public class Followup : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("EntitaetTyp")]
    public string EntityType { get; set; } = string.Empty;

    [Column("EntitaetId")]
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Due date (UTC); overdue = in the past while still open.</summary>
    [Column("FaelligAm")]
    public DateTime DueAt { get; set; }

    [Column("Notiz")]
    public string? Note { get; set; }

    /// <summary>Responsible agent; defaults to creator, decoupled via OnDelete SetNull.</summary>
    [Column("ZustaendigerAgentId")]
    public string? ResponsibleAgentId { get; set; }

    [Column("Erledigt")]
    public bool Done { get; set; }
    [Column("ErledigtAm")]
    public DateTime? DoneAt { get; set; }
    [Column("ErledigtVonId")]
    public string? DoneById { get; set; }

    /// <summary>Set once the due notification was sent; dedupes the recurring background check.</summary>
    [Column("BenachrichtigtAm")]
    public DateTime? NotifiedAt { get; set; }

    /// <summary>Set once leadership was told it is long overdue; a new due date clears it.</summary>
    [Column("EskaliertAm")]
    public DateTime? EscalatedAt { get; set; }

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
