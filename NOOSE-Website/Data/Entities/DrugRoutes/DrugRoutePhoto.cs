using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.DrugRoutes;

/// <summary>Evidence photo of a drug route, optionally pinned to one station; the file lives outside wwwroot under a server-assigned name.</summary>
[Table("DrogenroutenFotos")]
public class DrugRoutePhoto : IAuditable, ISoftDelete, IRecordPhoto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("DrogenrouteId")]
    public string DrugRouteId { get; set; } = string.Empty;
    public DrugRoute? DrugRoute { get; set; }

    /// <summary>Station the photo belongs to; null = route gallery.</summary>
    [Column("StationId")]
    public string? StationId { get; set; }

    [Column("Bildunterschrift")]
    public string? Caption { get; set; }

    [Column("DateinameGespeichert")]
    public string FileNameSaved { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    [Column("GroesseBytes")]
    public long SizeBytes { get; set; }

    [Column("IstTitelbild")]
    public bool IsTitleImage { get; set; }

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
