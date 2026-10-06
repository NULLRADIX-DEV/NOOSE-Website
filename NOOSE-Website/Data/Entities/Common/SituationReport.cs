using NOOSE_Website.Models.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Common;

/// <summary>Archived monthly situation report; frozen stats snapshot.</summary>
[Table("Lageberichte")]
public class SituationReport : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Jahr")]
    public int Year { get; set; }

    [Column("Monat")]
    public int Month { get; set; }

    [Column("Titel")]
    public string Title { get; set; } = string.Empty;

    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>Funding figures of the month; separate column because SnapshotJson is a frozen wire format that must not gain members.</summary>
    [Column("FinanzierungJson")]
    public string? FinancingJson { get; set; }

    /// <summary>Same statistics without classified records, for partner agencies; null on reports from before it existed.</summary>
    [Column("FreigabeSnapshotJson")]
    public string? ReleasedSnapshotJson { get; set; }

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
