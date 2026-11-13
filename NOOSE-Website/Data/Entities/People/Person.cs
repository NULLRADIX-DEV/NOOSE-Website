using System.ComponentModel.DataAnnotations.Schema;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Data.Entities.People;

/// <summary>Central person record; fully audited and soft-deletable.</summary>
[Table("Personen")]
public class Person : IAuditable, ISoftDelete, IClassifiableRecord, IArchivable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("Lebensstatus")]
    public LifeStatus LifeStatus { get; set; } = LifeStatus.Alive;

    /// <summary>Dead until (UTC).</summary>
    [Column("TotBis")]
    public DateTime? DeadUntil { get; set; }

    [Column("Einstufung")]
    public Classification Classification { get; set; } = Classification.Unknown;

    /// <summary>Restricted from general view; true when any secrecy level (leadership/TRU/HRB) is set.</summary>
    [Column("IstVerschlusssache")]
    public bool IsClassified { get; set; }

    /// <summary>Restricted to TRU (implies IsClassified); selects the audience instead of leadership-only.</summary>
    [Column("IstVerschlusssacheTRU")]
    public bool IsTRUClassified { get; set; }

    /// <summary>Restricted to HRB (implies IsClassified); selects the audience instead of leadership-only.</summary>
    [Column("IstVerschlusssacheHRB")]
    public bool IsHRBClassified { get; set; }

    /// <summary>Manually flagged for the wanted board, independent of classification/fugitive status.</summary>
    [Column("ZurFahndung")]
    public bool IsWanted { get; set; }

    /// <summary>Optional reason shown on the wanted board.</summary>
    [Column("Fahndungsgrund")]
    public string? WantedReason { get; set; }

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

    /// <summary>Any secrecy level set (drives the lock indicator).</summary>
    [NotMapped]
    public bool IsRestricted => IsClassified || IsTRUClassified || IsHRBClassified;

    /// <summary>Aging disabled: record never goes stale.</summary>
    [Column("VeralterungDeaktiviert")]
    public bool AgingDisabled { get; set; }

    /// <summary>Threat score (0–100).</summary>
    [Column("BedrohungsScore")]
    public int? ThreatScore { get; set; }

    /// <summary>Threat confidence (0–100).</summary>
    [Column("BedrohungsKonfidenz")]
    public int? ThreatConfidence { get; set; }

    [Column("BedrohungsDetailJson")]
    public string? ThreatDetailJson { get; set; }

    /// <summary>Last score calculation (UTC).</summary>
    [Column("ScoreBerechnetAm")]
    public DateTime? ScoreCalculatedAt { get; set; }

    /// <summary>Agent in charge of keeping the record current; set through IRecordKeeperService only.</summary>
    [Column("AktenfuehrerId")]
    public string? KeeperId { get; set; }

    /// <summary>Stands in for the keeper; never the keeper themselves.</summary>
    [Column("VertretungId")]
    public string? DeputyId { get; set; }

    public List<PersonAlias> Aliases { get; set; } = new();
    public List<PersonPhone> PhoneNumbers { get; set; } = new();
    public List<PersonVehicle> Vehicles { get; set; } = new();
    public List<PersonLocation> Locations { get; set; } = new();
    public List<PersonWeapon> Weapons { get; set; } = new();
    public List<PersonPhoto> Photos { get; set; } = new();
    public List<PersonDoc> Docs { get; set; } = new();
    public List<Observation> Observations { get; set; } = new();

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
    /// <summary>Optional note shown on the banner and in the audit row.</summary>
    [Column("Archivgrund")]
    public string? ArchiveReason { get; set; }

    [NotMapped]
    public LifeStatus EffectiveLifeStatus
        => LifeStatusLogic.Effective(LifeStatus, DeadUntil, DateTime.UtcNow);
}
