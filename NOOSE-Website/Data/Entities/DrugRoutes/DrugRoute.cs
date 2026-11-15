using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.DrugRoutes;

/// <summary>A drug route as a full case file: stations, evidence photos, involved people and the faction running it.</summary>
[Table("Drogenrouten")]
public class DrugRoute : IAuditable, ISoftDelete, IClassifiableRecord, IArchivable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. FIB-DR-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    /// <summary>Route name; unique among active routes, compared via <see cref="Services.DrugRouteRules.Key"/>.</summary>
    [Column("Bezeichnung")]
    public string Designation { get; set; } = string.Empty;

    /// <summary>Substance moved along the route (free text).</summary>
    [Column("Substanz")]
    public string? Substance { get; set; }

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("Erkenntnisse")]
    public string? Findings { get; set; }

    [Column("Bemerkungen")]
    public string? Remarks { get; set; }

    /// <summary>Faction running the route; null while unknown.</summary>
    [Column("FraktionId")]
    public string? FactionId { get; set; }
    public Faction? Faction { get; set; }

    [Column("Einstufung")]
    public Classification Classification { get; set; } = Classification.Unknown;

    [Column("IstVerschlusssache")]
    public bool IsClassified { get; set; }

    [Column("IstVerschlusssacheTRU")]
    public bool IsTRUClassified { get; set; }

    [Column("IstVerschlusssacheHRB")]
    public bool IsHRBClassified { get; set; }

    /// <summary>Unified secrecy level: None when unrestricted, else TRU/HRB by audience flag, otherwise leadership.</summary>
    [NotMapped]
    public DocumentClassification SecrecyLevel
    {
        get => !IsClassified ? DocumentClassification.None
            : IsTRUClassified ? DocumentClassification.Tru
            : IsHRBClassified ? DocumentClassification.Hrb
            : DocumentClassification.Leadership;
        set
        {
            IsClassified = value != DocumentClassification.None;
            IsTRUClassified = value == DocumentClassification.Tru;
            IsHRBClassified = value == DocumentClassification.Hrb;
        }
    }

    [NotMapped]
    public bool IsRestricted => IsClassified || IsTRUClassified || IsHRBClassified;

    [Column("VeralterungDeaktiviert")]
    public bool AgingDisabled { get; set; }

    public List<DrugRouteStation> Stations { get; set; } = new();
    public List<DrugRoutePerson> People { get; set; } = new();
    public List<DrugRoutePhoto> Photos { get; set; } = new();

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

    [Column("IstArchiviert")]
    public bool IsArchived { get; set; }
    [Column("ArchiviertAm")]
    public DateTime? ArchivedAt { get; set; }
    [Column("ArchiviertVonId")]
    public string? ArchivedById { get; set; }
    [Column("Archivgrund")]
    public string? ArchiveReason { get; set; }
}
