using System.ComponentModel.DataAnnotations.Schema;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Data.Entities.Factions;

/// <summary>A faction (gang/mafia/corporation) as a full case. Bundles master data, stocks, ranks and members; audited and soft-deletable. Conflicts to other factions/parties run through the generic linking engine.</summary>
[Table("Fraktionen")]
public class Faction : IAuditable, ISoftDelete, IClassifiableRecord, IArchivable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human-readable unique case number (e.g. NOOSE-F-2026-0001).</summary>
    [Column("Aktenzeichen")]
    public string CaseNumber { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [Column("Art")]
    public string? Kind { get; set; }

    [Column("Funk")]
    public string? Radio { get; set; }
    public string? Darkchat { get; set; }
    [Column("Ausstellungszeiten")]
    public string? IssuingTimes { get; set; }

    [Column("Anwesen")]
    public string? Estate { get; set; }

    /// <summary>Recognition colour as a hex code (e.g. #1E88E5).</summary>
    [Column("Erkennungsfarbe")]
    public string? RecognitionColor { get; set; }

    [Column("Ziele")]
    public string? Targets { get; set; }
    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("Einstufung")]
    public Classification Classification { get; set; } = Classification.Unknown;

    /// <summary>Automatic threat score (0-100, null = not yet rated or exempt, e.g. state faction). The danger level is derived from it on read (EHK score, see NOOSE-Doc/AlgoPlan.md).</summary>
    [Column("BedrohungsScore")]
    public int? ThreatScore { get; set; }

    /// <summary>Data confidence (0-100, null = not rated); a gap never lowers the score, only confidence. Always show the score with a confidence badge.</summary>
    [Column("BedrohungsKonfidenz")]
    public int? ThreatConfidence { get; set; }

    /// <summary>Structured breakdown of the last score run as JSON, for the "why this score?" display. Produced in the same run as the score.</summary>
    [Column("BedrohungsDetailJson")]
    public string? ThreatDetailJson { get; set; }

    /// <summary>Last score computation (UTC); null = never. Lets the nightly sweep detect decay drift without skewing the freshness signal (ModifiedAt).</summary>
    [Column("ScoreBerechnetAm")]
    public DateTime? ScoreCalculatedAt { get; set; }

    /// <summary>Restricted from general view; true when any secrecy level (leadership/TRU/HRB) is set.</summary>
    [Column("IstVerschlusssache")]
    public bool IsClassified { get; set; }

    /// <summary>Restricted to TRU (implies IsClassified); selects the audience instead of leadership-only.</summary>
    [Column("IstVerschlusssacheTRU")]
    public bool IsTRUClassified { get; set; }

    /// <summary>Restricted to HRB (implies IsClassified); selects the audience instead of leadership-only.</summary>
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

    /// <summary>Any secrecy level set (drives the lock indicator).</summary>
    [NotMapped]
    public bool IsRestricted => IsClassified || IsTRUClassified || IsHRBClassified;

    /// <summary>State faction: never goes stale (freshness signal stays current).</summary>
    [Column("IstStaatsfraktion")]
    public bool IsStateFaction { get; set; }

    /// <summary>Aging disabled: record never goes stale.</summary>
    [Column("VeralterungDeaktiviert")]
    public bool AgingDisabled { get; set; }

    /// <summary>Members last refreshed (UTC); null = never since creation. Only the four facet stamps drive the freshness light, not ModifiedAt (see <see cref="Services.FactionRecency"/>).</summary>
    [Column("MitgliederAktualisiertAm")]
    public DateTime? MembersRefreshedAt { get; set; }

    /// <summary>Stocks (weapons/inventory/drug routes) last refreshed (UTC); null = never since creation.</summary>
    [Column("BestaendeAktualisiertAm")]
    public DateTime? StockRefreshedAt { get; set; }

    /// <summary>Linked activities last refreshed (UTC); null = never since creation.</summary>
    [Column("AktivitaetenAktualisiertAm")]
    public DateTime? ActivitiesRefreshedAt { get; set; }

    /// <summary>Linked person docs last refreshed (UTC); null = never since creation.</summary>
    [Column("DoksAktualisiertAm")]
    public DateTime? DocsRefreshedAt { get; set; }

    /// <summary>Estimated total size (= y in the x/y capture progress); optional.</summary>
    [Column("GeschaetzteMitgliederzahl")]
    public int? EstimatedMemberCount { get; set; }

    public List<FactionRank> Ranks { get; set; } = new();
    public List<FactionWeaponStock> WeaponStock { get; set; } = new();
    public List<FactionInventory> Inventory { get; set; } = new();
    public List<FactionDrugRoute> DrugRoutes { get; set; } = new();
    public List<FactionMember> Members { get; set; } = new();
    public List<FactionAgent> Agents { get; set; } = new();

    /// <summary>Faction photos; one may be marked as the title image.</summary>
    public List<FactionPhoto> Photos { get; set; } = new();

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
}
