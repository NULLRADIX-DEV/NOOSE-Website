using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>Releases a record (or a single child item) to one partner unit. Own table: older code reads agency rows as agency-wide.</summary>
[Table("PartnerEinheitFreigaben")]
public class PartnerUnitShare : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Polymorphic CLR type name (nameof).</summary>
    [Column("EntitaetTyp")]
    public string EntityType { get; set; } = string.Empty;

    [Column("EntitaetId")]
    public string EntityId { get; set; } = string.Empty;

    [Column("PartnerEinheitId")]
    public string PartnerUnitId { get; set; } = string.Empty;

    /// <summary>True = incl. all current and future children; false = shell only.</summary>
    [Column("InklusiveKinder")]
    public bool IncludesChildren { get; set; }

    /// <summary>UTC instant the release ends; null = unlimited.</summary>
    [Column("GueltigBis")]
    public DateTime? ValidUntil { get; set; }

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
