using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.DrugRoutes;

/// <summary>A person involved in a drug route (driver, seller …); soft delete keeps the history.</summary>
[Table("DrogenroutenPersonen")]
public class DrugRoutePerson : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("DrogenrouteId")]
    public string DrugRouteId { get; set; } = string.Empty;
    public DrugRoute? DrugRoute { get; set; }

    public string PersonId { get; set; } = string.Empty;
    public Person? Person { get; set; }

    [Column("Rolle")]
    public string? Role { get; set; }

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
