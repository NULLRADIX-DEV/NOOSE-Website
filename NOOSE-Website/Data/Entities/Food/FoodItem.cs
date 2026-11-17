using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Food;

/// <summary>A requestable food or drink; name, picture and stock come from the linked evidence-room item.</summary>
[Table("Verpflegungspositionen")]
public class FoodItem : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Evidence-room item the issue books out of; one catalog entry per item (service-checked).</summary>
    [Column("AsservatId")]
    public string EvidenceItemId { get; set; } = string.Empty;
    public EvidenceItem? EvidenceItem { get; set; }

    /// <summary>Food or drink; decides which weekly limit the quantity counts against.</summary>
    [Column("Art")]
    public FoodKind Kind { get; set; } = FoodKind.Food;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    /// <summary>Upper bound for the quantity of this position within one request.</summary>
    [Column("MaxMenge")]
    public int MaxQuantity { get; set; } = 1;

    /// <summary>Only active positions can be requested.</summary>
    [Column("IstAktiv")]
    public bool IsActive { get; set; } = true;

    [Column("Sortierung")]
    public int Sorting { get; set; }

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
