using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.DrugRoutes;

/// <summary>One stop along a drug route (gathering, processing, sale …), in route order.</summary>
[Table("DrogenroutenStationen")]
public class DrugRouteStation : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("DrogenrouteId")]
    public string DrugRouteId { get; set; } = string.Empty;
    public DrugRoute? DrugRoute { get; set; }

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }

    [Column("Art")]
    public DrugRouteStationKind Kind { get; set; } = DrugRouteStationKind.Other;

    [Column("Ort")]
    public string Location { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

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
