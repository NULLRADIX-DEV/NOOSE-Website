using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Food;

/// <summary>One basket line of a food request; name and kind are copied so later catalog edits never move a filed request.</summary>
[Table("Verpflegungsanforderungspositionen")]
public class FoodRequestLine : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("AnforderungId")]
    public string RequestId { get; set; } = string.Empty;
    public FoodRequest? Request { get; set; }

    /// <summary>Source position; null once the catalog entry is gone.</summary>
    [Column("PositionId")]
    public string? ItemId { get; set; }
    public FoodItem? Item { get; set; }

    /// <summary>Evidence-room item the issue books out of, copied at filing time.</summary>
    [Column("AsservatId")]
    public string? EvidenceItemId { get; set; }

    [Column("Bezeichnung")]
    public string ItemName { get; set; } = string.Empty;

    [Column("Art")]
    public FoodKind Kind { get; set; }

    [Column("Menge")]
    public int Quantity { get; set; }

    /// <summary>Quantity leadership actually granted; null until decided, 0 means the line was struck.</summary>
    [Column("GenehmigteMenge")]
    public int? ApprovedQuantity { get; set; }

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
}
