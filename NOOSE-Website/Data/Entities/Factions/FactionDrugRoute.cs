using System.ComponentModel.DataAnnotations.Schema;
namespace NOOSE_Website.Data.Entities.Factions;

/// <summary>Legacy faction drug route row; replaced by the DrugRoute record, kept mapped until the table is dropped.</summary>
[Table("FraktionDrogenrouten")]
public class FactionDrugRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Column("FraktionId")]
    public string FactionId { get; set; } = string.Empty;
    public Faction? Faction { get; set; }
    [Column("Bezeichnung")]
    public string Designation { get; set; } = string.Empty;

    [Column("Notiz")]
    public string? Note { get; set; }
}
